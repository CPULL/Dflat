using System.Text;
using dfparser;

namespace dfcompiler;

// Strings behave as values inside a statement (spec 2.5, 4): the parts of a call, of an operator, of an interpolated string and of a literal
// run from left to right. A text read from a place that a part after it can change takes its copy (f(k.label, k.change())), and so does a loop
// variable when the body can change its collection.
public sealed partial class CodeGen {
  readonly HashSet<Node> strCopyReads = new(ReferenceEqualityComparer.Instance);   // the reads that take a copy of the text
  readonly HashSet<Node> strPartsChecked = new(ReferenceEqualityComparer.Instance);

  // the parts of an expression in the order they run; the ones that read a text that a later part can change are noted
  void NoteStrCopyReads(Node n, Scope scope) {
    if (LibraryCode || fn == null || programNode == null || n.Line >= LibraryLine
        || n.Kind is not ("Call" or "CtorChain" or "Binary" or "Interp" or "Tuple" or "List" or "MapLit")
        || n.Kind == "Binary" && n.Text is "&&" or "||" or "^^" or "??") {
      return;
    }
    var parts = new List<Node>();
    switch (n.Kind) {
      case "Call" or "CtorChain":
        if (n.Children.Count > 0 && n.Children[0] is { Kind: "Member", Children.Count: > 0 } callee && callee.Text is not ("clear" or "shrink")) {
          parts.Add(callee.Children[0]);   // k.label.indexOf(k.change())
        }
        foreach (var arg in n.Children.Where(c => c.Kind == "Args").SelectMany(c => c.Children)) {
          parts.Add(arg.Kind == "Named" && arg.Children.Count > 0 ? arg.Children[^1] : arg);
        }
        break;
      case "Interp":
        parts.AddRange(n.Children.Where(c => c.Kind != "Text" && c.Children.Count > 0).Select(c => c.Children[0]));
        break;
      case "MapLit":
        parts.AddRange(n.Children.SelectMany(entry => entry.Children));
        break;
      default:
        parts.AddRange(n.Children);
        break;
    }
    if (parts.Count < 2 || !parts.Any(part => part.Kind is not "Name" && !IsLiteralNode(part)) || !strPartsChecked.Add(n)) {
      return;
    }
    // arguments given by name run in the order of the parameters: any other one can come after
    bool named = n.Children.Any(c => c.Kind == "Args" && c.Children.Any(arg => arg.Kind == "Named"));
    for (int index = 0; index < parts.Count; index++) {
      var later = parts.Where((_, other) => other > index || named && other != index).Select(LaterPart).ToList();
      if (later.Count > 0 && IsTextRead(parts[index]) && StrChangedBy(parts[index], later, scope, false)) {
        strCopyReads.Add(parts[index]);
      }
    }
  }

  static bool IsTextRead(Node part) =>
    part.Kind is "Name" or "Member" or "Index" or "Paren" or "SafeMember" or "SwitchExpr" or "BoolSwitchExpr" || part.Kind == "Binary" && part.Text == "??";

  // &a as an argument changes a only inside the call, which takes care of its own parameters
  static Node LaterPart(Node part) => part.Kind == "WritableAddressOf" ? part.Children[0] : part;

  // the other targets of k.label, x = "n", k.label that end their old text when they are assigned, as assignments
  static IEnumerable<Node> ReplacedTargets(List<Node> targets, int index, Scope scope) =>
    targets.Where((target, other) => other != index && (target.Kind is "Member" or "Index" || target.Kind == "Name" && scope.Find(target.Text!)?.OwnFlag == null))
      .Select(target => Synthetic("Assign", target).Add(target).Add(Synthetic("Null", target)));

  // the text that a noted read gives: a copy, which ends with the statement
  Expr StrCopyRead(Node n, Expr e) {
    if (e.Fresh || IsStaticStr(e.Code) || !strCopyReads.Contains(n)) {
      return e;
    }
    if (e.Type == "string") {
      return e with { Code = StrRead(new Expr($"df_str_dup({e.Code})", "string", true)) };
    }
    if (e.Type == "string?") {
      fn!.UsesTmp = true;
      return e with { Code = $"df_opt_str_tmp(df_opt_str_dup({e.Code}))" };
    }
    return e;
  }

