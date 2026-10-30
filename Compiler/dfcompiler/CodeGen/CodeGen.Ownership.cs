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
    if (f.ReturnsOwnedSet && f.IsAbstract) {
      return f.ReturnsOwned;   // a trait function without a body takes the answer of its implementations (ClassifyReturns): it is not worked out again
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
      case "List":
      case "MapLit":
      case "Copy":
        return true;   // ->x, a literal or a copy: a new object
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
    if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Member", Text: "transformTo" }) {
      return true;   // c.transformTo<vec>(): a new collection (CodeGen.List.cs)
    }
    if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Name" } genericName) {
      return genericClasses.ContainsKey(genericName.Text!)
        || (genericFuns.TryGetValue(genericName.Text!, out var explicitFun) && TemplateReturnsMoves(explicitFun));
    }
    string? simple = callee.Kind is "Name" or "Member" ? callee.Text : null;
    if (simple == null) {
      return false;
    }
    if (callee.Kind == "Member" && simple is "clone" or "tryClone" && !funs.Values.SelectMany(list => list).Any(f => f.Name.EndsWith("." + simple))) {
      return true;   // the default clone: a new object (spec 12.6)
    }
    if (callee.Kind == "Member" && simple == "removeRefCounting" && !funs.Values.SelectMany(list => list).Any(f => f.Name.EndsWith(".removeRefCounting"))) {
      return true;   // the object leaves the counting: its receiver owns it (spec 12.7)
    }
    if (callee.Kind == "Member" && simple is "node_take" or "node_replace" or "tree_take" && call.Line >= LibraryLine) {
      return true;   // the item that leaves a list node or a map entry moves to the caller (CodeGen.List.cs, CodeGen.Map.cs)
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
      "List" or "MapLit" => true,   // a literal: a new collection (it only matters when the variable is an object)
      "Index" => e.Children[0].Kind == "GenericRef" || (e.Children.Count > 1 && e.Children[1].Kind == "Range"),   // vec<T>[n]: a new collection; c[a..b]: a new view
      "Paren" => GuessOwner(e.Children[0], state),
      "Copy" => true,   // x! and m.keys!: a new copy
      "Call" => CallMoves(e, state),
      "Member" => IsMapViewMember(e),
      _ => false
    };
  }

  // m.keys and m.values give a new view of the map (spec 6.6, CodeGen.Map.cs). Told apart by the name only, so not when a class of the program has
  // a member of that name
  bool IsMapViewMember(Node e) =>
    vecPrelude && e.Kind == "Member" && e.Text is "keys" or "values"
    && !classes.Values.Any(cls => cls.Node.Line < LibraryLine && (FindField(cls, e.Text!) != null || LookupMethods(cls, "get_" + e.Text).Count > 0));

  // ---------------------------------------------------------------- implicit drops

  // the class or trait an object variable is declared with (null for anything else)
  string? ObjectTypeOf(Node decl) {
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode == null) {
      return null;
    }
    if (typeNode is { Kind: "Type", Text: "vec" or "list" or "map" or "bmap" or "hmap" or "set" or "bset" or "hset", Children.Count: 0 } && vecPrelude) {
      return typeNode.Text;   // vec values = [1, 2, 3]: the type comes from the literal
    }
    if (typeNode is { Kind: "Type", Text: "var" or "arr" }) {
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init");
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Call" } call
          && call.Children[0] is { Kind: "Name" } name && classes.ContainsKey(name.Text!)) {
        return name.Text;
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Call" } genericCall && genericCall.Children[0].Kind == "GenericRef") {
        // var s = Stack<i32>(); with a set as type argument (Box<Num>(x)) the copy is known only when the call is compiled: any class will do here
        return HasSetArg(genericCall.Children[0]) ? ((Node)genericCall.Children[0].Children[0]).Text : GenericRefClass(genericCall.Children[0]);
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Index" } ranged && ranged.Children.Count > 1 && ranged.Children[1].Kind == "Range") {
        return "var";   // var part = c[a..b]: a view (spec 6.5)
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "MapLit" }) {
        return "var";   // var ages = ["one": 1]: a new map
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Member" } viewMember && IsMapViewMember(viewMember)) {
        return "var";   // var held = m.keys: a view of the map (spec 6.6)
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Call" } memberCall && memberCall.Children[0].Kind == "Member" && CallMoves(memberCall)) {
        // var it = numbers.getIterator(): a function that gives a new or moved object (the exact class is known when the call is compiled)
        return "var";
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
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl" && IsStringDecl(statement))) {
      owners.Add(statement);
      drops.Add(StrDropNode(statement.Text!, statement));
    }
    foreach (var target in statements.Where(statement => statement.Kind == "MultiAssign")
               .SelectMany(statement => statement.Children[0].Children).Where(target => target.Kind == "Decl" && IsStringDecl(target))) {
      owners.Add(target);
      drops.Add(StrDropNode(target.Text!, target));
    }
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl")) {
      if (IsCountedDecl(statement) || statement.Children.Any(child => child.Kind == "Weak")) {
        owners.Add(statement);   // a user or a weak reference: released when the scope ends (spec 12.7)
        drops.Add(DoneDropNode(statement.Text!, statement));
        continue;
      }
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
    if (!statements.Any(s => (s.Kind == "Decl" && (IsCountedDecl(s) || ObjectTypeOf(s) != null || IsStringDecl(s)))
        || (s.Kind == "MultiAssign" && s.Children[0].Children.Any(target => target.Kind == "Decl" && IsStringDecl(target))))) {
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
    if (operand.Kind == "Index") {
      return GiftIndex(n, operand, scope);
    }
    if (operand.Kind is not ("Name" or "Member")) {
      throw Error("only a variable or a field can be moved with ->", n);
    }
    if (StrGift(operand, scope, n) is Expr movedString) {
      return movedString;
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
    return Adopted(new Expr(code, value.Type, true), n);
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
    if (subject.Kind == "Name" && scope.Find(subject.Text!) is VarInfo { IsCounted: true }) {
      throw Error($"'{subject.Text}' is counted: release its usage with '{subject.Text} is done.'; 'is atEnd.' is only for plain owners (spec 12.4)", finalize);
    }
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
    if (target.IsField) {
      value = Adopted(value, at);   // a child put into a field changes owner (spec 12.5)
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

  // _T x is a weak declaration with a WeakInit: null when nothing is written ("default"), otherwise the value of = (spec 12.7). Done on the
  // whole tree before the other passes, so all of them see the weak declarations by their WeakInit.
  void LowerWeakDecls(Node node) {
    if (node.Kind == "Decl" && node.Children.Any(child => child.Kind == "Weak")) {
      var init = node.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
      if (init == null) {
        node.Add(Synthetic("WeakInit", node, "default").Add(Synthetic("Null", node)));   // "default": not written, see RegisterClasses
      } else {
        init.Kind = "WeakInit";
      }
    }
    foreach (var child in node.Children) {
      LowerWeakDecls(child);
    }
  }

  // ---------------------------------------------------------------- reference counting (spec 12.7)

  // @T x = @e and var x = @e are @T x = e with the marker kept on the Init (Text "marked"), so every pass sees a counted declaration
  // as a Decl with a "Counted" child (local variables and fields). Multiple assignments are not built yet.
  void LowerCountedDecls(Node node) {
    if (node.Kind == "MultiAssign" && node.Children[0].Children.Any(target => target.Children.Any(child => child.Kind == "Counted"))) {
      throw NotSupported("Counted variables in a multiple assignment", node);
    }
    if (node.Kind == "Decl") {
      var init = node.Children.FirstOrDefault(child => child.Kind == "Init");
      if (init?.Children[0] is { Kind: "CountedOf" } marked) {
        init.Children[0] = marked.Children[0];
        init.Text = "marked";
        if (!node.Children.Any(child => child.Kind == "Counted")) {
          node.Add(Synthetic("Counted", node));
        }
      }
    }
    foreach (var child in node.Children) {
      LowerCountedDecls(child);
    }
  }

  // var x = make(), where every function of that name returns @T: x is a counted variable (spec 12.7). Decided by the name of the call, as
  // CallMoves does, once the functions are registered and before any body is compiled
  void LowerCountedVars(Node node) {
    if (node.Kind == "Decl" && !IsCountedDecl(node) && node.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { Kind: "Type", Text: "var", Children.Count: 0 }
        && node.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is { Kind: "Call" } call && CallReturnsCounted(call)) {
      node.Add(Synthetic("Counted", node));
    }
    foreach (var child in node.Children) {
      LowerCountedVars(child);
    }
  }

  bool CallReturnsCounted(Node call) {
    var callee = call.Children[0];
    if (callee.Kind is not ("Name" or "Member") || callee.Text == null) {
      return false;
    }
    var candidates = funs.Values.SelectMany(list => list).Where(f => !f.IsCtor && !f.IsInit && (f.Name == callee.Text || f.Name.EndsWith("." + callee.Text))).ToList();
    return candidates.Count > 0 && candidates.All(f => IsCountedType(f.Ret));
  }

  static bool IsCountedDecl(Node decl) => decl.Kind == "Decl" && decl.Children.Any(child => child.Kind == "Counted");

  // "@Dog": the internal type of a counted parameter or result (its C type is the counting block, df_rc*); a counted variable keeps the plain
  // type with VarInfo.IsCounted
  static bool IsCountedType(string type) => type.Length > 1 && type[0] == '@';
  static string CountedBase(string type) => type[1..];

  string CountedTypeOf(string type, Node at) {
    if (!IsClass(type)) {
      throw NotSupported($"Counted values of type {type} (only objects are counted for now, spec 12.7)", at);
    }
    return "@" + type;
  }

  // a counted variable or a counted field named by a value (through parentheses); Code is the C lvalue of its counting block
  readonly record struct CountedRefInfo(string Code, string Type, string Name, VarInfo? Variable, FieldInfo? Field);

  // LValue gives the counting block of a counted field only to the counted assignment and to is done. (spec 12.7)
  bool countedFieldWrite;

  CountedRefInfo? CountedRef(Node value, Scope scope) {
    while (value.Kind == "Paren") {
      value = value.Children[0];
    }
    if (value.Kind == "Name") {
      var variable = scope.Find(value.Text!);
      if (variable != null) {
        return variable.IsCounted ? new CountedRefInfo(variable.CName, variable.Type, value.Text!, variable, null) : null;
      }
      if (currentClass is { IsTrait: false } && scope.Find("this") != null && FindField(currentClass, value.Text!) is { IsCounted: true, IsStatic: false } own) {
        return new CountedRefInfo($"self->{own.CName}", own.Type, value.Text!, null, own);
      }
      return null;
    }
    if (value.Kind == "Member" && CountedFieldOf(value, scope) is FieldInfo field) {
      var stored = ObjectField(ExprOf(value.Children[0], scope), value.Text!, value, true);
      return new CountedRefInfo(stored.Code, field.Type, value.Text!, null, field);
    }
    return CountedItemRef(value, scope);
  }

  // the counted field a member names (k.friend), or null: it only looks, an error of the owner is left to the usual code
  FieldInfo? CountedFieldOf(Node member, Scope scope) {
    if (member.Kind != "Member" || member.Text == null) {
      return null;
    }
    Expr owner;
    try {
      owner = ExprOf(member.Children[0], scope);
    } catch (CompileException) {
      return null;
    }
    if (!IsClass(owner.Type) || ObjInfo(owner.Type).IsTrait) {
      return null;
    }
    return FindField(ObjInfo(owner.Type), member.Text) is { IsCounted: true, IsStatic: false } field ? field : null;
  }

  // reading a counted field gives its object
  Expr CountedFieldRead(Expr stored, FieldInfo field) => new($"DF_RC_OBJ({CType(field.Type)}, {stored.Code})", field.Type);

  // return value in a function that returns @T: a counted variable gives its usage (no count change, it is null afterwards, so the end of the
  // scope releases nothing), a counted field adds a user, anything else is taken as by a counted variable (a new object, @plain, null,
  // another counted result)
  string CountedReturnCode(Node value, Scope scope, Node at) {
    string type = CountedBase(currentRet!);
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      if (!IsSubtype(counted.Type, type)) {
        throw Error($"'{counted.Name}' is a {counted.Type}, not a {type}", at);
      }
      return counted.Variable != null ? TakeUsage(counted.Variable) : $"df_rc_use({counted.Code})";
    }
    return CountedValue(value, scope, type, false, at);
  }

  // the usage of a counted variable leaves it: the variable becomes null
  string TakeUsage(VarInfo counted) {
    string taken = NewTemp();
    return $"({{ df_rc* {taken} = {counted.CName}; {counted.CName} = NULL; {taken}; }})";
  }

  // an argument for a @T parameter: f(rc) adds a user for the call, f(->rc) gives the caller's usage, f(Dog()) and f(@plain) start a block
  string CountedArg(Node arg, string type, Scope scope) {
    if (arg.Kind == "Gift" && CountedRef(arg.Children[0], scope) is CountedRefInfo given) {
      if (!IsSubtype(given.Type, type)) {
        throw Error($"'{given.Name}' is a {given.Type}, not a {type}", arg);
      }
      if (given.Variable == null) {
        throw NotSupported("Moving the usage out of a counted field (->k.field)", arg);
      }
      return TakeUsage(given.Variable);
    }
    return CountedValue(arg, scope, type, false, arg);
  }

  // the cost of an argument in the overload choice for the counted cases: -1 not a counted case (the usual rules decide), -2 it cannot be
  // passed (a counted argument to a plain parameter, a plain object to a @T parameter)
  int CountedArgCost(Node arg, string paramType, Scope scope) {
    if (IsWeakType(paramType)) {
      string weakType = paramType[1..];
      var source = arg.Kind == "WeakOf" ? CountedRef(arg.Children[0], scope) ?? WeakRef(arg.Children[0], scope) : WeakRef(arg, scope);
      return arg.Kind == "Null" ? 50 : source is CountedRefInfo given && IsSubtype(given.Type, weakType)
        ? (given.Type == weakType ? 0 : 10 + ClassDistance(given.Type, weakType)) : -2;
    }
    if (arg.Kind == "WeakOf" || WeakRef(arg, scope) != null) {
      return -2;   // a weak reference goes only to a weak parameter
    }
    if (!IsCountedType(paramType)) {
      bool countedArg = CountedRef(arg, scope) != null || arg.Kind == "CountedOf" || (arg.Kind == "Gift" && CountedRef(arg.Children[0], scope) != null);
      return countedArg ? -2 : -1;
    }
    string type = CountedBase(paramType);
    int Fits(string given) => IsSubtype(given, type) ? 10 + ClassDistance(given, type) : -2;
    if (arg.Kind == "Null") {
      return 50;
    }
    var named = arg.Kind == "Gift" ? CountedRef(arg.Children[0], scope) : CountedRef(arg, scope);
    if (named is CountedRefInfo found) {
      return found.Type == type ? 0 : Fits(found.Type);
    }
    var inner = arg.Kind == "CountedOf" ? arg.Children[0] : arg;
    var value = ExprOf(inner, scope);
    if (IsCountedType(value.Type)) {
      return CountedBase(value.Type) == type ? 0 : Fits(CountedBase(value.Type));
    }
    if (IsClass(value.Type) && (value.Fresh || arg.Kind == "CountedOf")) {
      return value.Type == type ? 5 : Fits(value.Type);
    }
    return -2;   // -2: cannot be passed
  }

  // the code of the counting block a counted variable or field receives: a new object starts a block (1 user), another counted variable or
  // field adds a user, null holds nothing
  string CountedValue(Node value, Scope scope, string type, bool marked, Node at) {
    if (value.Kind == "CountedOf") {
      value = value.Children[0];
      marked = true;
    }
    if (value.Kind == "Null") {
      return "NULL";
    }
    if (CountedRef(value, scope) is CountedRefInfo other) {
      if (!IsSubtype(other.Type, type)) {
        throw Error($"'{other.Name}' is a {other.Type}, not a {type}", at);
      }
      return $"df_rc_use({other.Code})";   // another user of the same object; @x on a counted x changes nothing
    }
    if (marked && value is { Kind: "Name", Text: "this" }) {
      throw Error("an object cannot make itself counted: @this (whoever holds the object makes it counted, spec 12.7)", at);
    }
    if (marked && value.Kind is "Name" or "Member") {
      // @plain: the owner gives its object to the counting system and is null afterwards, as after ->plain
      if (value.Kind == "Name" && scope.Find(value.Text!) is VarInfo plain && (plain.IsWeak || !plain.Own)) {
        throw Error($"'{value.Text}' {(plain.IsWeak ? "is a weak reference" : "only borrows its object")}: only the owner of an object can make it counted (spec 12.7)", at);
      }
      var moved = GiftExpr(Synthetic("Gift", value).Add(value), scope);
      if (!IsClass(moved.Type)) {
        throw Error($"only objects are counted for now: this is {moved.Type} (spec 12.7)", at);
      }
      return $"df_rc_new((void*)({Convert(moved, type, at)}))";
    }
    var given = ExprOf(value, scope, type);
    if (IsCountedType(given.Type)) {
      // the result of a function that returns @T: its usage is adopted, no count change
      if (!IsSubtype(CountedBase(given.Type), type)) {
        throw Error($"this is a counted {CountedBase(given.Type)}, not a {type}", at);
      }
      return given.Code;
    }
    if (!given.Fresh) {
      throw marked
        ? Error("@ takes a new object or a variable that owns its object (spec 12.7)", at)
        : Error($"a counted variable or parameter takes a new object, another counted object or null; an existing plain object starts counting with the marker: @{value.Text ?? "x"} (spec 12.7)", at);
    }
    return $"df_rc_new((void*)({Convert(given, type, at)}))";
  }

  // x = value for a counted variable: the new value is taken before the old one is released (x = x keeps the object)
  string CountedAssign(Node s, VarInfo variable, Scope scope) {
    if (s.Text != "=") {
      throw Error($"'{s.Children[0].Text}' is a counted variable: only = is defined on it", s);
    }
    if (variable.IsConst) {
      throw Error($"'{s.Children[0].Text}' is const", s);
    }
    string code = CountedValue(s.Children[1], scope, variable.Type, false, s);
    string fresh = NewTemp();
    string old = NewTemp();
    MarkAssigned(variable);
    return CheckRaise($"({{ df_rc* {fresh} = {code}; df_rc* {old} = {variable.CName}; {variable.CName} = {fresh}; df_rc_release({old}); }})", "void");
  }

  // the counting block of a counted field as a target, through the usual checks of a field write (access, readonly, read-only references)
  LValueInfo CountedFieldTarget(Node target, Scope scope) {
    countedFieldWrite = true;
    try {
      return LValue(target, scope);
    } finally {
      countedFieldWrite = false;
    }
  }

  // k.friend = value, friend = value: as for a counted variable, the old usage is released after the new one is taken
  string CountedFieldAssign(Node s, Scope scope) {
    if (s.Text != "=") {
      throw Error($"'{s.Children[0].Text}' is a counted field: only = is defined on it", s);
    }
    var target = CountedFieldTarget(s.Children[0], scope);
    if (target.IsConst) {
      throw Error($"'{s.Children[0].Text}' is const", s);
    }
    string code = CountedValue(s.Children[1], scope, target.Type, false, s);
    string fresh = NewTemp();
    string slot = NewTemp();
    string old = NewTemp();
    return CheckRaise($"({{ df_rc* {fresh} = {code}; df_rc** {slot} = &({target.Code}); df_rc* {old} = *{slot}; *{slot} = {fresh}; df_rc_release({old}); }})", "void");
  }

  // x is done.: releases the usage early; x becomes null and the end of the scope (or of the holder, for a field) releases nothing
  void EmitDone(Node done, StringBuilder sb, Scope scope, string pad) {
    var subject = done.Children[0];
    if (WeakRef(subject, scope) is CountedRefInfo weak) {
      // a weak reference lets its counting block go
      string weakSlot = NewTemp();
      string weakTaken = NewTemp();
      string weakPlace = weak.Variable != null ? weak.Code : CountedFieldTarget(subject, scope).Code;
      sb.Append($"{pad}({{ df_rc** {weakSlot} = &({weakPlace}); df_rc* {weakTaken} = *{weakSlot}; *{weakSlot} = NULL; df_rc_unweak({weakTaken}); }});\n");
      return;
    }
    if (CountedRef(subject, scope) is not CountedRefInfo counted) {
      throw Error("'is done.' releases a usage or a weak reference (@T, _T); a plain object ends with 'is atEnd.' (spec 12.7)", done);
    }
    string slot = NewTemp();
    string taken = NewTemp();
    string place = counted.Variable != null ? counted.Code : CountedFieldTarget(subject, scope).Code;
    sb.Append(pad).Append(CheckRaise($"({{ df_rc** {slot} = &({place}); df_rc* {taken} = *{slot}; *{slot} = NULL; df_rc_release({taken}); }})", "void")).Append(";\n");
  }

  // @Dog first = Dog("Rex"), @Dog second = first, var third = @Dog(): the variable was created at the start of its scope (it has a release)
  void EmitCountedDecl(Node s, string type, Node? init, StringBuilder sb, Scope scope, string pad) {
    if (IsCountedType(type)) {
      type = CountedBase(type);   // var x = make()
    }
    if (!IsClass(type)) {
      throw NotSupported($"Counted values of type {type} (only objects are counted for now, spec 12.7)", s);
    }
    if (!frames.Peek().Hoisted.TryGetValue(s.Text!, out var variable) || !variable.IsCounted) {
      throw NotSupported("A counted variable in this place (only in blocks and function bodies for now)", s);
    }
    string code = init == null ? "NULL" : CountedValue(init.Children[0], scope, type, init.Text == "marked", init);
    sb.Append($"{pad}{variable.CName} = {code};\n");
    scope.Vars[s.Text!] = variable;
    assign.Unassigned.Remove(variable);
  }

  // a counted object cannot be borrowed (&, plain T) or held by a plain variable or field: it is used through a new usage (spec 12.7)
  void CheckNotBorrowOfCounted(Node value, Scope scope, string target, Node at) {
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      throw Error($"'{counted.Name}' is counted and cannot be borrowed: take another usage with @{counted.Type} {target} = ... (spec 12.7)", at);
    }
  }

  // (&) on a counted object is a compile error: writing needs a read/write usage, (@)T (spec 12.7)
  void CheckNotWritableBorrowOfCounted(Node value, Scope scope, Node at) {
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      throw Error($"(&) is not allowed on the counted object '{counted.Name}': writing needs a read/write usage, (@)T (spec 12.7)", at);
    }
  }

  static Node DoneDropNode(string name, Node at) {
    var done = Synthetic("Done", at).Add(Synthetic("Name", at, name));
    return Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("ExprStmt", at).Add(done)));
  }

  // x.usersCount, x.weakrefsCount, k.friend.usersCount (spec 12.7): null raises NullPointerException, like any member
  Expr? CountsMember(Node member, Scope scope) {
    if (member.Text is not ("usersCount" or "weakrefsCount") || CountedRef(member.Children[0], scope) is not CountedRefInfo counted) {
      return null;
    }
    string block = NewTemp();
    string objectCode = $"DF_RC_OBJ({CType(counted.Type)}, {block})";
    string field = member.Text == "usersCount" ? "users" : "weaks";
    return new Expr($"({{ df_rc* {block} = {counted.Code}; (void){NullChecked(objectCode, counted.Type)}; (uint64_t){block}->{field}; }})", "u64");
  }

  // x.removeRefCounting() (spec 12.7): a new plain owned object for the last user (x becomes null), null when other users exist (x stays
  // valid). Only on counted variables and fields; a class of the program may still have a method of that name for its plain objects
  Expr? RemoveCountingCall(Node call, Scope scope) {
    var callee = call.Children[0];
    if (callee is not { Kind: "Member", Text: "removeRefCounting" }) {
      return null;
    }
    var receiver = callee.Children[0];
    if (WeakRef(receiver, scope) is CountedRefInfo weak) {
      throw Error($"'{weak.Name}' is a weak reference: removeRefCounting() is valid only on a counted object (@T, spec 12.7)", call);
    }
    if (CountedRef(receiver, scope) is not CountedRefInfo counted) {
      Expr plain;
      try {
        plain = ExprOf(receiver, scope);
      } catch (CompileException) {
        return null;
      }
      if (IsClass(plain.Type) && LookupMethods(ObjInfo(plain.Type), "removeRefCounting").Count > 0) {
        return null;   // a method of the class
      }
      throw Error("removeRefCounting() is valid only on a counted object (@T); this object is not counted (spec 12.7)", call);
    }
    if (call.Children.Count > 1 && call.Children[1].Children.Count > 0) {
      throw Error("removeRefCounting() takes no arguments", call);
    }
    string place = counted.Variable != null ? counted.Code : CountedFieldTarget(receiver, scope).Code;
    return new Expr($"(({CType(counted.Type)})df_rc_take(&({place})))", counted.Type, true);
  }

  // two classes with counted fields of each other's type (or one of its own type) may hold each other: a level 1 warning (spec 12.7, 19.3)
  void WarnCountedCycles() {
    var program = classes.Values.Where(cls => !cls.IsTrait && cls.Node.Line < LibraryLine).ToList();
    var reported = new HashSet<string>();
    foreach (var start in program) {
      foreach (var first in start.Fields.Where(field => field.IsCounted)) {
        var path = new List<FieldInfo> { first };
        if (FindCountedCycle(first.Type, start, path, new HashSet<string>())
            && reported.Add(string.Join(",", path.Select(field => field.Owner.Name + "." + field.Name).Order()))) {
          string links = string.Join(" -> ", path.Select(field => $"{field.Owner.Name}.{field.Name}")) + $" -> {start.Name}";
          Warn($"possible cycle of counted objects: {links}; objects in a cycle are never destroyed (zombie objects): make one of the links a weak reference (_T)", first.At, 1);
        }
      }
    }
  }

  bool FindCountedCycle(string type, ClassInfo start, List<FieldInfo> path, HashSet<string> seen) {
    if (type == start.Name || IsSubtype(start.Name, type)) {
      return true;
    }
    if (!seen.Add(type) || !classes.TryGetValue(type, out var cls)) {
      return false;
    }
    foreach (var field in cls.Fields.Where(field => field.IsCounted)) {
      path.Add(field);
      if (FindCountedCycle(field.Type, start, path, seen)) {
        return true;
      }
      path.RemoveAt(path.Count - 1);
    }
    return false;
  }

  // ---------------------------------------------------------------- weak references (spec 12.7)

  // "_Dog": the internal type of a weak parameter (its C type is the counting block, df_rc*); a weak variable or field keeps the plain type
  // with IsWeak
  static bool IsWeakType(string type) => type.Length > 1 && type[0] == '_';

  // a weak variable or a weak field named by a value; Code is the C lvalue of the counting block it holds
  CountedRefInfo? WeakRef(Node value, Scope scope) {
    while (value.Kind == "Paren") {
      value = value.Children[0];
    }
    if (value.Kind == "Name") {
      var variable = scope.Find(value.Text!);
      if (variable != null) {
        return variable.IsWeak ? new CountedRefInfo(variable.CName, variable.Type, value.Text!, variable, null) : null;
      }
      if (currentClass is { IsTrait: false } && scope.Find("this") != null && FindField(currentClass, value.Text!) is { IsWeak: true, IsStatic: false } own) {
        return new CountedRefInfo($"self->{own.CName}", own.Type, value.Text!, null, own);
      }
      return null;
    }
    if (value.Kind == "Member" && value.Text != null) {
      Expr owner;
      try {
        owner = ExprOf(value.Children[0], scope);
      } catch (CompileException) {
        return null;
      }
      if (!IsClass(owner.Type) || ObjInfo(owner.Type).IsTrait || FindField(ObjInfo(owner.Type), value.Text) is not { IsWeak: true, IsStatic: false } field) {
        return null;
      }
      var stored = ObjectField(owner, value.Text, value, true);
      return new CountedRefInfo(stored.Code, field.Type, value.Text, null, field);
    }
    return null;
  }

  // the counting block a weak reference receives (one more weak reference on it): _x of a counted variable or field, another weak reference
  // (copied without enforce), or null
  string WeakValue(Node value, Scope scope, string type, Node at) {
    if (value.Kind == "Null") {
      return "NULL";
    }
    if (value.Kind == "WeakOf") {
      var target = value.Children[0];
      var source = CountedRef(target, scope) ?? WeakRef(target, scope);
      if (source is not CountedRefInfo found) {
        throw Error($"weak references exist only on counted objects: '{target.Text ?? "this value"}' is not counted (spec 12.7)", at);
      }
      if (!IsSubtype(found.Type, type)) {
        throw Error($"'{found.Name}' is a {found.Type}, not a {type}", at);
      }
      return $"df_rc_weak({found.Code})";
    }
    if (WeakRef(value, scope) is CountedRefInfo weak) {
      if (!IsSubtype(weak.Type, type)) {
        throw Error($"'{weak.Name}' is a {weak.Type}, not a {type}", at);
      }
      return $"df_rc_weak({weak.Code})";
    }
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      throw Error($"a weak reference is made with the marker: _{counted.Name} (spec 12.7)", at);
    }
    throw Error("a weak reference takes _x of a counted object, another weak reference or null (spec 12.7)", at);
  }

  // w = value for a weak variable, parameter or field: the new block is taken before the old one is let go
  string WeakAssign(Node s, Scope scope) {
    if (s.Text is not ("=" or "_=")) {
      throw Error($"'{s.Children[0].Text}' is a weak reference: only = is defined on it", s);
    }
    var target = CountedFieldTarget(s.Children[0], scope);
    if (target.IsConst) {
      throw Error($"'{s.Children[0].Text}' is const", s);
    }
    string code = WeakValue(s.Children[1], scope, target.Type, s);
    string fresh = NewTemp();
    string slot = NewTemp();
    string old = NewTemp();
    MarkAssigned(target.Variable);
    return $"({{ df_rc* {fresh} = {code}; df_rc** {slot} = &({target.Code}); df_rc* {old} = *{slot}; *{slot} = {fresh}; df_rc_unweak({old}); }})";
  }

  // _Dog w = _first: the variable was created at the start of its scope (it lets the block go at the end)
  void EmitWeakDecl(Node s, string type, Node? init, StringBuilder sb, Scope scope, string pad) {
    if (!IsClass(type)) {
      throw Error("only objects have weak references", s);
    }
    if (!frames.Peek().Hoisted.TryGetValue(s.Text!, out var variable) || !variable.IsWeak) {
      throw NotSupported("A weak variable in this place (only in blocks and function bodies for now)", s);
    }
    string code = init == null || init.Text == "default" ? "NULL" : WeakValue(init.Children[0], scope, type, init);
    sb.Append($"{pad}{variable.CName} = {code};\n");
    scope.Vars[s.Text!] = variable;
    assign.Unassigned.Remove(variable);
  }

  // enforce w { }: inside the block w is a counted variable of its own, a temporary usage taken when the block starts and released on every
  // way out of it (the usual release of a counted variable at the end of its scope)
  Node EnforcedWeakBlock(Node block, string name, string type, string blockCode) {
    var use = Synthetic("Decl", block, name).Add(Synthetic("Type", block, type)).Add(Synthetic("Counted", block)).Add(Synthetic("EnforceUse", block))
      .Add(Synthetic("Init", block).Add(Synthetic("CValue", block, $"df_rc_use({blockCode})").Add(Synthetic("Type", block, "@" + type))));
    var wrapped = Synthetic("Block", block).Add(use);
    wrapped.Children.AddRange(block.Children);
    return wrapped;
  }

  // a weak field is read through enforce, or copied into another weak reference
  void CheckWeakFieldRead(FieldInfo? field, Node at) {
    if (field is { IsWeak: true }) {
      throw at.Kind == "Member"
        ? Error($"'{field.Name}' is a weak reference: copy it into a weak variable (_{field.Type} {field.Name} = ...) and use that through enforce", at)
        : Error($"'{field.Name}' is a weak reference: use it through enforce {at.Text} {{ }} else {{ }}", at);
    }
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
    if (CountedRef(expression, scope) is CountedRefInfo counted) {
      throw Error($"'{counted.Name}' is counted and cannot be borrowed: a function returns it as fun @{counted.Type} (spec 12.7)", at);
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
    return moves == true ? Adopted(value, at) : value;   // a child returned to the caller changes owner (spec 12.5)
  }

  // ---------------------------------------------------------------- the destruction code of the classes

  List<FieldInfo> OwnedStringFields(ClassInfo cls) {
    return IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields)
      .Where(field => !field.IsStatic && field.Type == "string").ToList();
  }

  List<FieldInfo> OwnedObjectFields(ClassInfo cls) {
    return IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields)
      .Where(field => !field.IsStatic && IsClass(field.Type) && !field.IsParentRef && !field.IsWeak && !field.IsCounted).ToList();
  }

  void EmitDropGlue(ClassInfo cls) {
    var sb = classCode;
    sb.Append($"static void df_dropc_{cls.Name}({cls.StructName}* self) {{\n");
    if (cls.Dtor != null) {
      // an orphan adopted child already ran its atEnd when it found its parent gone (slot DF_UNS_MAX, spec 12.5)
      string orphanDone = cls.ParentField != null ? "if (self->m_df_slot != DF_UNS_MAX) " : "";
      sb.Append($"  {orphanDone}{cls.Dtor.CName}(self);\n");
    }
    if (cls.HasChildren) {
      sb.Append("  df_kids_release(&self->m_df_children);   /* the adopted children lose their reference to this parent (spec 12.5) */\n");
    }
    if (cls.ParentField is FieldInfo parentField) {
      // an adopted child that ends before its parent leaves the list of the parent
      sb.Append($"  if (self->m_df_slot != 0 && self->m_df_slot != DF_UNS_MAX && self->{parentField.CName} != NULL) {{ df_kids_remove(self->{parentField.CName}->m_df_children, self->m_df_slot); }}\n");
    }
    foreach (var field in OwnedObjectFields(cls)) {
      sb.Append($"  if (self->{field.CName} != NULL) {{ void* o = (void*)self->{field.CName}; self->{field.CName} = NULL; df_drop(o); }}\n");
    }
    foreach (var field in OwnedStringFields(cls)) {
      sb.Append($"  df_str_free(self->{field.CName});\n  self->{field.CName} = NULL;\n");
    }
    foreach (var field in cls.Fields.Where(field => field.IsCounted && !field.IsStatic)) {
      // a counted field is a user: it releases its usage (spec 12.7)
      sb.Append($"  {{ df_rc* r = self->{field.CName}; self->{field.CName} = NULL; df_rc_release(r); }}\n");
    }
    foreach (var field in cls.Fields.Where(field => field.IsWeak && !field.IsStatic)) {
      sb.Append($"  {{ df_rc* r = self->{field.CName}; self->{field.CName} = NULL; df_rc_unweak(r); }}\n");
    }
    if (IsVec(cls.Name)) {
      EmitVecDropGlue(sb, cls);
    }
    if (IsList(cls.Name)) {
      EmitListDropGlue(sb, cls);
    }
    if (IsMap(cls.Name) || IsSet(cls.Name)) {
      EmitMapDropGlue(sb, cls);
    }
    sb.Append("}\n");
    sb.Append($"static void df_dropall_{cls.Name}(void* p) {{\n");
    for (var c = cls; c != null; c = c.Parent) {
      sb.Append($"  df_dropc_{c.Name}(({c.StructName}*)p);\n");
    }
    sb.Append("  ((df_object*)p)->cls = &df_dead_class;\n}\n\n");
  }
}
