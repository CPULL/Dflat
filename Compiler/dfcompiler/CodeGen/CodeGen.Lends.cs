using dfparser;

namespace dfcompiler;

// Lends (spec 12, 13.1.1): a local that borrows an object, a string or an array keeps what it borrows from locked until its last use, and a
// plain result lends only from what outlives the function (this, a parameter, static data). Checked inside one function, by statement order.
public sealed partial class CodeGen {
  // every declaration of a block and the statements that follow it in the same block
  readonly Dictionary<Node, List<Node>> followingStatements = new();

  void RecordFollowingStatements(List<Node> statements) {
    for (int index = 0; index < statements.Count; index++) {
      if (statements[index].Kind == "Decl") {
        followingStatements[statements[index]] = statements.Skip(index + 1).ToList();
      }
    }
  }

  // a.b.c for a name or a chain of fields; null for anything else
  static string? LendPath(Node node) => node.Kind switch {
    "Name" => node.Text,
    "Member" when node.Children.Count > 0 && LendPath(node.Children[0]) is string root => root + "." + node.Text,
    "Paren" => LendPath(node.Children[0]),
    _ => null
  };

  // what a borrowed value comes from: the place itself, the collection of an item, or the receiver and the lent arguments of a call
  List<string> LenderPaths(Node value, Scope scope) {
    var paths = new List<string>();
    switch (value.Kind) {
      case "Name" or "Member" or "Paren":
        if (LendPath(value) is string path) {
          paths.Add(path);
        }
        break;
      case "Index":
        paths.AddRange(LenderPaths(value.Children[0], scope));
        break;
      case "Call":
        if (value.Children[0] is { Kind: "Member" } callee) {
          paths.AddRange(LenderPaths(callee.Children[0], scope));
        }
        if (value.Children.Count > 1) {
          foreach (var raw in value.Children[1].Children) {
            var arg = raw.Kind == "Named" && raw.Children.Count > 0 ? raw.Children[^1] : raw;
            if (arg.Kind is "Name" or "Member" && LendPath(arg) is string argPath && PlaceType(arg, scope) is string argType
                && (IsClass(argType) || argType == "string" || IsArr(argType))) {
              paths.Add(argPath);
            }
          }
        }
        break;
    }
    return paths.Distinct().ToList();
  }

  // the value of a declaration is borrowed (a place, or a call with a plain result), not a new one
  bool DeclBorrows(Node init, string type, Scope scope) => init.Kind switch {
    "Name" or "Member" or "Index" => true,
    "Call" => !IsNewResult(init, type, scope),
    _ => false
  };

  // Dog d = k.leader(): until the last use of d, k (and k.boss) cannot be replaced, released, moved or handed out with & (spec 12). Strings
  // keep their identity when changed in place and a borrow sees the change (spec 4.1, 4.4): they are not locked here
  void CheckBorrowLock(Node decl, string type, Node? init, VarInfo variable, Scope scope) {
    if (LibraryCode || init == null || !(IsClass(type) || type == "string" || IsArr(type)) || !DeclBorrows(init, type, scope)) {
      return;
    }
    var lenders = LenderPaths(init, scope);
    if (lenders.Select(path => path.Split('.')[0]).FirstOrDefault(root => LocalLender(root, scope)) is string local) {
      variable.LentFromLocal = scope.Find(local)?.LentFromLocal ?? local;
    }
    if (type == "string" || lenders.Count == 0 || !followingStatements.TryGetValue(decl, out var after)) {
      return;
    }
    // Dog f = pack[0]: replacing an item of pack ends the object f borrows; a view or an array item of plain values is not ended (spec 6.4)
    bool items = init.Kind == "Index" && !(init.Children.Count > 1 && init.Children[1].Kind == "Range") && IsClass(type);
    int last = -1;
    for (int index = 0; index < after.Count; index++) {
      if (Mentions(after[index], decl.Text!)) {
        last = index;
      }
    }
    for (int index = 0; index < last; index++) {
      if (FindRelease(after[index], lenders, items) is (Node at, string path)) {
        throw Error($"'{path}' is lent to '{decl.Text}' until its last use: until then it and its fields cannot be assigned, ended, moved or passed as & (spec 12)", at);
      }
    }
  }