  // the text read (or the items of the collection read) can be changed by one of these nodes. blocks: their blocks count too (the body of a
  // loop); any: what is read comes from a custom collection or from a call, so every change of a text counts
  bool StrChangedBy(Node read, IEnumerable<Node> later, Scope scope, bool blocks, bool any = false) {
    CollectBodies();
    var changed = new HashSet<string>();
    var writes = new HashSet<string>();
    var receivers = new HashSet<string>();
    bool unknown = false;
    bool userCall = false;
    foreach (var node in later) {
      // in the blocks the variables declared there are not in scope: a member is judged by its name
      CollectStrChanges(node, changed, blocks ? null : (receiver, member) => StrReceiverChanges(receiver, member, scope), blocks);
      unknown |= CalledNames(node, name => {
        var effect = CallEffect(name);
        writes.UnionWith(effect.Writes);
        unknown |= effect.Unknown;
        userCall |= bodiesByName.ContainsKey(name);
      }, blocks);
      unknown |= InTaskRunBody && ContainsWait(node);
      ChangingReceivers(node, receivers, blocks);
    }
    if (changed.Count == 0 && writes.Count == 0 && receivers.Count == 0 && !unknown && !userCall) {
      return false;
    }
    var borrows = CurrentStrBorrows();
    var lenders = new HashSet<string>();
    StrLenderPaths(read, lenders);
    if (lenders.Count == 0 && any) {
      lenders.UnionWith(LenderPaths(read, scope).Select(StrPath));
    }
    void Aliases(HashSet<string> paths) {
      foreach (string path in paths.ToList()) {
        string root = path.Split('.')[0];
        // a string variable that borrows takes its own copy before the statement (EmitStrDetaches)
        if (borrows.Lenders.TryGetValue(root, out var sources) && scope.Find(root) is not { Type: "string" }) {
          paths.UnionWith(sources.Select(source => source + path[root.Length..]));
        }
      }
    }
    Aliases(lenders);
    Aliases(changed);
    if (any && (writes.Count > 0 || unknown || changed.Count > 0)) {
      return true;
    }
    bool Through(string path) => scope.Find(path.Split('.')[0]) is VarInfo held && (held.LendPointer != null || held.RefSlot && !held.IsParam);
    bool Local(string path) => scope.Find(path.Split('.')[0]) is { IsLocal: true, IsParam: false };
    bool through = changed.Any(Through);
    bool elsewhere = through || unknown || writes.Count > 0
      || changed.Any(path => !Local(path) && (scope.Find(path.Split('.')[0]) is not { IsParam: true } p || p.RefSlot || p.Type != "string"));
    return lenders.Any(lender => {
      string root = lender.Split('.')[0];
      var variable = scope.Find(root);
      if (through || receivers.Contains(root) || changed.Any(path => StrTouches(path, lender))) {
        return true;
      }
      if (variable is { IsCounted: true } && (userCall || unknown)) {
        return true;
      }
      if (variable is { IsParam: true, Type: "string", RefSlot: false }) {
        return blocks && elsewhere;   // before a statement, a parameter takes its own copy
      }
      if (variable is { IsParam: true } && root != "this" || variable is { RefSlot: true }) {
        return elsewhere;   // what is lent can be reached from outside too
      }
      if (variable is { IsLocal: true } && !lender.Contains('.')) {
        return unknown && borrows.Eager.Any(path => StrTouches(path, lender));   // a lambda that changes it
      }
      return unknown || lender.Split('.').Any(writes.Contains);
    });
  }

  // the body of a for loop can change the collection, and then it uses the loop variable
  bool LoopTextChanged(Node source, Expr collection, List<Node> statements, string? name, Scope scope, bool custom) {
    if (LibraryCode || fn == null || programNode == null || source.Line >= LibraryLine || collection.Fresh) {
      return false;
    }
    bool any = custom || source.Kind == "Call";
    for (int index = 0; index < statements.Count; index++) {
      if (name != null && !statements.Skip(index).Any(statement => Mentions(statement, name))) {
        return false;
      }
      if (StrChangedBy(source, new[] { statements[index] }, scope, true, any)) {
        return true;
      }
    }
    return false;
  }
}
