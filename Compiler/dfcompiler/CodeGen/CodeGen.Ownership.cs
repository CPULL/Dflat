using System.Text;
using dfparser;

namespace dfcompiler;

// Ownership (spec 12, 12.4, 14.2): one owner per object, freed (its class atEnd runs) at the end of the owner's scope.
//
// What an owner is, decided when a variable is declared (the declaration is the only place that says):
//   T x = new object / function result that moves / ->y   x owns the object (an implicit "x is atEnd." ends its scope)
//   T x = y / y.field / parameter                          x borrows: it is never destroyed by x
//   T x _= y                                               x is a weak reference: used only through enforce
//   fields always own; parameters borrow, unless written ->T x (the callee owns, the call writes ->y)
// A function that returns an object either always moves (a new object, ->x, an owned local) or always borrows
// (a field, a parameter, another variable); the compiler works that out from the returns.
// Destroyed objects are marked dead and keep their memory (nothing is returned to the system yet): a
// borrow or a weak reference to a dead object finds it dead (NullPointerException / enforce fails).
public sealed partial class CodeGen {
  readonly Dictionary<Node, bool> declOwner = new();         // declaration → the variable owns its object

  // ---------------------------------------------------------------- what returns an object that moves

  // an object-returning function returns new or moved objects (true) or borrowed ones (false)
  void ClassifyReturns() {
    var state = new Dictionary<FunSig, int>();                // 1 = busy, 2 = done
    var all = funs.Values.SelectMany(list => list).ToList();
    foreach (var f in all) {
      Classify(f, state);
    }
    // functions that share a virtual slot, and the functions that answer a trait method, must agree
    foreach (var group in all.Where(f => f.Class != null && !f.IsStatic && IsClass(f.Ret) && !f.IsAbstract).GroupBy(f => f.Root)) {
      var values = group.Select(f => f.ReturnsOwned).Distinct().ToList();
      if (values.Count > 1) {
        throw Error($"'{group.Key.Name}' and the functions that override it do not return objects the same way (some move, some borrow)", group.First().Node);
      }
    }
    var abstractAnswers = new Dictionary<FunSig, bool>();
    foreach (var cls in classOrder) {
      foreach (var impl in cls.Impls) {
        for (int index = 0; index < impl.Methods.Count; index++) {
          var method = impl.Trait.TraitMethods[index];
          var answer = impl.Methods[index];
          if (!IsClass(method.Ret) || answer == method) {
            continue;
          }
          bool differs = method.IsAbstract
            ? abstractAnswers.TryGetValue(method, out bool first) && first != answer.ReturnsOwned
            : method.ReturnsOwned != answer.ReturnsOwned;
          if (differs) {
            throw Error($"{cls.Name}.{answer.Name[(answer.Name.IndexOf('.') + 1)..]} and the trait function {method.Name} do not return objects the same way (some move, some borrow)", answer.Node);
          }
          if (method.IsAbstract) {
            abstractAnswers[method] = answer.ReturnsOwned;
            method.ReturnsOwned = answer.ReturnsOwned;
          }
        }
      }
    }
  }

  bool Classify(FunSig f, Dictionary<FunSig, int> state) {
    if (state.TryGetValue(f, out int s)) {
      return s == 1 || f.ReturnsOwned;                       // busy: assume it moves
    }
    state[f] = 1;
    bool? result = null;
    if (f.IsCtor) {
      result = true;
    } else if (IsClass(f.Ret) && f.Node.Children.Any(child => child.Kind == "Block")) {
      var locals = new Dictionary<string, Node>();
      CollectDeclarations(f.Node, locals);
      bool sawOwned = false;
      bool sawBorrowed = false;
      foreach (var returned in CollectReturns(f.Node)) {
        if (returned.Children.Count == 0) {
          continue;
        }
        var kind = ReturnKind(returned.Children[0], locals, state);
        if (kind == true) {
          sawOwned = true;
        } else if (kind == false) {
          sawBorrowed = true;
        }
      }
      if (sawOwned && sawBorrowed) {
        throw Error($"the returns of '{f.Name}' mix moving and borrowing: all of them must move (a new object, ->x, an owned variable) or all must borrow", f.Node);
      }
      result = sawOwned;
    }
    f.ReturnsOwned = result == true;
    f.ReturnsOwnedSet = true;
    state[f] = 2;
    return f.ReturnsOwned;
  }