  // the first place of the statement that replaces, releases, moves or hands out one of the lenders (or what holds it), or writes one of its
  // fields: while a borrow is alive its lender is frozen, as in Rust (spec 12)
  static (Node, string)? FindRelease(Node node, List<string> lenders, bool items) {
    if (node.Kind is "Fun" or "Lambda") {
      return null;
    }
    string? Touched(Node target) {
      var place = target.Kind == "Index" ? target.Children[0] : target;
      if (LendPath(place) is not string path) {
        return null;
      }
      // k.boss = x, k.dogs[0] = x, k.count++ while something borrows from k: a field of the lender is written
      if (lenders.FirstOrDefault(lender => path.StartsWith(lender + ".")) is string owner) {
        return owner;
      }
      if (target.Kind == "Index" && !items) {
        return null;   // pack[1] = x while a view of pack is alive: allowed (spec 6.4)
      }
      return lenders.FirstOrDefault(lender => lender == path || lender.StartsWith(path + "."));
    }
    switch (node.Kind) {
      case "Assign" when node.Children.Count > 0 && Touched(node.Children[0]) is string assigned:
        return (node, assigned);
      case "Finalize" or "Gift" or "WritableAddressOf" or "PreInc" or "PreDec" or "PostInc" or "PostDec"
        when node.Children.Count > 0 && Touched(node.Children[0]) is string released:
        return (node, released);
      case "MultiAssign" when node.Children[0].Children.FirstOrDefault(target => target.Kind != "Decl" && Touched(target) != null) is Node multi:
        return (node, Touched(multi)!);
    }
    foreach (var child in node.Children) {
      if (FindRelease(child, lenders, items) is (Node, string) found) {
        return found;
      }
    }
    return null;
  }

  // a local of the function (not a parameter, not this, not a global) that owns its value, or borrows from such a local
  bool LocalLender(string root, Scope scope) =>
    root != "this" && scope.Find(root) is VarInfo held && held.IsLocal
    && (held.Own || held.OwnsArray || held.OwnFlag != null && !held.ReadOnlyString || held.LentFromLocal != null);

  // return x with a plain result: it lends, so x must outlive the function (spec 13.1.1)
  void CheckLentResult(Node value, string type, Scope scope) {
    if (LibraryCode || !(IsClass(type) || type == "string" || IsArr(type))) {
      return;
    }
    var paths = value.Kind == "Call" ? LenderPaths(value, scope)
      : LendPath(value.Kind == "Index" ? value.Children[0] : value) is string path ? new List<string> { path } : new List<string>();
    if (paths.Select(lender => lender.Split('.')[0]).FirstOrDefault(root => LocalLender(root, scope)) is string root) {
      string local = scope.Find(root)?.LentFromLocal ?? root;
      throw Error($"this result lends from '{local}', which ends with the function: give the caller its own value with fun ->{DisplayName(type)} (spec 13.1.1)", value);
    }
  }

  // return x with a &T result: the caller writes x in place, so x is something this function can write: this and its fields, a & parameter,
  // a global that is not const, or the &T result of another call (spec 13.1.1)
  void CheckWritableLend(Node value, string type, Scope scope) {
    if (LibraryCode) {
      return;
    }
    if (value.Kind == "Call") {
      if (CalleesOf(value) is { Count: > 0 } callees && callees.All(f => f.Writes.Contains(0))) {
        return;
      }
      throw Error("a & result lends what the function can write: this result is lent read-only by the call; return the place, or give a value with ->", value);
    }
    var place = value.Kind == "Index" ? value.Children[0] : value;
    if (LendPath(place) is not string path) {
      throw Error("a & result lends a place the function can write: this, a field, a & parameter or a global", value);
    }
    string root = path.Split('.')[0];
    if (root != "this" && scope.Find(root) is VarInfo held) {
      if (held.IsConst) {
        throw Error($"'{root}' is const: a & result cannot lend it (spec 13.1.1)", value);
      }
      if (held.IsParam && !held.RefSlot) {
        throw Error($"'{root}' is lent read-only to this function: a & result lends only what the function can write (this, its fields, a & parameter) (spec 13.1.1)", value);
      }
    }
  }
}
