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

  // a plain result that is a new value (a built string, array or object) is given, as if written ->T (spec 13.1.1): the function could not
  // lend it, because nothing else holds it. Worked out from the returns before any body is compiled, so every caller sees the same answer;
  // repeated while a call to a function found here makes another result new
  void InferGivenResults() {
    var all = funs.Values.SelectMany(list => list)
      .Where(f => !f.IsCtor && !f.IsInit && !f.IsAbstract && !f.IsExtern && f.Node.Line < LibraryLine && !IsCountedType(f.Ret)
        && !f.Name.Split('.')[^1].StartsWith("get_") && f.Node.Children.Any(child => child.Kind == "Block")).ToList();
    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var f in all) {
        var types = IsMulti(f.Ret) ? TupleElems(f.Ret).Select(elem => elem.Type).ToList() : new List<string> { f.Ret };
        var locals = new Dictionary<string, Node>();
        CollectDeclarations(f.Node, locals);
        foreach (var returned in CollectReturns(f.Node).Where(ret => ret.Children.Count == types.Count)) {
          for (int index = 0; index < types.Count; index++) {
            string type = types[index];
            if (f.Gives.Contains(index) || f.Writes.Contains(index) || !(IsClass(type) || IsArr(type) || type == "string")) {
              continue;
            }
            if (IsBuiltResult(returned.Children[index], type, locals, 0)) {
              f.Gives.Add(index);
              changed = true;
            }
          }
        }
      }
    }
  }

  // the value is built here (no scope yet: a local counts when its declaration builds the value)
  bool IsBuiltResult(Node value, string type, Dictionary<string, Node> locals, int depth) => depth < 8 && value.Kind switch {
    "Paren" => IsBuiltResult(value.Children[0], type, locals, depth + 1),
    "Gift" or "Copy" => true,
    "List" => IsArr(type),
    "Interp" => type == "string",
    "Binary" => type == "string" && value.Text == "+",
    "Call" => value.Children[0] is { Kind: "Name" } callee && classes.ContainsKey(callee.Text!)
      || CalleesOf(value) is { Count: > 0 } callees && callees.All(f => f.Gives.Contains(0)) || IsArr(type) && IsNewArray(value)
      || (type == "string" || IsArr(type)) && CalleesOf(value).Count == 0,
    "Name" => locals.TryGetValue(value.Text!, out var decl)
      && decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is Node init
      && init.Kind is not ("Name" or "Paren") && IsBuiltResult(init, type, locals, depth + 1),   // Dog view = made borrows: not built here
    _ => false
  };

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
        throw Error($"'{group.Key.Name}' and the functions that replace it do not return objects the same way (some move, some borrow)", group.First().Node);
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
      case "EmptyNew":
      case "Copy":
        return true;   // ->x, a literal, () or a copy: a new object
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
    if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Member", Text: "transformTo" or "map" }) {
      return true;   // c.transformTo<vec>(), c.map<U>(f): a new collection (CodeGen.List.cs, CodeGen.Functional.cs)
    }
    if (callee.Kind is "Lambda" or "Call" or "Paren") {
      return true;   // a call of a function value: its result is new (spec 13.3)
    }
    if (callee.Kind == "GenericRef" && callee.Children[0] is { Kind: "Name" } genericName) {
      return genericClasses.ContainsKey(genericName.Text!)
        || (genericFuns.TryGetValue(genericName.Text!, out var explicitFun) && TemplateReturnsMoves(explicitFun));
    }
    string? simple = callee.Kind is "Name" or "Member" ? callee.Text : null;
    if (simple == null) {
      return false;
    }
    if (callee.Kind == "Member" && simple is "clone" or "tryClone" or "filter" && !funs.Values.SelectMany(list => list).Any(f => f.Name.EndsWith("." + simple))) {
      return true;   // the default clone: a new object (spec 12.6); filter: a new collection (spec 6.6)
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
    if (candidates.Count == 0 && callee.Kind == "Name" && !Stdlib.ContainsKey(simple)) {
      return true;   // f() where f is a function value: what it returns is new (spec 13.3)
    }
    if (candidates.Count == 0 && callee.Kind == "Member" && simple == "getIterator") {
      return true;   // a.getIterator() of an arr (made by the compiler, no function): a new iterator (spec 6.7)
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
      "List" or "MapLit" or "EmptyNew" => true,   // a literal or (): a new collection or object (it only matters when the variable is an object)
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
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Copy" }) {
        return "var";   // var h2 = h!, var part = c[a..b]!: a new object, the variable owns it (spec 12.6)
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "MapLit" }) {
        return "var";   // var ages = ["one": 1]: a new map
      }
      if (typeNode.Text == "var" && init?.Children[0] is { Kind: "Member" } viewMember && IsMapViewMember(viewMember)) {
        return "var";   // var held = m.keys: a view of the map (spec 6.6)
      }
      if (typeNode.Text == "var" && decl.Line < LibraryLine && init?.Children[0] is { Kind: "Call" } funCall && funCall.Children[0].Kind == "Name"
          && CalleesOf(funCall) is { Count: > 0 } callees && callees.All(f => IsClass(f.Ret)) && CallMoves(funCall)) {
        // var lost = orphan(): a function that gives a new or moved object (its class or trait is known when the call is compiled)
        return "var";
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
    var finalize = Synthetic("Finalize", at, "implicit").Add(Synthetic("Name", at, name));   // added by the compiler, not written
    var statement = Synthetic("ExprStmt", at).Add(finalize);
    return Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(statement));
  }

  // the declarations of a scope whose objects end with the scope: an atEnd for each, run after the written ones
  HashSet<Node> AddImplicitDrops(Frame frame, List<Node> statements) {
    var drops = new List<Node>();
    var owners = new HashSet<Node>();
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl" && (IsStringDecl(statement) || IsOptStringDecl(statement) || IsFunDecl(statement)))) {
      owners.Add(statement);
      drops.Add(StrDropNode(statement.Text!, statement));
    }
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl" && IsStructObjDecl(statement))) {
      owners.Add(statement);   // a struct that holds strings or objects: they end with the scope (spec 11.1.5.3)
      structObjDecls.Add(statement);
      drops.Add(StructDropNode(statement.Text!, statement));
    }
    // (also with one value for each target, Pet a, i32 n = q, 3: the struct is a new one or a clone)
    foreach (var target in statements.Where(statement => statement.Kind == "MultiAssign" && (GivesOwnMultiValues(statement)
                 || statement.Children[1].Children.Count == statement.Children[0].Children.Count && statement.Children[1].Children.Count > 1))
               .SelectMany(statement => statement.Children[0].Children).Where(target => target.Kind == "Decl"
                 && target.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { Kind: "Type" } typeNode
                 && TryResolveType(typeNode) is string type && StructHoldsObjects(type))) {
      owners.Add(target);   // Pet p, i32 n = f(): the struct given by a function of the program ends with the scope (spec 11.1.5.3)
      structObjDecls.Add(target);
      drops.Add(StructDropNode(target.Text!, target));
    }
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl" && IsBigStructDecl(statement))) {
      bigStructDecls.Add(statement);   // a struct above 64 KB: on the heap, freed with the scope (spec 11.1.5.5)
      if (owners.Add(statement)) {
        drops.Add(StructDropNode(statement.Text!, statement));
      }
    }
    foreach (var statement in statements.Where(statement => statement.Kind == "Decl" && IsTupleArrDecl(statement))) {
      owners.Add(statement);   // a tuple that holds arrays: they end with the scope (spec 6.8.1)
      tupleArrDecls.Add(statement);
      drops.Add(TupleDropNode(statement.Text!, statement));
    }
    foreach (var target in statements.Where(statement => statement.Kind == "MultiAssign")
               .SelectMany(statement => statement.Children[0].Children).Where(target => target.Kind == "Decl" && IsCountedDecl(target))) {
      owners.Add(target);   // @Dog a, @Dog b = x, y: each usage is released with the scope (spec 12.7)
      drops.Add(DoneDropNode(target.Text!, target));
    }
    foreach (var target in statements.Where(statement => statement.Kind == "MultiAssign")
               .SelectMany(statement => statement.Children[0].Children).Where(target => target.Kind == "Decl" && (IsStringDecl(target) || IsFunDecl(target)))) {
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
    for (int index = 0; index < statements.Count; index++) {
      var statement = statements[index];
      var before = statements.Take(index);
      bool mixed = statement.Kind == "Decl" && !IsOwnedLocalArray(statement, statements.Skip(index + 1), before) && IsMixedLocalArray(statement, statements.Skip(index + 1), before);
      if (statement.Kind == "Decl" && (mixed || IsOwnedLocalArray(statement, statements.Skip(index + 1), before))) {
        owners.Add(statement);   // hoisted, so the drop at the end of the scope sees it
        ownedArrayDecls.Add(statement);
        if (mixed) {
          mixedArrayDecls.Add(statement);
        } else if (lastHandover != null) {
          arrHandover[statement] = lastHandover;   // the last IsOwnedLocalArray above was this one
        }
        drops.Add(Synthetic("AtEnd", statement, "drop").Add(Synthetic("Block", statement).Add(Synthetic("ArrDrop", statement, statement.Text))));
      }
      if (statement.Kind == "MultiAssign" && GivesOwnMultiValues(statement)) {
        // arr<T> values, i32 n = f(): an array declared from the values of a function of the program is owned (spec 6.8.1)
        foreach (var target in statement.Children[0].Children.Where(target => target.Kind == "Decl" && IsArrTypeNode(target.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)))
            && statements.Skip(index + 1).All(node => ArrayUsesStayLocal(node, null, null, 0, target.Text!, true, false)))) {
          owners.Add(target);
          ownedArrayDecls.Add(target);
          drops.Add(Synthetic("AtEnd", target, "drop").Add(Synthetic("Block", target).Add(Synthetic("ArrDrop", target, target.Text))));
        }
      }
    }
    frame.AtEnds.InsertRange(0, drops);
    return owners;
  }

  // an array that ends with its scope (spec 6.8.1, point 20), for now only when that is safe to see: a local declared with its type from a new
  // array (a literal, arr<T>[n], a clone, ->b) or with no value, assigned again only new arrays, and used afterwards only in the ways
  // ArrayUsesStayLocal accepts. Any other use (b = a, a view a[x..y], an argument a function keeps) leaves it as before: not freed
  bool IsOwnedLocalArray(Node decl, IEnumerable<Node> after, IEnumerable<Node>? block = null) {
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    var init = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
    // var x = [1, 2] and var x = f() with f giving a new array: an arr of its scope
    bool varLiteral = typeNode is { Kind: "Type", Text: "var" } && (init is { Kind: "List" } || init is { Kind: "Call" } && IsNewArray(init));
    if (!IsArrTypeNode(typeNode) && !varLiteral) {
      return false;
    }
    if (decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("const") == true) {
      return false;
    }
    bool plain = !varLiteral && TryResolveType(typeNode!) is string arrType && IsArr(arrType) && HasPlainItems(arrType);
    if (init != null && !GivesOwnArray(init) && !(mixedPass && init.Kind == "Name")) {
      return false;   // (arr<T> c = b: a mixed local that starts borrowing; return c gives the caller a copy, b still ends)
    }
    var region = after.ToList();
    string? handover = null;
    bool owned = WithArrScan(decl, region, block, scan => {
      if (!region.All(node => ArrayUsesStayLocal(node, null, null, 0, decl.Text!, true, plain))) {
        return false;
      }
      // a = b with a outside b's block, or b assigned again while a may hold its array: when b lets its array go, it hands it to a if a
      // still holds it (a takes it as its own). Only with one such a and no other borrower; b moved or given with &: not freed, as before
      if (!scan.OuterBorrower && !(scan.AssignLent && scan.Released)) {
        return true;
      }
      if (mixedPass || scan.AssignBorrowers.Count != 1 || scan.InitLent || scan.MovedOut) {
        return false;
      }
      handover = scan.AssignBorrowers.First();
      return true;
    });
    lastHandover = owned ? handover : null;
    return owned;
  }

  // a local array that is also assigned arrays it only borrows (a = b): a flag says at run time whether it holds its own array, which ends
  // when it is assigned again and with the scope. return a moves its own array out or gives a copy of the borrowed one; ->a and &a are
  // compile errors (it may only borrow)
  bool IsMixedLocalArray(Node decl, IEnumerable<Node> after, IEnumerable<Node>? block = null) {
    bool saved = mixedPass;
    mixedPass = true;
    try {
      return IsOwnedLocalArray(decl, after, block);
    } finally {
      mixedPass = saved;
    }
  }

  bool mixedPass;

  // the scan of the uses of one array (the lender): the statements where it lives, the local arrays declared there (they end no later than
  // the lender), and what the scan found
  sealed class ArrScan {
    public string Lender = "";
    public List<Node> Region = new();
    public Dictionary<string, Node> Inner = new();   // name -> its declaration
    public List<Node> Block = new();   // the statements of the lender's block (before it, itself, after it)
    public Dictionary<string, bool> Mixed = new();
    public HashSet<string> Visiting = new();
    public bool AssignLent;   // a = b: an existing local borrows the lender
    public bool Released;   // the lender is assigned again, moved or handed out with &
    public bool MovedOut;   // the lender is moved or handed out with & (not only assigned again)
    public bool InitLent;   // arr<T> c = b or var v = b[x..y]: a local declared from the lender borrows it
    public bool OuterBorrower;   // a = b with a declared outside the lender's block: a outlives it
    public HashSet<string> AssignBorrowers = new();   // the names of the a in a = b
  }

  // the lender that hands its array over to the one local that borrows it with a = b (spec 6.8.1): set by IsOwnedLocalArray
  string? lastHandover;
  readonly Dictionary<Node, string> arrHandover = new();   // the declaration of the lender -> the borrower that takes its array

  ArrScan? arrScan;
  bool borrowerPass;   // checking a local that borrows the lender: it must not give the array away

  bool WithArrScan(Node lender, List<Node> region, IEnumerable<Node>? block, Func<ArrScan, bool> check) {
    var scan = new ArrScan { Lender = lender.Text!, Region = region };
    foreach (var node in region) {
      CollectArrLocals(node, scan.Inner);
    }
    if (block != null) {
      scan.Block = block.Append(lender).Concat(region).ToList();
      foreach (var statement in block.Where(statement => statement.Kind == "Decl")) {
        scan.Inner[statement.Text!] = statement;   // declared in the same block before the lender: it ends with the same scope
      }
    }
    var saved = arrScan;
    bool savedBorrower = borrowerPass;
    arrScan = scan;
    borrowerPass = false;
    try {
      return check(scan);
    } finally {
      arrScan = saved;
      borrowerPass = savedBorrower;
    }
  }

  static void CollectArrLocals(Node node, Dictionary<string, Node> names) {
    if (node.Kind is "Fun" or "Lambda") {
      return;
    }
    if (node.Kind == "Decl" && node.Text != null) {
      names[node.Text] = node;
    }
    foreach (var child in node.Children) {
      CollectArrLocals(child, names);
    }
  }

  // arr<T> c = b, var v = b[x..y], a = b: a local declared where the lender lives borrows it; the lender can still end with its scope when
  // the borrower never gives the array away (no return, ->, &, pop(&c)) and uses it only in the ways that keep it local (spec 6.8.1)
  bool BorrowerStaysLocal(string borrower, bool plain) {
    if (arrScan == null || borrower == arrScan.Lender || !arrScan.Inner.ContainsKey(borrower)) {
      return false;
    }
    if (!arrScan.Visiting.Add(borrower)) {
      return true;   // already being checked
    }
    bool saved = borrowerPass;
    borrowerPass = true;
    try {
      return arrScan.Region.All(node => ArrayUsesStayLocal(node, null, null, 0, borrower, true, plain));
    } finally {
      borrowerPass = saved;
    }
  }

  // a borrower that is a mixed local (a = b): return a gives the caller a copy of what it borrows, so the lender can still end
  bool MixedBorrower(string borrower) {
    if (arrScan == null || !arrScan.Inner.TryGetValue(borrower, out var decl)) {
      return false;
    }
    if (arrScan.Mixed.TryGetValue(borrower, out bool known)) {
      return known;
    }
    arrScan.Mixed[borrower] = false;
    var holder = arrScan.Block.Contains(decl) ? arrScan.Block : arrScan.Region.Select(node => FindHolder(node, decl)).FirstOrDefault(found => found != null)?.Children;
    if (holder == null) {
      return false;
    }
    int index = holder.IndexOf(decl);
    var scan = arrScan;
    bool savedBorrower = borrowerPass;
    try {
      return scan.Mixed[borrower] = IsMixedLocalArray(decl, holder.Skip(index + 1), holder.Take(index));
    } finally {
      arrScan = scan;
      borrowerPass = savedBorrower;
    }
  }

  bool NoteAssignLent(string borrower) {
    if (!borrowerPass && arrScan != null) {
      arrScan.AssignLent = true;
      arrScan.AssignBorrowers.Add(borrower);
    }
    return true;
  }

  // a = b with a declared outside the lender's block (not a local the scan knows): b may hand its array to a when it ends
  bool NoteOuterBorrower(string borrower) {
    if (borrowerPass || mixedPass || arrScan == null) {
      return false;
    }
    arrScan.OuterBorrower = true;
    arrScan.AssignBorrowers.Add(borrower);
    return true;
  }

  bool NoteInitLent() {
    if (!borrowerPass && arrScan != null) {
      arrScan.InitLent = true;
    }
    return true;
  }

  bool NoteReleased(string name, bool moved = false) {
    if (!borrowerPass && arrScan?.Lender == name) {
      arrScan.Released = true;
      arrScan.MovedOut |= moved;
    }
    return true;
  }

  static bool Mentions(Node node, string name) => node is { Kind: "Name" } && node.Text == name || node.Children.Any(child => Mentions(child, name));

  readonly HashSet<Node> mixedArrayDecls = new();

  // a value an array variable can own: a new array, a moved one (->a) or null
  bool GivesOwnArray(Node value) => value.Kind is "Gift" or "Null" || IsNewArray(value);

  // a value that is a new array nobody else holds: a literal, a clone, arr<T>[n], or a call of a function that returns a new array
  bool IsNewArray(Node? value) =>
    value is { Kind: "List" or "Copy" } || value is { Kind: "Index" } && value.Children[0].Kind == "GenericRef"
    || value is { Kind: "Call" } && CalleesOf(value) is { Count: > 0 } callees && callees.All(ReturnsNewArray)
    || value is { Kind: "Call" } && value.Children[0] is { Kind: "Member", Text: "filter" or "map" };   // a.filter(f), a.map<U>(f): a new array (spec 6.6)

  static bool IsArrTypeNode(Node? type) =>
    type is { Kind: "Type", Text: "arr", Children.Count: 1 } || type is { Kind: "ResolvedType" } && type.Text!.StartsWith("arr<");

  // the uses of an array that keep it local: a[i] (not a view), a.member (not transformTo), the source of a for, an argument to a parameter
  // that keeps it local (ParamStaysLocal), and (for the owner, canReturn) return a, which moves it to the caller
  bool ArrayUsesStayLocal(Node node, Node? parent, Node? grand, int position, string name, bool canReturn, bool plain) {
    if (node is { Kind: "WritableAddressOf", Children: [{ Kind: "Name" } handed] } && handed.Text == name) {
      // f(&a) to a &arr<T> parameter: the function may replace the array, the variable still owns the one it ends with (spec 13.1)
      return !mixedPass && !borrowerPass && parent is { Kind: "Args" } && grand is { Kind: "Call" } && CalleesOf(grand) is { Count: > 0 } writers
        && writers.All(f => IsWritableRefParamNode(f, position)) && NoteReleased(name, true);
    }
    if (node is { Kind: "Call" } && node.Children[0] is { Kind: "Member", Text: "pop" } && node.Children.Count > 1
        && node.Children[1].Children is [{ Kind: "WritableAddressOf" } written] && written.Children[0] is { Kind: "Name" } popped && popped.Text == name) {
      // v.pop(&a): the old array of a ends (a mixed local: when it holds its own); a owns the one it receives
      return canReturn && (!borrowerPass || MixedBorrower(name)) && NoteReleased(name, true) && ArrayUsesStayLocal(node.Children[0], node, parent, 0, name, canReturn, plain);
    }
    if (node is { Kind: "Index", Children: [{ Kind: "Name" } viewed, { Kind: "Range" } range] } && viewed.Text == name) {
      // a view a[x..y] used at once (its len, a copy, the source of a for, an index, a read of the runtime) does not outlive a
      bool used = parent switch {
        { Kind: "Member", Text: "len" } or { Kind: "Copy" or "For" or "In" } => true,
        { Kind: "Index" } => position == 0,
        { Kind: "Args" } => grand is { Kind: "Call" } && IsRuntimeCall(grand),
        { Kind: "Init" } => grand is { Kind: "Decl" } viewDecl && BorrowerStaysLocal(viewDecl.Text!, plain) && NoteInitLent(),   // var v = a[x..y] kept in a local
        _ => false
      };
      return used && ArrayUsesStayLocal(range, node, parent, 1, name, canReturn, plain);
    }
    if (node.Kind == "Name" && node.Text == name) {
      return parent switch {
        { Kind: "Index" } => position == 0 && parent.Children.Count > 1 && parent.Children[1].Kind != "Range",
        { Kind: "Member" } => position == 0 && parent.Text != "transformTo",
        { Kind: "For" or "In" or "Copy" } => true,   // a! only reads it
        { Kind: "Hole" } => position == 0,   // $"{a}" prints it
        { Kind: "Tuple" } => true,   // (a, 1): a tuple that is kept copies it, a tuple given as an argument borrows it
        { Kind: "Gift" } => canReturn && !mixedPass && !borrowerPass && NoteReleased(name, true),   // ->a moves the block out and empties a
        // return a, or return a, n of several values: a moves out (a mixed local: its own array moves, a borrowed one is copied)
        { Kind: "Return" } => canReturn && (!borrowerPass || MixedBorrower(name)),
        { Kind: "Targets" } => canReturn && !borrowerPass && grand is { Kind: "MultiAssign" } && GivesOwnMultiValues(grand) && NoteReleased(name, true),   // a, n = f(): the old block is freed
        { Kind: "Args" } => grand is { Kind: "Call" } && ArgumentStaysLocal(grand, position, plain),
        // a = [..]: the old block is freed; a = b: a borrows from now on. A borrower (borrowerPass) only changes what it borrows
        { Kind: "Assign", Text: "=" } when position == 0 => borrowerPass || canReturn && (GivesOwnArray(parent.Children[1])
          || mixedPass && !Mentions(parent.Children[1], name)) && NoteReleased(name),
        // a = b, a local declared where b lives: a borrows b (spec 6.8.1)
        { Kind: "Assign", Text: "=" } when position == 1 && parent.Children[0] is { Kind: "Name" } borrower && borrower.Text != name
          && BorrowerStaysLocal(borrower.Text!, plain) => NoteAssignLent(borrower.Text!),
        // a = b with a declared outside the lender's block: b hands its array to a when it ends (only a single such a)
        { Kind: "Assign", Text: "=" } when position == 1 && parent.Children[0] is { Kind: "Name" } outer && outer.Text != name
          && arrScan != null && !arrScan.Inner.ContainsKey(outer.Text!) && arrScan.Lender == name => NoteOuterBorrower(outer.Text!),
        { Kind: "Assign", Text: "=" } => plain && position == 1 && parent.Children[0].Kind == "Member",   // obj.field = a: the field gets a copy
        { Kind: "Init" } => grand is { Kind: "Decl" } borrowing && (IsArrTypeNode(borrowing.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)))
          || borrowing.Children.Any(child => child is { Kind: "Type", Text: "var" })) && BorrowerStaysLocal(borrowing.Text!, plain) && NoteInitLent(),   // arr<T> c = a borrows
        _ => false
      };
    }
    if (node.Kind is "Fun" or "Lambda") {
      return true;
    }
    for (int index = 0; index < node.Children.Count; index++) {
      if (!ArrayUsesStayLocal(node.Children[index], node, parent, index, name, canReturn, plain)) {
        return false;
      }
    }
    return true;
  }

  // a call of a function of the standard library written in the runtime (Root.X.f, through an alias of Root)
  bool IsRuntimeCall(Node call) {
    if (Path(call.Children[0]) is not string path) {
      return false;
    }
    string first = path.Split('.')[0];
    if (nameAliases.TryGetValue(first, out var target)) {
      first = target.Split('.')[0];
    }
    return first == "Root";
  }

  // the functions a call can reach, by name (every overload, every class with that method); empty for the functions of the runtime
  List<FunSig> CalleesOf(Node call) {
    var callee = call.Children[0];
    string? name = callee.Kind is "Name" or "Member" ? callee.Text : null;
    if (name == null) {
      return new List<FunSig>();
    }
    int count = call.Children.Count > 1 ? call.Children[1].Children.Count : 0;
    return funs.Values.SelectMany(list => list)
      .Where(f => (f.Name == name || f.Name.EndsWith("." + name)) && f.Params.Count == count && !f.IsExtern && !f.IsAbstract).ToList();
  }

  // plain: the array has plain items, so a ->arr<T> (or ->T) parameter gets a copy of it and the argument is only read
  bool ArgumentStaysLocal(Node call, int position, bool plain) {
    if (IsRuntimeCall(call)) {
      return true;   // r.Console.log(a) and the other functions of Root read their arguments (a borrowed read)
    }
    var callees = CalleesOf(call);
    if (callees.Count == 0 && call.Children[0] is { Kind: "Member", Text: "copyFrom" or "has" or "indexOf" }) {
      return true;   // members of the collections of the compiler that only read the array they are given (spec 6.6)
    }
    return callees.Count > 0 && callees.All(f => IsGiftParamNode(f, position) ? plain : ParamStaysLocal(f, position));
  }

  static bool IsWritableRefParamNode(FunSig f, int position) =>
    f.Node.Children.FirstOrDefault(child => child.Kind == "Params") is Node ps && position < ps.Children.Count && ps.Children[position].Kind == "WritableRefParam";

  static bool IsGiftParamNode(FunSig f, int position) =>
    f.Node.Children.FirstOrDefault(child => child.Kind == "Params") is Node ps && position < ps.Children.Count && ps.Children[position].Kind == "GiftParam";

  readonly Dictionary<(FunSig, int), bool> paramStaysLocal = new();
  readonly HashSet<Node> ownedArrayDecls = new();
  readonly HashSet<Node> tupleArrDecls = new();

  // a borrowed array parameter that its function only reads (the same uses, never returned): an owned array can be given to it
  bool ParamStaysLocal(FunSig f, int position) {
    if (paramStaysLocal.TryGetValue((f, position), out bool known)) {
      return known;
    }
    var param = f.Params[position];
    if (!IsArr(param.Type) || param.IsGift || param.Ref != 0 || param.IsVariadic || f.Node.Children.FirstOrDefault(child => child.Kind == "Block") is not Node body) {
      return paramStaysLocal[(f, position)] = false;
    }
    paramStaysLocal[(f, position)] = true;   // a recursive call is assumed to keep it local
    bool plain = HasPlainItems(param.Type);
    return paramStaysLocal[(f, position)] = WithArrScan(Synthetic("Decl", body, param.Name), new List<Node> { body }, null, _ => ArrayUsesStayLocal(body, null, null, 0, param.Name, false, plain));
  }

  readonly Dictionary<FunSig, bool> returnsNewArray = new();

  // every return of the function gives a new array: a new value, or a local array or ->arr<T> parameter it owns (moved out by the return)
  bool ReturnsNewArray(FunSig f) {
    if (returnsNewArray.TryGetValue(f, out bool known)) {
      return known;
    }
    returnsNewArray[f] = false;   // a recursive call is assumed not to
    if (f.Node.Line >= LibraryLine && IsArr(f.Ret) && f.Name.Split('.')[^1] is "takeAt" or "replace" or "node_take" or "node_replace") {
      return returnsNewArray[f] = true;   // an item handed out by a collection: the caller owns it (spec 6.8.1)
    }
    if (!IsArr(f.Ret) || f.Node.Children.FirstOrDefault(child => child.Kind == "Block") is not Node body) {
      return false;
    }
    var locals = new Dictionary<string, Node>();
    CollectDeclarations(body, locals);
    var returns = CollectReturns(body).ToList();
    bool result = returns.Count > 0 && returns.All(ret => ret.Children.Count == 1 && (IsNewArray(ret.Children[0]) || ret.Children[0].Kind == "Gift"   // return ->a moves it out
      || ret.Children[0] is { Kind: "Name" } returned && (locals.TryGetValue(returned.Text!, out var decl)
        ? IsOwnedLocalArray(decl, new[] { body }) || IsMixedInItsBlock(body, decl)   // a mixed local moves its own array or gives a copy
        : f.Params.FirstOrDefault(p => p.Name == returned.Text) is ParamSig given && OwnsGivenArray(f, given))));
    return returnsNewArray[f] = result;
  }

  // the local is a mixed array where it is declared, as the scope of its block will see it (AddImplicitDrops)
  bool IsMixedInItsBlock(Node body, Node decl) {
    if (FindHolder(body, decl) is not Node holder) {
      return false;
    }
    var statements = holder.Children;
    int index = statements.IndexOf(decl);
    return !IsOwnedLocalArray(decl, statements.Skip(index + 1), statements.Take(index)) && IsMixedLocalArray(decl, statements.Skip(index + 1), statements.Take(index));
  }

  static Node? FindHolder(Node node, Node wanted) {
    if (node.Children.Contains(wanted)) {
      return node;
    }
    foreach (var child in node.Children) {
      if (FindHolder(child, wanted) is Node found) {
        return found;
      }
    }
    return null;
  }

  // a[i] items end with the array: objects dropped, strings freed, arrays of arrays dropped in turn; then the block is freed
  string ArrDropHelper(string type) {
    string elem = ArrElem(type);
    string name = "df_arrdrop_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (arrDropHelpers.Add(name)) {
      string item = IsClass(elem) ? "if (a.p[i] != NULL) { df_drop((void*)a.p[i]); }"
        : elem == "string" ? "df_str_free(a.p[i]);"
        : elem == "string?" ? "df_str_free(a.p[i].val);"
        : IsFunType(elem) ? "df_fn_release(a.p[i]);"
        : IsArr(elem) ? $"{ArrDropHelper(elem)}(a.p[i]);"
        : ItemOwnsParts(elem) ? $"{TupleItemDropHelper(elem)}(a.p[i]);" : "";   // spec 3.2
      string arrType = ArrCType(elem);
      classCode.Append($"static void {name}({arrType} a) {{\n");
      if (item != "") {
        classCode.Append($"  for (df_uns i = 0; i < a.len; i++) {{\n    {item}\n  }}\n");
      }
      classCode.Append("  df_ait_free(a.p);\n}\n\n");   // spec 6.4, 12.5: a view or an iterator that outlives the array keeps the block until it ends
    }
    return name;
  }

  readonly HashSet<string> arrDropHelpers = new();

  // a loop body that declares objects: each pass is a block of its own, so its objects end with the pass
  List<Node> LoopStatements(List<Node> statements) {
    if (statements.Count == 0 || statements.Any(s => s.Kind is "AtEnd" or "AtEndOf" or "Catch" or "Label")) {
      return statements;
    }
    if (!statements.Any(s => (s.Kind == "Decl" && (IsCountedDecl(s) || ObjectTypeOf(s) != null || IsStringDecl(s) || IsOptStringDecl(s) || IsFunDecl(s) || IsStructObjDecl(s) || IsBigStructDecl(s)
          || IsArrTypeNode(s.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)))))
        || (s.Kind == "MultiAssign" && s.Children[0].Children.Any(target => target.Kind == "Decl" && IsStringDecl(target))))) {
      return statements;
    }
    var wrapper = Synthetic("Block", statements[0]);
    wrapper.Children.AddRange(statements);
    return new List<Node> { wrapper };
  }

  // atEnd of a, b { }: runs when the scope ends, while at least one of the listed objects exists (spec 14.2): not when all are null
  static Node NormalizeAtEndOf(Node node) {
    var alive = Synthetic("AnyAlive", node);
    foreach (var name in node.Children.Where(child => child.Kind == "Name")) {
      alive.Add(Synthetic("Name", name, name.Text));
    }
    var guarded = Synthetic("Block", node).Add(Synthetic("If", node).Add(alive).Add(node.Children[^1]));
    return Synthetic("AtEnd", node).Add(guarded);
  }

  // the condition of an atEnd of: a listed variable that can be null (an object, a T?, a counted or weak reference) is alive; one that
  // cannot be null always is
  Expr AnyAlive(Node node, Scope scope) {
    var tests = new List<string>();
    foreach (var name in node.Children) {
      var variable = scope.Find(name.Text!);
      if (variable == null) {
        throw Error($"unknown variable '{name.Text}' in atEnd of", name);
      }
      tests.Add(variable.IsCounted || variable.IsWeak ? $"DF_RC_ALIVE({variable.CName})"
        : IsNullable(variable.Type) ? $"{variable.CName}.has"
        : IsClass(variable.Type) && !IsTraitType(variable.Type) ? $"({variable.CName} != NULL && !DF_IS_DEAD({variable.CName}))"
        : IsTraitType(variable.Type) ? $"({variable.CName} != NULL)"
        : "true");
    }
    return new Expr(tests.Count == 0 ? "true" : "(" + string.Join(" || ", tests) + ")", "bool");
  }

  // ---------------------------------------------------------------- moves and finalizers

  // ->x and ->a.c
  Expr GiftExpr(Node n, Scope scope) {
    var operand = n.Children[0];
    CheckUnrestrictedWrite(operand, scope);   // ->field changes the object (spec 1.6.2)
    if (operand.Kind == "Name" && operand.Text == "this") {
      throw Error("an object cannot give away its own ownership: ->this", n);
    }
    if (operand.Kind is "Name" or "Member" && !LibraryCode && CountedRef(operand, scope) is CountedRefInfo counted) {
      throw PlainGiftOfCounted(counted, operand, n);   // ->rc into a plain receiver (spec 12.7)
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
    if (IsArr(value.Type) && !LibraryCode) {
      return ArrGift(n, operand, value.Type, scope);   // spec 6.8.1
    }
    if (StructHoldsObjects(value.Type) && (!LibraryCode || operand.Kind == "Name")) {
      // spec 11.1.5.3; in the collections of the library, ->value of a ->T parameter leaves it empty (it ends with the function)
      return StructGift(operand, value, scope, n);
    }
    if (!IsClass(value.Type)) {
      if (GiftWarningApplies()) {
        Warn("-> on a basic value is ignored: it is a copy", n, 1);
      }
      return new Expr(value.Code, value.Type);
    }
    var target = LValue(operand, scope);
    CheckMovable(operand, target, scope);
    if (operand.Kind == "Name" && target.Variable != null && !LibraryCode) {
      pendingMoves.Add((target.Variable, $"moved away with ->{operand.Text}"));   // a later use is a compile error (spec 12.4)
    }
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
    CheckUnrestrictedWrite(finalize.Children[0], scope);   // field is atEnd. (spec 1.6.2)
    var subject = finalize.Children[0];
    if (CountedRef(subject, scope) is CountedRefInfo counted) {
      throw Error($"'{counted.Name}' is counted: release its usage with '{counted.Name} is done.'; 'is atEnd.' is only for plain owners (spec 12.4)", finalize);
    }
    if (WeakRef(subject, scope) is CountedRefInfo weak) {
      throw Error($"'{weak.Name}' is a weak reference: release it with '{weak.Name} is done.'; 'is atEnd.' is only for plain owners (spec 12.4)", finalize);
    }
    var target = LValue(subject, scope);
    if (finalize.Text == "implicit" && (!IsClass(target.Type) || target.Variable is { Own: false })) {
      return;   // the end of a var that turned out not to own an object (var s = t! of a string): nothing to end
    }
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
    bool owns = variable != null ? variable.Own || variable.RefSlot : target.IsField;   // a & parameter replaces the caller's object (spec 13.1)
    if (variable is { IsWeak: true }) {
      throw Error($"'{targetNode.Text}' is a weak reference: it takes a counted object marked with % ({targetNode.Text} = %x) or another weak reference (spec 12.7)", at);
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
    if (node.Kind == "MultiAssign" && node.Children[0].Children.Any(target => target.Children.Any(child => child.Kind == "Counted"))
        && node.Children[1].Children.Count != node.Children[0].Children.Count && node.Children[1].Children.Count != 1) {
      throw NotSupported("Counted variables from several return values (a, b = f())", node);
    }
    if (node.Kind == "Decl") {
      var init = node.Children.FirstOrDefault(child => child.Kind == "Init");
      if (init?.Children[0] is { Kind: "CountedOf" } marked) {
        if (marked.Children.Count == 1) {
          init.Children[0] = marked.Children[0];   // @T() timeout 100 keeps its marker node for the timeout (CountedValue)
        }
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
      if (CalleesOf(call) is { Count: > 0 } callees && callees.All(f => f.RetWritable)) {
        node.Add(Synthetic("ReadWrite", node));   // var x = make() with fun &@T make(): x is &@T
      }
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

  string CountedTypeOf(string type, Node at, bool values = false) {
    if (values && IsCountedValueType(type)) {
      return "@" + type;   // @i32, @string: a counted value (spec 12.7)
    }
    if (!IsClass(type)) {
      throw NotSupported(IsCountedValueType(type) ? $"Counted values (@{type}) as fields, results and items (only variables and parameters for now, spec 12.7)" : $"Counted values of type {type} (spec 12.7)", at);
    }
    return "@" + type;
  }

  // a counted variable or a counted field named by a value (through parentheses); Code is the C lvalue of its counting block
  readonly record struct CountedRefInfo(string Code, string Type, string Name, VarInfo? Variable, FieldInfo? Field, bool Writable = false);

  // LValue gives the counting block of a counted field only to the counted assignment and to is done. (spec 12.7)
  bool countedFieldWrite;

  CountedRefInfo? CountedRef(Node value, Scope scope) {
    while (value.Kind == "Paren") {
      value = value.Children[0];
    }
    if (value.Kind == "RawField" && currentClass != null && FindField(currentClass, value.Text!) is { IsCounted: true } storage) {
      return new CountedRefInfo($"self->{storage.CName}", storage.Type, storage.Name, null, storage);   // field in the accessors of a counted property
    }
    if (value.Kind == "Name") {
      var variable = scope.Find(value.Text!);
      if (variable != null) {
        return variable.IsCounted ? new CountedRefInfo(variable.CName, variable.Type, value.Text!, variable, null) : null;
      }
      if (currentClass is { IsTrait: false } && scope.Find("this") != null && FindField(currentClass, value.Text!) is { IsCounted: true, IsStatic: false, HasAccessors: false } own) {
        return new CountedRefInfo($"self->{own.CName}", own.Type, value.Text!, null, own);
      }
      if (currentClass is { IsTrait: false } && FindField(currentClass, value.Text!) is { IsCounted: true, IsStatic: true } ownStatic) {
        return new CountedRefInfo(ownStatic.CName, ownStatic.Type, value.Text!, null, ownStatic);   // a static counted field of the class (spec 12.7)
      }
      return null;
    }
    // Kennel.shared: a static counted field
    if (value is { Kind: "Member", Children: [{ Kind: "Name" } owner] } && scope.Find(owner.Text!) == null && classes.TryGetValue(owner.Text!, out var named)
        && FindField(named, value.Text!) is { IsCounted: true, IsStatic: true } shared) {
      CheckAccess(shared.Visibility, shared.Owner, value.Text!, value);
      return new CountedRefInfo(shared.CName, shared.Type, value.Text!, null, shared);
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
    return FindField(ObjInfo(owner.Type), member.Text) is { IsCounted: true, IsStatic: false, HasAccessors: false } field ? field : null;
  }

  // reading a counted field gives its object
  Expr CountedFieldRead(Expr stored, FieldInfo field, Node? at = null) {
    if (IsCountedValueType(field.Type)) {
      // a counted value field: the value of its cell; a &@string is read through a local that shares it, inside enforce (spec 12.7)
      if (BaseOf(field.Type) == "string" && field.IsWritable && !typingOnly) {
        throw Error($"'{field.Name}' is a &@string: read it through a variable that shares it (&@string s = ...), inside enforce s {{ }} (spec 12.7)", at ?? field.At);
      }
      return new Expr(CellRead(stored.Code, field.Type), field.Type);
    }
    return new($"DF_RC_OBJ({CType(field.Type)}, {stored.Code})", field.Type);
  }

  // return value in a function that returns @T: a counted variable gives its usage (no count change, it is null afterwards, so the end of the
  // scope releases nothing), a counted field adds a user, anything else is taken as by a counted variable (a new object, @plain, null,
  // another counted result)
  string CountedReturnCode(Node value, Scope scope, Node at) {
    string type = CountedBase(currentRet!);
    bool writable = currentFun?.RetWritable == true;   // fun &@T: only read/write usages go out (spec 12.7)
    if (IsCountedValueType(type)) {
      return CountedValueReturn(value, scope, type, writable, at);
    }
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      if (!IsSubtype(counted.Type, type)) {
        throw Error($"'{counted.Name}' is a {counted.Type}, not a {type}", at);
      }
      CheckWritableSource(counted, writable, at);
      return counted.Variable != null ? TakeUsage(counted.Variable) : $"df_rc_use({counted.Code})";
    }
    return CountedValue(value, scope, type, false, at, writable);
  }

  // a counted property read by a counted receiver: the usage its R: gives, or null when value is not one
  string? CountedPropertyUsage(Node value, Scope scope, string type, bool writable, Node at) {
    if (value.Kind is not ("Member" or "Name") || PropertyTarget(value, scope) is not var (_, property) || !property.IsCounted) {
      return null;
    }
    wantCountedUsage = true;
    var got = ExprOf(value, scope);
    wantCountedUsage = false;
    if (!IsCountedType(got.Type) || !IsSubtype(CountedBase(got.Type), type) && BaseOf(CountedBase(got.Type)) != BaseOf(type)) {
      throw Error($"'{property.Name}' gives a counted {CountedBase(got.Type)}, not a {type}", at);
    }
    if (writable && !property.IsWritable) {
      throw Error($"'{property.Name}' gives a read-only @{CountedBase(got.Type)}: a read/write &@{type} needs a &@T property (spec 12.7)", at);
    }
    return got.Code;
  }

  // a variable, parameter or field declared &@T or &%T: read/write, its object has a lock (spec 12.7)
  static bool IsWritableRef(CountedRefInfo info) => info.Writable || info.Variable?.IsWritable == true || info.Field?.IsWritable == true;

  // a value that gives a read/write usage of its own: a call of functions that return &@T, or an item handed out by a collection of
  // read/write objects (replace, takeAt, pop on list<&@T>)
  bool GivesWritableUsage(Node value, Scope scope) {
    if (value.Kind == "CValue") {
      return true;   // made by the compiler: the read/write usage that enforce takes of a &%T
    }
    if (value.Kind != "Call") {
      return false;
    }
    if (value.Children[0] is { Kind: "Member", Text: "replace" or "takeAt" or "pop" } member) {
      try {
        var receiver = ExprOf(member.Children[0], scope);
        string? elem = IsVec(receiver.Type) ? VecElem(receiver.Type) : IsList(receiver.Type) ? ListElem(receiver.Type) : null;
        if (elem != null && BoxInner(elem) != null) {
          return writableBoxes.Contains(elem);
        }
      } catch (CompileException) {
        return false;
      }
    }
    return CalleesOf(value) is { Count: > 0 } callees && callees.All(f => f.RetWritable);
  }

  // x.name = v through a &@T x, or through the read/write usage of a &%T in its enforce: the lock must be held, otherwise the write raises
  // WritingOnRefCountWithoutLockingException (spec 12.7). Writes made by the methods of the object are not checked yet
  string? WriteLockCheck(Node target, Scope scope) {
    if (!LibraryCode && target.Kind == "Name" && scope.Find(target.Text!) is VarInfo cell && IsCountedValueVar(cell) && cell.IsWritable) {
      return $"if (!DF_RC_WRITABLE({cell.CName})) {{ df_raise(&df_desc_WritingOnRefCountWithoutLockingException, {fn!.NameConst}); {RaiseJump} }} ";   // &@i32 n (spec 12.7)
    }
    if (LibraryCode || target.Kind is not ("Member" or "Index")) {
      return null;
    }
    for (var node = target.Children[0]; ; node = node.Children[0]) {
      if (node.Kind is "Name" or "Member" && CountedRef(node, scope) is CountedRefInfo counted && IsWritableRef(counted)) {
        return $"if (!DF_RC_WRITABLE({counted.Code})) {{ df_raise(&df_desc_WritingOnRefCountWithoutLockingException, {fn!.NameConst}); {RaiseJump} }} ";
      }
      if (node.Kind is not ("Member" or "Index" or "Paren") || node.Children.Count == 0) {
        return null;
      }
    }
  }

  // a method of a class of the program that can write its object: not unrestricted and not static (spec 12.7)
  bool MethodNeedsLock(Expr receiver, string name) =>
    !LibraryCode && IsClass(receiver.Type) && classes.TryGetValue(receiver.Type, out var cls) && cls.Node.Line < LibraryLine
      && LookupMethods(ObjInfo(receiver.Type), name).Any(f => !f.IsUnrestricted && !f.IsStatic);

  // the same for the analysis of the exceptions, which knows only the name: some method of the program with that name can write its object
  bool FlowMethodNeedsLock(string name) =>
    funs.Any(entry => entry.Key.EndsWith("." + name) && entry.Value.Any(f => f.Class is { IsTrait: false } && f.Class.Node.Line < LibraryLine && !f.IsUnrestricted && !f.IsStatic));

  // the same seen by the analysis of the exceptions (CodeGen.Leaks.cs): a write whose target goes through a name or a field declared
  // &@T or &%T
  bool FlowWritesThroughLock(Node target, FunSig? fun) {
    if (target.Kind is not ("Member" or "Index" or "Name")) {
      return false;
    }
    bool WritableField(string name) =>
      classes.Values.Any(cls => cls.Fields.Any(field => field.Name == name && field.IsWritable && (field.IsCounted || field.IsWeak)));
    bool WritableName(string name) {
      if (fun == null) {
        return false;
      }
      if (fun.Params.FirstOrDefault(p => p.Name == name) is ParamSig param) {
        return param.IsWritable;
      }
      bool found = false;
      void Walk(Node node) {
        found |= node.Kind == "Decl" && node.Text == name && node.Children.Any(child => child.Kind == "ReadWrite");
        foreach (var child in node.Children) {
          Walk(child);
        }
      }
      Walk(fun.Node);
      return found || fun.Class != null && WritableField(name);
    }
    if (target.Kind == "Name") {
      return WritableName(target.Text!) || WritableField(target.Text!);   // &@i32 n = ..., a &@i32 field by its bare name (spec 12.7)
    }
    if (target.Kind == "Member" && target.Text != null && WritableField(target.Text)) {
      return true;   // k.hits += 1 on a &@i32 field
    }
    for (var node = target.Children[0]; ; node = node.Children[0]) {
      if (node.Kind == "Name" && WritableName(node.Text!) || node.Kind == "Member" && node.Text != null && WritableField(node.Text)) {
        return true;
      }
      if (node.Kind is not ("Member" or "Index" or "Paren") || node.Children.Count == 0) {
        return false;
      }
    }
  }

  // a read/write target takes only read/write sources: a read-only usage cannot become a writing one (spec 12.7)
  void CheckWritableSource(CountedRefInfo source, bool writable, Node at) {
    if (writable && !IsWritableRef(source)) {
      throw Error(IsCountedValueType(source.Type)
        ? $"'{source.Name}' is read-only (@{source.Type}): only a &@{source.Type} gives a read/write one (spec 12.7)"
        : $"'{source.Name}' is read-only (@{source.Type}): only a &@{source.Type} or a &%{source.Type} gives a read/write one (spec 12.7)", at);
    }
  }

  // ->rc into a ->T parameter or a plain receiver: the object is shared, nobody can own it alone (spec 12.7)
  CompileException PlainGiftOfCounted(CountedRefInfo counted, Node source, Node at) {
    string Text(Node n) => n.Kind switch {
      "Name" => n.Text!,
      "Paren" => Text(n.Children[0]),
      "Member" => Text(n.Children[0]) + "." + n.Text,
      "Index" => Text(n.Children[0]) + "[" + (n.Children.Count > 1 && n.Children[1].Kind is "Name" or "Int" ? n.Children[1].Text : "i") + "]",
      _ => counted.Name
    };
    string name = Text(source);
    string way = source.Kind == "Index" ? "removeRefCounting() on a counted variable" : $"{name}.removeRefCounting()";
    return Error($"'{name}' is counted: ->{name} gives its usage only to a counted receiver (@{counted.Type} x = ->{name}, "
      + $"a @T parameter); to take the object out of the counting use {way}, which gives it to the last user (spec 12.7)", at);
  }

  // ->x of a counted variable, field or item (CountedRef) into a counted receiver: its usage leaves it, the source is null afterwards (spec 12.7)
  string TakeSourceUsage(CountedRefInfo source, Node gift, Scope scope) {
    var operand = gift.Children[0];
    while (operand.Kind == "Paren") {
      operand = operand.Children[0];
    }
    if (source.Variable != null) {
      return TakeUsage(source.Variable);
    }
    return source.Field == null && operand.Kind == "Index" ? TakeItemUsage(gift, operand, scope) : TakeFieldUsage(operand, scope);
  }

  // ->pack[i] of a collection of counted objects: as on plain collections a list loses the node and an arr or a vec keeps null in the
  // slot; the usage of the item goes to the receiver, no count change (spec 12.7)
  bool countedItemGift;

  string TakeItemUsage(Node gift, Node operand, Scope scope) {
    bool saved = countedItemGift;
    countedItemGift = true;
    Expr taken;
    try {
      taken = GiftIndex(gift, operand, scope);
    } finally {
      countedItemGift = saved;
    }
    return BoxInner(taken.Type) != null ? UnboxTaken(taken).Code : taken.Code;
  }

  // ->k.field: the usage of a counted field leaves it, the field becomes null (spec 12.7)
  string TakeFieldUsage(Node field, Scope scope) {
    var target = CountedFieldTarget(field, scope);
    if (target.IsConst) {
      throw Error($"'{field.Text}' is const: its usage cannot be moved", field);
    }
    string slot = NewTemp();
    string taken = NewTemp();
    return $"({{ df_rc** {slot} = &({target.Code}); df_rc* {taken} = *{slot}; *{slot} = NULL; {taken}; }})";
  }

  // the usage of a counted variable leaves it: the variable becomes null
  string TakeUsage(VarInfo counted) {
    if (!LibraryCode) {
      pendingMoves.Add((counted, "given away with ->"));   // its usage left: a later use is a compile error (spec 12.7)
    }
    string taken = NewTemp();
    return $"({{ df_rc* {taken} = {counted.CName}; {counted.CName} = NULL; {taken}; }})";
  }

  // an argument for a @T parameter: f(rc) adds a user for the call, f(->rc) gives the caller's usage, f(Dog()) and f(@plain) start a block
  string CountedArg(Node arg, string type, Scope scope, bool writable = false) {
    if (IsCountedValueType(type)) {
      return CountedCellValue(arg, scope, type, writable, arg);   // @i32 n: shares the cell, a value makes a new one (spec 12.7)
    }
    if (arg.Kind == "Gift" && CountedRef(arg.Children[0], scope) is CountedRefInfo given) {
      if (!IsSubtype(given.Type, type)) {
        throw Error($"'{given.Name}' is a {given.Type}, not a {type}", arg);
      }
      CheckWritableSource(given, writable, arg);
      return TakeSourceUsage(given, arg, scope);
    }
    return CountedValue(arg, scope, type, false, arg, writable);
  }

  // the cost of an argument in the overload choice for the counted cases: -1 not a counted case (the usual rules decide), -2 it cannot be
  // passed (a counted argument to a plain parameter, a plain object to a @T parameter)
  int CountedArgCost(Node arg, string paramType, Scope scope) {
    if (arg.Kind == "WritableAddressOf" && arg.Children[0].Kind is "CountedOf" or "WeakOf") {
      arg = arg.Children[0];   // &@x, &%x: the marker of a read/write parameter (spec 13.1)
    }
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
      if (CountedRef(arg, scope) is CountedRefInfo valueArg && IsCountedValueRef(valueArg)) {
        return -1;   // a counted value read as a plain value
      }
      bool countedArg = CountedRef(arg, scope) != null || arg.Kind == "CountedOf" || (arg.Kind == "Gift" && CountedRef(arg.Children[0], scope) != null);
      return countedArg ? -2 : -1;
    }
    string type = CountedBase(paramType);
    if (IsCountedValueType(type)) {
      var cellArg = arg.Kind is "Gift" or "CountedOf" ? arg.Children[0] : arg;
      if (arg.Kind == "Null") {
        return 50;
      }
      if (CountedRef(cellArg, scope) is CountedRefInfo cellRef) {
        return BaseOf(cellRef.Type) == BaseOf(type) ? 0 : -2;
      }
      string given = ExprOf(cellArg, scope).Type;
      return BaseOf(given) == BaseOf(type) ? 5 : IsNumeric(BaseOf(given)) && IsNumeric(BaseOf(type)) ? 20 : -2;
    }
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
  string CountedValue(Node value, Scope scope, string type, bool marked, Node at, bool writable = false) {
    if (CountedPropertyUsage(value.Kind == "CountedOf" && value.Children.Count == 1 ? value.Children[0] : value, scope, type, writable, at) is string usage) {
      return usage;   // @Dog b = k.boss, keep(@k.boss): the usage that R: gives (spec 11.1.4)
    }
    if (value is { Kind: "CountedOf", Children.Count: 2 } timed) {
      // @Player() timeout 100: the default wait for the lock of a read/write object, in milliseconds (spec 12.7)
      if (!writable) {
        throw Error($"timeout is the wait for a lock: only a read/write &@{type} has one (spec 12.7)", at);
      }
      var ms = ExprOf(timed.Children[1].Children[0], scope, "i64");
      if (!IsInt(ms.Type)) {
        throw Error($"timeout takes a whole number of milliseconds, not {ms.Type}", at);
      }
      return $"df_rc_timeout({CountedValue(timed.Children[0], scope, type, true, at, writable)}, (int64_t)({ms.Code}))";
    }
    if (value.Kind == "CountedOf") {
      value = value.Children[0];
      marked = true;
    }
    if (value.Kind == "Null") {
      return "NULL";
    }
    if (value.Kind == "Gift" && CountedRef(value.Children[0], scope) is CountedRefInfo source) {
      // ->a, ->k.field: the usage moves, the source is null afterwards (spec 12.7)
      if (!IsSubtype(source.Type, type)) {
        throw Error($"'{source.Name}' is a {source.Type}, not a {type}", at);
      }
      CheckWritableSource(source, writable, at);
      return TakeSourceUsage(source, value, scope);
    }
    if (CountedRef(value, scope) is CountedRefInfo other) {
      if (!IsSubtype(other.Type, type)) {
        throw Error($"'{other.Name}' is a {other.Type}, not a {type}", at);
      }
      CheckWritableSource(other, writable, at);
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
      if (writable && !GivesWritableUsage(value, scope)) {
        throw Error($"this is a read-only @{CountedBase(given.Type)}: a read/write &@{type} needs a &@T result (spec 12.7)", at);
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
    string code = CountedValue(s.Children[1], scope, variable.Type, false, s, variable.IsWritable);
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
    bool writableField = CountedRef(s.Children[0], scope) is CountedRefInfo field && IsWritableRef(field);
    string code = CountedValue(s.Children[1], scope, target.Type, false, s, writableField);
    string fresh = NewTemp();
    string slot = NewTemp();
    string old = NewTemp();
    return CheckRaise($"({{ df_rc* {fresh} = {code}; df_rc** {slot} = &({target.Code}); df_rc* {old} = *{slot}; *{slot} = {fresh}; df_rc_release({old}); }})", "void");
  }

  // x is done.: releases the usage early; x becomes null and the end of the scope (or of the holder, for a field) releases nothing
  void EmitDone(Node done, StringBuilder sb, Scope scope, string pad) {
    CheckUnrestrictedWrite(done.Children[0], scope);   // field is done. (spec 1.6.2)
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
      throw Error("'is done.' releases a usage or a weak reference (@T, %T); a plain object ends with 'is atEnd.' (spec 12.7)", done);
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
    bool isValue = IsCountedValueType(type);
    if (!IsClass(type) && !isValue) {
      throw NotSupported($"Counted values of type {type} (spec 12.7)", s);
    }
    if (!frames.Peek().Hoisted.TryGetValue(s.Text!, out var variable) || !variable.IsCounted) {
      throw NotSupported("A counted variable in this place (only in blocks and function bodies for now)", s);
    }
    string code = isValue
      ? init == null ? (IsNullable(type) ? "NULL" : NewCell(new Expr(ZeroValue(BaseOf(type)), BaseOf(type)), type, s))
        : CountedCellValue(init.Children[0], scope, type, variable.IsWritable, init)   // @i32 n = 5, @i32 m = n (spec 12.7)
      : init == null ? "NULL" : CountedValue(init.Children[0], scope, type, init.Text == "marked", init, variable.IsWritable);
    sb.Append($"{pad}{variable.CName} = {code};\n");
    scope.Vars[s.Text!] = variable;
    assign.Unassigned.Remove(variable);
  }

  // a counted object cannot be borrowed (&, plain T) or held by a plain variable or field: it is used through a new usage (spec 12.7)
  void CheckNotBorrowOfCounted(Node value, Scope scope, string target, Node at) {
    if (CountedRef(value, scope) is CountedRefInfo counted && !IsCountedValueRef(counted)) {
      throw Error($"'{counted.Name}' is counted and cannot be borrowed: take another usage with @{counted.Type} {target} = ... (spec 12.7)", at);
    }
  }

  // & on a counted object is a compile error: writing needs a read/write usage, &@T (spec 12.7)
  void CheckNotWritableBorrowOfCounted(Node value, Scope scope, Node at) {
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      throw Error($"& is not allowed on the counted object '{counted.Name}': writing needs a read/write usage, &@T (spec 12.7)", at);
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
    string field = member.Text == "usersCount" ? "users" : "weaks";
    if (IsCountedValueRef(counted)) {
      return new Expr($"({{ df_rc* {block} = {counted.Code}; {block} != NULL ? (uint64_t){block}->{field} : (uint64_t)0; }})", "u64");   // spec 12.7
    }
    string objectCode = $"DF_RC_OBJ({CType(counted.Type)}, {block})";
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
          Warn($"possible cycle of counted objects: {links}; objects in a cycle are never destroyed (zombie objects): make one of the links a weak reference (%T)", first.At, 1);
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
  string WeakValue(Node value, Scope scope, string type, Node at, bool writable = false) {
    if (value.Kind == "Null") {
      return "NULL";
    }
    if (value.Kind == "CValue") {
      return value.Text!;   // made by the compiler: the weak binding of a type check (spec 12.7)
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
      CheckWritableSource(found, writable, at);   // &%T w = _x: x is &@T or &%T
      return $"df_rc_weak({found.Code})";
    }
    if (WeakRef(value, scope) is CountedRefInfo weak) {
      if (!IsSubtype(weak.Type, type)) {
        throw Error($"'{weak.Name}' is a {weak.Type}, not a {type}", at);
      }
      CheckWritableSource(weak, writable, at);
      return $"df_rc_weak({weak.Code})";
    }
    if (CountedRef(value, scope) is CountedRefInfo counted) {
      throw Error($"a weak reference is made with the marker: %{counted.Name} (spec 12.7)", at);
    }
    throw Error("a weak reference takes %x of a counted object, another weak reference or null (spec 12.7)", at);
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
    string code = WeakValue(s.Children[1], scope, target.Type, s, WeakRef(s.Children[0], scope) is CountedRefInfo weakTarget && IsWritableRef(weakTarget));
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
    string code = init == null || init.Text == "default" ? "NULL" : WeakValue(init.Children[0], scope, type, init, variable.IsWritable);
    sb.Append($"{pad}{variable.CName} = {code};\n");
    scope.Vars[s.Text!] = variable;
    assign.Unassigned.Remove(variable);
  }

  // enforce w { }: inside the block w is a counted variable of its own, a temporary usage taken when the block starts and released on every
  // way out of it (the usual release of a counted variable at the end of its scope)
  Node EnforcedWeakBlock(Node block, string name, string type, string blockCode, bool writable) {
    var use = Synthetic("Decl", block, name).Add(Synthetic("Type", block, type)).Add(Synthetic("Counted", block)).Add(Synthetic("EnforceUse", block))
      .Add(Synthetic("Init", block).Add(Synthetic("CValue", block, $"df_rc_use({blockCode})").Add(Synthetic("Type", block, "@" + type))));
    if (writable) {
      use.Add(Synthetic("ReadWrite", block));   // &%T: the temporary usage is read/write, the enforce holds the lock (spec 12.7)
    }
    var wrapped = Synthetic("Block", block).Add(use);
    wrapped.Children.AddRange(block.Children);
    return wrapped;
  }

  // a weak field is read through enforce, or copied into another weak reference
  void CheckWeakFieldRead(FieldInfo? field, Node at) {
    if (field is { IsWeak: true }) {
      throw at.Kind == "Member"
        ? Error($"'{field.Name}' is a weak reference: copy it into a weak variable (%{field.Type} {field.Name} = ...) and use that through enforce", at)
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
      .Where(field => !field.IsStatic && field.Type == "string" && !field.IsCounted).ToList();
  }

  List<FieldInfo> OwnedObjectFields(ClassInfo cls) {
    return IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields)
      .Where(field => !field.IsStatic && IsClass(field.Type) && !field.IsParentRef && !field.IsWeak && !field.IsCounted).ToList();
  }

  void EmitDropGlue(ClassInfo cls) {
    if (cls.Node.Line < LibraryLine) {
      foreach (var field in cls.Fields.Where(field => IsArr(field.Type) && !field.IsStatic)) {
        ArrDropHelper(field.Type);   // the helpers are written before the drop function that calls them
      }
      foreach (var field in cls.Fields.Where(field => TupleHoldsArrays(field.Type) && !field.IsStatic)) {
        TupleDropCode(field.Type, "self");   // the same for the arrays of a tuple field
      }
      foreach (var field in cls.Fields.Where(field => StructHoldsObjects(field.Type) && !field.IsStatic)) {
        StructDropHelper(field.Type);   // and for a struct field that holds strings or objects (spec 11.1.5.3)
      }
    }
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
    foreach (var field in IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields).Where(field => !field.IsStatic && field.Type == "string?" && !field.IsCounted)) {
      sb.Append($"  df_opt_str_drop(&self->{field.CName});\n");   // spec 7.1
    }
    EmitArrayFieldDrops(cls, sb);   // spec 6.8.1 (CodeGen.ArrOwnership.cs)
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