  static IEnumerable<Node> CollectReturns(Node node) {
    if (node.Kind == "Return") {
      yield return node;
    }
    foreach (var child in node.Children) {
      if (child.Kind is "Fun" or "Lambda") {
        continue;
      }
      foreach (var inner in CollectReturns(child)) {
        yield return inner;
      }
    }
  }

  static void CollectDeclarations(Node node, Dictionary<string, Node> locals) {
    if (node.Kind == "Decl" && node.Text != null) {
      locals.TryAdd(node.Text, node);
    }
    foreach (var child in node.Children) {
      CollectDeclarations(child, locals);
    }
  }

  // true: moves, false: borrows, null: says nothing (null)
  bool? ReturnKind(Node e, Dictionary<string, Node> locals, Dictionary<FunSig, int> state) {
    switch (e.Kind) {
      case "Null":
        return null;
      case "Paren":
        return ReturnKind(e.Children[0], locals, state);
      case "Gift":
        return true;
      case "Call":
        return CallMoves(e, state);
      case "Name":
        return locals.TryGetValue(e.Text!, out var declaration) && GuessOwnerInit(declaration.Children.FirstOrDefault(c => c.Kind is "Init" or "WeakInit"), state);
      default:
        return false;
    }
  }

  // does a call give a new object (a constructor, or a function that returns objects that move)?
  bool CallMoves(Node call, Dictionary<FunSig, int>? state = null) {
    state ??= new Dictionary<FunSig, int>();
    var callee = call.Children[0];
    if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Name" } genericName) {
      return genericClasses.ContainsKey(genericName.Text!)
        || (genericFuns.TryGetValue(genericName.Text!, out var explicitFun) && TemplateReturnsMoves(explicitFun));
    }
    string? simple = callee.Kind is "Name" or "Member" ? callee.Text : null;
    if (simple == null) {
      return false;
    }
    if (callee.Kind == "Name" && genericFuns.TryGetValue(simple, out var inferredFun)) {
      return TemplateReturnsMoves(inferredFun);
    }
    if (callee.Kind == "Name" && classes.ContainsKey(simple)) {
      return true;
    }
    var candidates = funs.Values.SelectMany(list => list)
      .Where(f => !f.IsCtor && !f.IsInit && (f.Name == simple || f.Name.EndsWith("." + simple)))
      .ToList();
    if (callee.Kind == "Member" && Path(callee) is string path && funs.TryGetValue(path, out var exact)) {
      candidates = exact;
    }
    if (candidates.Count == 0 || candidates.Any(f => !IsClass(f.Ret))) {
      return false;
    }
    return candidates.All(f => Classify(f, state));
  }

  // the initializer of a declaration: does the variable own an object?
  bool GuessOwnerInit(Node? init, Dictionary<FunSig, int>? state = null) {
    if (init == null) {
      return true;                                           // no value yet: an owner
    }
    if (init.Kind == "WeakInit") {
      return false;
    }
    return GuessOwner(init.Children[0], state);
  }

  bool GuessOwner(Node e, Dictionary<FunSig, int>? state) {
    return e.Kind switch {
      "Null" or "Gift" => true,
      "Paren" => GuessOwner(e.Children[0], state),
      "Call" => CallMoves(e, state),
      _ => false
    };
  }

  // ---------------------------------------------------------------- implicit drops

  // the class or trait an object variable is declared with (null for anything else)
  string? ObjectTypeOf(Node decl) {
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode == null) {
      return null;
    }
    if (typeNode is { Kind: "Type", Text: "var" or "arr" }) {
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init");
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Call" } call
          && call.Children[0] is { Kind: "Name" } name && classes.ContainsKey(name.Text!)) {
        return name.Text;
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Call" } genericCall && genericCall.Children[0].Kind == "GenericRef") {
        return GenericRefClass(genericCall.Children[0]);   // var s = Stack<i32>()
      }
      return null;
    }
    try {
      string type = ResolveType(typeNode);
      return IsClass(type) ? type : null;
    } catch (CompileException) {
      return null;
    }
  }

  static Node DropNode(string name, Node at) {
    var finalize = Synthetic("Finalize", at).Add(Synthetic("Name", at, name));
    var statement = Synthetic("ExprStmt", at).Add(finalize);
    return Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(statement));
  }

  // the declarations of a scope whose objects end with the scope: an atEnd for each, run after the written ones
  HashSet<Node> AddImplicitDrops(Frame frame, List<Node> statements) {
    var drops = new List<Node>();
    var owners = new HashSet<Node>();
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl")) {
      var mods = statement.Children.FirstOrDefault(child => child.Kind == "Mods");
      if (mods?.Text?.Split(' ').Contains("const") == true || ObjectTypeOf(statement) == null) {
        continue;
      }
      var init = statement.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
      if (!GuessOwnerInit(init)) {
        continue;
      }
      declOwner[statement] = true;
      owners.Add(statement);
      drops.Add(DropNode(statement.Text!, statement));
    }
    frame.AtEnds.InsertRange(0, drops);
    return owners;
  }

  // a loop body that declares objects: each pass is a block of its own, so its objects end with the pass
  List<Node> LoopStatements(List<Node> statements) {
    if (statements.Count == 0 || statements.Any(s => s.Kind is "AtEnd" or "AtEndOf" or "Catch" or "Label")) {
      return statements;
    }
    if (!statements.Any(s => s.Kind == "Decl" && ObjectTypeOf(s) != null)) {
      return statements;
    }
    var wrapper = Synthetic("Block", statements[0]);
    wrapper.Children.AddRange(statements);
    return new List<Node> { wrapper };
  }

  // atEnd of a, b { }: runs when the scope ends (the listed names are not tracked yet)
  static Node NormalizeAtEndOf(Node node) {
    return Synthetic("AtEnd", node).Add(node.Children[^1]);
  }

  // ---------------------------------------------------------------- moves and finalizers

  // ->x and ->a.c
  Expr GiftExpr(Node n, Scope scope) {
    var operand = n.Children[0];
    if (operand.Kind == "Name" && operand.Text == "this") {
      throw Error("an object cannot give away its own ownership: ->this", n);
    }
    if (operand.Kind is not ("Name" or "Member")) {
      throw Error("only a variable or a field can be moved with ->", n);
    }
    var value = ExprOf(operand, scope);
    if (!IsClass(value.Type)) {
      Warn("-> on a basic value is ignored: it is a copy", n, 1);
      return new Expr(value.Code, value.Type);
    }
    var target = LValue(operand, scope);
    CheckMovable(operand, target, scope);
    string slot = NewTemp();
    string taken = NewTemp();
    string code = $"({{ {CType(value.Type)}* {slot} = &({target.Code}); {CType(value.Type)} {taken} = *{slot}; *{slot} = NULL; {taken}; }})";
    return new Expr(code, value.Type, true);
  }

  // only an owner moves its object, and a field only out of an object the code may write
  void CheckMovable(Node operand, LValueInfo target, Scope scope) {
    if (target.IsConst) {
      throw Error("a const cannot be moved", operand);
    }
    if (target.Variable != null) {
      if (target.Variable.IsWeak) {
        throw Error("a weak reference cannot be moved", operand);
      }
      if (!target.Variable.Own) {
        throw Error($"'{operand.Text}' only borrows its object: it cannot be moved", operand);
      }
      return;
    }
    // a field: the object it belongs to must be owned (or be this)
    if (operand.Kind == "Member") {
      var root = operand.Children[0];
      while (root.Kind == "Member") {
        root = root.Children[0];
      }
      if (root.Kind == "Name" && root.Text != "this" && scope.Find(root.Text!) is VarInfo holder && !holder.Own) {
        throw Error($"'{root.Text}' only borrows its object: a field cannot be moved out of it", operand);
      }
    }
  }

  // x is atEnd.
  void EmitFinalize(Node finalize, StringBuilder sb, Scope scope, string pad) {
    var subject = finalize.Children[0];
    var target = LValue(subject, scope);
    if (!IsClass(target.Type)) {
      throw Error("'is atEnd.' ends the life of an object: this is " + target.Type, finalize);
    }
    if (target.IsConst) {
      throw Error("a const cannot be released", finalize);
    }
    if (target.Variable != null) {
      if (target.Variable.IsWeak) {
        throw Error("a weak reference does not own its object: it cannot be released", finalize);
      }
      if (!target.Variable.Own) {
        throw Error($"'{subject.Text}' only borrows its object: 'is atEnd.' on a borrowed variable is not allowed", finalize);
      }
    }
    string slot = NewTemp();
    string taken = NewTemp();
    string code = $"({{ {CType(target.Type)}* {slot} = &({target.Code}); void* {taken} = (void*)*{slot}; *{slot} = NULL; df_drop({taken}); }})";
    sb.Append(pad).Append(CheckRaise(code, "void")).Append(";\n");
  }

  // ---------------------------------------------------------------- assignments

  // target = value, for a variable or field that owns (the old object ends) or borrows
  string OwnedAssign(LValueInfo target, Node targetNode, Expr value, Node at) {
    var variable = target.Variable;
    bool owns = variable != null ? variable.Own : target.IsField;
    if (variable is { IsWeak: true }) {
      throw Error($"'{targetNode.Text}' is a weak reference: assign it with _=", at);
    }
    bool movesIn = value.Fresh || value.Type == "null";
    if (owns && !movesIn) {
      throw Error($"'{targetNode.Text}' owns its object: give it a new object or move one in with ->x", at);
    }
    if (!owns && value.Fresh) {
      throw Error($"'{targetNode.Text}' only borrows: a new object needs an owner (declare the variable with the new object, or assign a field)", at);
    }
    string converted = Convert(value, target.Type, at);
    MarkAssigned(variable);
    if (!owns) {
      return $"{target.Code} = {converted}";
    }
    string slot = NewTemp();
    string fresh = NewTemp();
    string old = NewTemp();
    string typeName = CType(target.Type);
    string code = $"({{ {typeName} {fresh} = {converted}; {typeName}* {slot} = &({target.Code}); {typeName} {old} = *{slot}; *{slot} = {fresh}; "
      + $"if ({old} != {fresh}) {{ df_drop((void*){old}); }} }})";
    return CheckRaise(code, "void");
  }

  // w _= x
  string WeakAssign(LValueInfo target, Node targetNode, Node source, Scope scope, Node at) {
    if (target.Variable is not { IsWeak: true }) {
      throw Error($"'{targetNode.Text}' is not a weak reference: declare it with 'Type name _= value'", at);
    }
    var value = ExprOf(source, scope, target.Type);
    if (value.Fresh) {
      throw Error("a weak reference needs an existing object: nobody would own a new one", at);
    }
    MarkAssigned(target.Variable);
    return $"{target.Code} = {Convert(value, target.Type, at)}";
  }

  // ---------------------------------------------------------------- returns

  // return x: a function that moves returns new objects, ->y and its own owners; one that borrows, anything else
  Expr CheckReturnOwnership(Expr value, Node expression, Scope scope, Node at) {
    if (currentFun == null || !IsClass(currentRet ?? "")) {
      return value;
    }
    bool? moves;
    if (expression.Kind == "Null") {
      return value;
    }
    if (expression.Kind == "Name" && scope.Find(expression.Text!) is VarInfo variable && IsClass(variable.Type) && variable.Own && !variable.IsWeak) {
      string taken = NewTemp();
      value = new Expr($"({{ {CType(variable.Type)} {taken} = {variable.CName}; {variable.CName} = NULL; {taken}; }})", value.Type, true);
      moves = true;
    } else {
      moves = value.Fresh;
    }
    if (currentFun.ReturnsOwned != moves) {
      throw Error(currentFun.ReturnsOwned
        ? $"'{currentFun.Name}' returns new or moved objects; this return gives a borrowed one (write ->x, or return a new object)"
        : $"'{currentFun.Name}' returns borrowed objects; this return gives a new or moved one", at);
    }
    return value;
  }

  // ---------------------------------------------------------------- the destruction code of the classes

  List<FieldInfo> OwnedObjectFields(ClassInfo cls) {
    return IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields)
      .Where(field => !field.IsStatic && IsClass(field.Type)).ToList();
  }

  void EmitDropGlue(ClassInfo cls) {
    var sb = classCode;
    sb.Append($"static void df_dropc_{cls.Name}({cls.StructName}* self) {{\n");
    if (cls.Dtor != null) {
      sb.Append($"  {cls.Dtor.CName}(self);\n");
    }
    foreach (var field in OwnedObjectFields(cls)) {
      sb.Append($"  if (self->{field.CName} != NULL) {{ void* o = (void*)self->{field.CName}; self->{field.CName} = NULL; df_drop(o); }}\n");
    }
    sb.Append("}\n");
    sb.Append($"static void df_dropall_{cls.Name}(void* p) {{\n");
    for (var c = cls; c != null; c = c.Parent) {
      sb.Append($"  df_dropc_{c.Name}(({c.StructName}*)p);\n");
    }
    sb.Append("  ((df_object*)p)->cls = &df_dead_class;\n}\n\n");
  }
}
