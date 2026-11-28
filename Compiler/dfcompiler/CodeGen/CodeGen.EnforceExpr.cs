using dfparser;

namespace dfcompiler;

// The expression form of enforce (spec 7.2.6): T r = enforce E clauses is lowered, before everything else, to the statement form
//   T r
//   enforce <the variables of E> { r = E } clauses
// where each clause gives its value to r: a single expression is the value, a longer block gives it with return (the return becomes
// r = value and leaves the clause). return enforce E ... (and an enforce expression that is the single expression of a function body) goes through a
// variable of the return type. The variables of E that cannot be checked (functions, types, plain values) are skipped by EmitEnforce.
public sealed partial class CodeGen {
  int enforceTemps;

  void LowerEnforceExprs(Node node, Node? fun) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child.Kind == "Fun") {
        LowerEnforceExprs(child, child);
        continue;
      }
      if (child.Kind == "Lambda") {
        continue;
      }
      if (child.Kind is "Decl" or "MultiAssign" && child.Children.FirstOrDefault(c => c.Kind == "AtEndOf" && c.Text == "decl") is Node own) {
        child.Children.Remove(own);   // Some a = Some() atEnd { } is a declaration followed by atEnd of a (spec 14.2)
        node.Children.Insert(index + 1, own);
      }
      LowerEnforceHeader(child);
      var lowered = LowerEnforceStatement(child, node, index, fun);
      if (lowered != null) {
        node.Children.RemoveAt(index);
        node.Children.InsertRange(index, lowered);
        child = node.Children[index];
      }
      LowerEnforceExprs(child, fun);
    }
  }

  List<Node>? LowerEnforceStatement(Node statement, Node holder, int index, Node? fun) {
    if (statement.Kind == "Decl" && statement.Children.FirstOrDefault(child => child.Kind == "Init") is Node init && init.Children[0].Kind == "EnforceExpr") {
      if (statement.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { Kind: "Type", Text: "var" }) {
        return HoistEnforceExpr(statement);   // var r = enforce ...: the type comes from the expression
      }
      statement.Children.Remove(init);
      return new List<Node> { statement, EnforceStatementOf(init.Children[0], statement.Text!) };
    }
    bool lastOfFunction = fun != null && holder.Kind == "Block" && fun.Children.Contains(holder) && index == holder.Children.Count - 1;
    bool returns = statement.Kind == "Return" && statement.Children.Count == 1 && statement.Children[0].Kind == "EnforceExpr";
    bool lastExpression = lastOfFunction && holder.Children.Count == 1 && statement.Kind == "ExprStmt" && statement.Children[0].Kind == "EnforceExpr";   // the single expression of the body
    if (returns || lastExpression) {
      var rets = fun?.Children.FirstOrDefault(child => child.Kind == "Returns");
      if (rets == null || rets.Children.Count != 1) {
        throw Error("an enforce expression is returned only by a function that returns one value", statement);
      }
      string name = $"enforced{++enforceTemps}";
      var decl = Synthetic("Decl", statement, name).Add(rets.Children[0]);
      return new List<Node> {
        decl,
        EnforceStatementOf(statement.Children[0], name),
        Synthetic("Return", statement).Add(Synthetic("Name", statement, name))
      };
    }
    if (statement.Kind is "ExprStmt" or "Decl" or "Assign" or "Return" or "MultiAssign" && ContainsEnforceExpr(statement)) {
      return HoistEnforceExpr(statement);
    }
    if (statement.Kind is "If" or "Switch" && holder.Kind is "Block" or "Program" && ContainsEnforceExpr(statement.Children[0])) {
      return HoistEnforceExpr(statement);   // if enforce f.ok() default { false } { }: evaluated once, before the if
    }
    if (statement.Kind == "For" && holder.Kind is "Block" or "Program" && statement.Children.Any(child => child.Kind != "Block" && ContainsEnforceExpr(child))) {
      return HoistEnforceExpr(statement);   // for x in enforce f.items() default { [] } { }: the header is evaluated once, before the loop
    }
    if (statement.Kind != "Fun" && ContainsEnforceExpr(statement)) {
      throw NotSupported("An enforce expression here (in a loop header or an else if: give it to a variable first)", statement);
    }
    return null;
  }

  // the headers that are evaluated more than once, or after other conditions (spec 7.2.6):
  //   else if <enforce> { }        ->  else { if <enforce> { } }             (evaluated only when the conditions before it failed)
  //   while <enforce> { body }     ->  while true { if !(<enforce>) { break } body }   (evaluated at every turn, also after continue)
  static void LowerEnforceHeader(Node statement) {
    if (statement.Kind == "If" && statement.Children.Count > 2 && statement.Children[2] is { Kind: "Else", Children: [{ Kind: "If" } elseIf] } elseNode
        && ContainsEnforceExpr(elseIf.Children[0])) {
      elseNode.Children[0] = Synthetic("Block", elseIf).Add(elseIf);
    }
    if (statement.Kind == "While" && statement.Children.Count == 2 && ContainsEnforceExpr(statement.Children[0])) {
      var condition = statement.Children[0];
      var stop = Synthetic("If", condition).Add(Synthetic("Not", condition).Add(condition))
        .Add(Synthetic("Block", condition).Add(Synthetic("Break", condition)));
      statement.Children[1].Children.Insert(0, stop);
      statement.Children[0] = Synthetic("Bool", condition, "true");
    }
  }

  // x = enforce ..., f(enforce ...), var r = enforce ...: the enforce expression moves before its statement into a variable that starts at
  // the zero value of its type (what it gives when nothing handles a failure), and the statement reads that variable
  List<Node> HoistEnforceExpr(Node statement) {
    var found = FindEnforceExpr(statement)!.Value;
    string name = $"enforced{++enforceTemps}";
    var (holder, at) = found;
    var enforceExpr = holder.Children[at];
    holder.Children[at] = Synthetic("Name", enforceExpr, name);
    var decl = Synthetic("Decl", enforceExpr, name).Add(Synthetic("Type", enforceExpr, "var"))
      .Add(Synthetic("Init", enforceExpr).Add(Synthetic("ZeroLike", enforceExpr).Add(enforceExpr.Children[0])));
    return new List<Node> { decl, EnforceStatementOf(enforceExpr, name), statement };
  }

  static (Node Holder, int Index)? FindEnforceExpr(Node node) {
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (child.Kind == "EnforceExpr") {
        return (node, index);
      }
      if (child.Kind is "Block" or "Fun" or "Lambda") {
        continue;
      }
      if (FindEnforceExpr(child) is { } inner) {
        return inner;
      }
    }
    return null;
  }

  // the zero or null value of the type of an expression (ZeroLike, made by HoistEnforceExpr)
  Expr ZeroLike(Node node, Scope scope) {
    string type;
    typingOnly = true;   // only the type is wanted: a &@string may be named outside its enforce here (spec 12.7)
    try {
      type = ExprOf(node.Children[0], scope).Type;
    } finally {
      typingOnly = false;
    }
    if (type is "void" or "null") {
      throw Error("an enforce expression must give a value", node);
    }
    // an object starts null: nothing to own yet, the variable owns what the enforce gives it (EnforcedGivesObject)
    return new Expr(ZeroValue(type), type, IsClass(type) && EnforcedGivesObject(node));
  }

  static bool ContainsEnforceExpr(Node node) =>
    node.Kind == "EnforceExpr" || node.Kind is not ("Block" or "Fun" or "Lambda") && node.Children.Any(ContainsEnforceExpr);

  Node EnforceStatementOf(Node enforceExpr, string target) {
    var expression = enforceExpr.Children[0];
    var enforce = Synthetic("Enforce", enforceExpr, "expr");
    var seen = new HashSet<string>();
    var sharedTimeout = enforceExpr.Children.Skip(1).FirstOrDefault(child => child.Kind == "Timeout");
    foreach (var name in VariablesOf(expression)) {
      if (seen.Add(name.Text!)) {
        var enforced = Synthetic("Name", name, name.Text);
        if (sharedTimeout != null) {
          enforced.Add(sharedTimeout);   // the wait of every lock of the expression; the variables with no lock ignore it (spec 7.2.6)
        }
        enforce.Add(enforced);
      }
    }
    if (enforce.Children.Count == 0) {
      throw Error("an enforce expression needs at least one variable to check", enforceExpr);
    }
    enforce.Add(Synthetic("Block", enforceExpr).Add(AssignTo(target, expression)));
    foreach (var clause in enforceExpr.Children.Skip(1)) {
      if (clause.Kind == "Timeout") {
        continue;
      }
      if (clause.Kind == "AtEnd") {
        enforce.Add(clause);
        continue;
      }
      int at = clause.Children.FindIndex(child => child.Kind == "Block");
      clause.Children[at] = ValueBlock(clause.Children[at], target);
      enforce.Add(clause);
    }
    return enforce;
  }

  Node AssignTo(string target, Node value) => Synthetic("Assign", value, "=").Add(Synthetic("Name", value, target)).Add(value);

  // the names of E that the enforce checks: variables, not the functions called by name nor the parts after a dot
  static IEnumerable<Node> VariablesOf(Node node) {
    if (node.Kind == "Name") {
      yield return node;
      yield break;
    }
    if (node.Kind is "Lambda" or "EnforceExpr") {
      yield break;
    }
    for (int index = 0; index < node.Children.Count; index++) {
      var child = node.Children[index];
      if (node.Kind == "Call" && index == 0 && child.Kind == "Name") {
        continue;   // f(x): f is a function
      }
      foreach (var inner in VariablesOf(child)) {
        yield return inner;
      }
    }
  }

  // the value of a clause: { v } is r = v; a longer block gives the value with return, which becomes r = v and leaves the clause
  // a clause that gives the value: return v assigns the target and leaves the clause, also from a loop inside it (spec 7.2.6)
  Node ValueBlock(Node block, string target) =>
    WaitValueBlock(block, new List<string> { target }, "a clause of an enforce expression gives its value with return value");
}
