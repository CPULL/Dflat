using System.Text;
using dfparser;

namespace dfcompiler;

// Strings behave as values (spec 4): b = a borrows the text of a, and a change to one is never seen through the other. A change through b
// makes its copy first (StrMine). A change of a, or the end of what holds the text (d = null, v.clear()), first gives their own copy to the
// borrowers that are used later: the statement that changes it starts with their df_str_mine (EmitStrDetaches). What borrows from what is
// found by name in the whole function (a loop can come back); where a change cannot be placed before a statement (the condition of a loop,
// a lambda), the borrower copies at once (StrKept).
public sealed partial class CodeGen {
  sealed class StrBorrows {
    public Dictionary<string, HashSet<string>> Lenders { get; } = new();   // a name -> the paths its text can come from
    public HashSet<string> Eager { get; } = new();                         // the paths changed where nothing can be placed before
  }

  Node? programNode;
  readonly Dictionary<Node, StrBorrows> strBorrows = new(ReferenceEqualityComparer.Instance);

  StrBorrows CurrentStrBorrows() {
    var body = currentFun?.Node ?? programNode!;
    if (strBorrows.TryGetValue(body, out var known)) {
      return known;
    }
    var made = new StrBorrows();
    strBorrows[body] = made;
    foreach (var child in body.Children) {
      if (body.Kind != "Program" || child.Kind is not ("Fun" or "Class" or "Trait" or "Struct" or "Enum")) {
        CollectStrBorrows(child, made);
      }
    }
    // c = b, b = a: c can hold the text of a; D e = d: the text of e.label is the one of d.label
    for (bool grown = true; grown;) {
      grown = false;
      foreach (var lenders in made.Lenders.Values) {
        foreach (var path in lenders.ToList()) {
          string root = path.Split('.')[0];
          if (path.Count(c => c == '.') < 4 && made.Lenders.TryGetValue(root, out var further)) {
            foreach (var more in further.ToList()) {
              grown |= lenders.Add(more + path[root.Length..]);
            }
          }
        }
      }
    }
    return made;
  }

  static string StrPath(string path) => path.StartsWith("this.") ? path["this.".Length..] : path;

  // the paths a string value can be borrowed from: names, fields, the collection of an item; a call and a + give a new string
  static void StrLenderPaths(Node value, HashSet<string> paths) {
    switch (value.Kind) {
      case "Call" or "Lambda" or "Binary" when value.Kind != "Binary" || value.Text != "??":
        return;
      case "Name" or "Member" or "Index" or "Paren":
        if (LendPath(value) is string path) {
          paths.Add(StrPath(path));
        } else if (value.Children.Count > 0) {
          StrLenderPaths(value.Children[0], paths);
        }
        return;
    }
    foreach (var child in value.Children) {
      StrLenderPaths(child, paths);
    }
  }

  static void CollectStrBorrows(Node node, StrBorrows found) {
    void Add(string name, Node value) {
      if (!found.Lenders.TryGetValue(name, out var paths)) {
        found.Lenders[name] = paths = new HashSet<string>();
      }
      StrLenderPaths(value, paths);
    }
    switch (node.Kind) {
      case "Decl" when node.Children.FirstOrDefault(c => c.Kind == "Init") is { Children.Count: > 0 } init:
        Add(node.Text!, init.Children[0]);
        break;
      case "Assign" when node.Children[0].Kind == "Name":
        Add(node.Children[0].Text!, node.Children[1]);
        break;
      case "MultiAssign" when node.Children.Count > 1:
        foreach (var target in node.Children[0].Children.Where(t => t.Kind is "Name" or "Decl")) {
          foreach (var value in node.Children[1].Children) {
            Add(target.Text!, value);
          }
        }
        break;
      case "While":
        CollectStrChanges(node.Children[0], found.Eager, null);
        break;
      case "DoWhile":
        CollectStrChanges(node.Children[1], found.Eager, null);
        break;
      case "For":
        if (node.Children.Count > 2 && node.Children[1] is { Kind: "In", Children.Count: > 0 } walked) {
          // the items of the collection: for string s in v, for (a, b) in v
          foreach (var item in node.Children[0].Kind == "Destructure" ? node.Children[0].Children : new List<Node> { node.Children[0] }) {
            if (item.Kind != "WritableVar" && item.Text != null) {
              Add(item.Text, walked.Children[0]);
            }
          }
        }
        foreach (var header in node.Children.Where(c => c.Kind is not "Block")) {
          CollectStrChanges(header, found.Eager, null);
        }
        break;
      case "Lambda":
        CollectStrChanges(node, found.Eager, null, true);
        foreach (var capture in node.Children.Where(c => c.Kind == "Captures").SelectMany(c => c.Children).Where(c => c.Kind == "WritableCapture")) {
          found.Eager.Add(capture.Text!);
        }
        break;
    }
    foreach (var child in node.Children) {
      CollectStrBorrows(child, found);
    }
  }

  // the paths that a statement (without its blocks) changes, replaces, gives away or ends. changing: whether a call of that member on that
  // receiver changes it (null: any member that changes a string or a collection, by its name)
  static void CollectStrChanges(Node node, HashSet<string> paths, Func<Node, string, bool>? changing, bool blocks = false) {
    void Touch(Node target) {
      while (target.Kind is "Index" or "Paren") {
        target = target.Children[0];
      }
      if (LendPath(target) is string path) {
        paths.Add(StrPath(path));
      } else if (target.Kind == "Member") {
        Touch(target.Children[0]);   // v[0].name = x: something v holds
      }
    }
    switch (node.Kind) {
      case "Assign" or "Finalize" or "Gift" or "WritableAddressOf" or "PreInc" or "PreDec" or "PostInc" or "PostDec" when node.Children.Count > 0:
        Touch(node.Children[0]);
        break;
      case "MultiAssign":
        foreach (var target in node.Children[0].Children.Where(t => t.Kind != "Decl" && t.Kind != "Discard")) {
          Touch(target);
        }
        break;
      case "Decl" when node.Children.Any(c => c.Kind == "ReadWrite") && node.Children.FirstOrDefault(c => c.Kind == "Init") is { Children.Count: > 0 } init:
        Touch(init.Children[0]);   // &string r = a
        break;
      case "Call" when node.Children[0] is { Kind: "Member" } member:
        if (changing == null ? StrChanging.Contains(member.Text!) || !ReadOnlyCollectionMembers.Contains(member.Text!) : changing(member.Children[0], member.Text!)) {
          Touch(ChangedRoot(node) ?? member.Children[0]);
        }
        break;
    }
    foreach (var child in node.Children) {
      if (blocks || child.Kind is not ("Block" or "Case" or "Catch" or "AtEnd" or "Lambda" or "Fun" or "WaitBranch" or "TimeoutBlock")) {
        CollectStrChanges(child, paths, changing, blocks);
      }
    }
  }

  // path is changed: a text that comes from lender can change or end with it
  static bool StrTouches(string path, string lender) =>
    lender == path || lender.StartsWith(path + ".") || path.StartsWith(lender + ".");

  bool StrReceiverChanges(Node receiver, string member, Scope scope) {
    var root = receiver;
    while (root.Kind == "Call" && root.Children[0].Kind == "Member") {
      root = root.Children[0].Children[0];   // s.trim().toUpper()
    }
    if (PlaceType(root, scope) is not string type) {
      return false;
    }
    if (type == "string") {
      return StrChanging.Contains(member) || member is "clear" or "shrink";
    }
    return (IsArr(type) || IsVec(type) || IsList(type) || IsMap(type) || IsSet(type) || IsView(type))
      && !ReadOnlyCollectionMembers.Contains(member) && !member.StartsWith("get_");
  }

  // the variable is named by this statement or by one that can run after it
  // now: the statement itself counts
  bool StrUsedLater(string name, bool now) {
    bool first = now;
    bool loop = false;
    for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
      if (frame.Kind is FrameKind.AtEndBody && frame.Owner is Frame owner) {
        // the other atEnd blocks of the scope, and what follows it
        if (owner.AtEnds.Any(atEnd => atEnd.Text != "drop" && Mentions(atEnd, name))) {
          return true;
        }
      }
      for (int index = 0; index < frame.Statements.Count; index++) {
        var statement = frame.Statements[index];
        bool later = index > frame.CurrentIndex || index == frame.CurrentIndex && (first || loop);
        if ((later || frame.Labels.Count > 0 || frame.Kind == FrameKind.LoopBody || statement.Kind is "AtEnd" or "Catch") && Mentions(statement, name)) {
          return true;
        }
      }
      loop = frame.Kind == FrameKind.LoopBody;
      first = false;
      if (frame.Kind == FrameKind.Function) {
        break;
      }
    }
    return false;
  }

  // the address of a string variable that owns its text from now on
  static string StrMine(VarInfo variable) => $"df_str_mine(&{variable.CName}, &{variable.OwnFlag})";
}
