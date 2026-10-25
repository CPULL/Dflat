using System.Text;
using dfparser;

namespace dfcompiler;

sealed class ImplInfo {
  public ImplInfo(ClassInfo trait, List<FunSig> methods) {
    Trait = trait;
    Methods = methods;
  }

  public ClassInfo Trait { get; }
  public List<FunSig> Methods { get; }   // the function that answers each method of the trait, in the order of the trait
}

// Traits (spec 11.2): properties, methods with or without a body, a constructor without parameters, one parent trait.
// A value of a trait type is the object itself (a pointer); a call finds the implementation in the class descriptor
// (dynamic dispatch), and a property is found by its offset in the object.
public sealed partial class CodeGen {
  readonly Dictionary<string, ClassInfo> traits = new();
  readonly List<ClassInfo> traitOrder = new();        // parent traits before child traits

  bool IsTraitType(string type) => traits.ContainsKey(type);

  ClassInfo ObjInfo(string type) => classes.TryGetValue(type, out var cls) ? cls : traits[type];

  // up the hierarchy: class to parent class, class to the traits it implements, trait to its parent trait
  bool IsSubtype(string from, string to) {
    if (from == to) {
      return true;
    }
    if (classes.TryGetValue(from, out var fromClass)) {
      if (IsSubclass(from, to)) {
        return true;
      }
      return traits.TryGetValue(to, out var target) && AllTraits(fromClass).Contains(target);
    }
    if (traits.TryGetValue(from, out var fromTrait) && traits.TryGetValue(to, out var targetTrait)) {
      for (var t = fromTrait; t != null; t = t.Super) {
        if (t == targetTrait) {
          return true;
        }
      }
    }
    return false;
  }

  // the listed traits and their parents, parents first, each once
  List<ClassInfo> TraitClosure(IEnumerable<ClassInfo> direct) {
    var result = new List<ClassInfo>();
    void Add(ClassInfo trait) {
      if (trait.Super != null) {
        Add(trait.Super);
      }
      if (!result.Contains(trait)) {
        result.Add(trait);
      }
    }
    foreach (var trait in direct) {
      Add(trait);
    }
    return result;
  }

  // every trait of a class, those of its parents first
  List<ClassInfo> AllTraits(ClassInfo cls) {
    var chain = new List<ClassInfo>();
    for (var c = cls; c != null; c = c.Parent) {
      chain.Insert(0, c);
    }
    return TraitClosure(chain.SelectMany(c => c.Traits));
  }

  // the traits this class adds to what its parent already implements: their properties live in this class
  List<ClassInfo> IntroducedTraits(ClassInfo cls) {
    var inherited = cls.Parent != null ? AllTraits(cls.Parent) : new List<ClassInfo>();
    return AllTraits(cls).Where(trait => !inherited.Contains(trait)).ToList();
  }

  // ---------------------------------------------------------------- registering

  void DeclareTraits(Node program) {
    foreach (var node in program.Children.Where(child => child.Kind == "Trait")) {
      string name = node.Text!;
      if (classes.ContainsKey(name) || traits.ContainsKey(name) || enums.ContainsKey(name) || IsKnownType(name) || genericClasses.ContainsKey(name)) {
        throw Error($"the name '{name}' is already used by a type", node);
      }
      if (node.Children.Any(child => child.Kind is "GenericParams" or "Where")) {
        throw NotSupported("Generic traits", node);
      }
      traits[name] = new ClassInfo(name, node, traits.Count) { IsTrait = true };
    }
  }

  void RegisterTraits() {
    foreach (var trait in traits.Values) {
      var bases = trait.Node.Children.FirstOrDefault(child => child.Kind == "Bases");
      if (bases == null) {
        continue;
      }
      var baseType = bases.Children[0];
      string parentName = baseType.Text ?? "";
      if (baseType.Kind == "Type" && baseType.Children.Count > 0 && genericClasses.TryGetValue(parentName, out var genericParent)
          && genericParent is { IsTrait: true, HasPlaceholders: false }) {
        // trait Indexed<Item> : Container<Item>: the copy of the generic parent for these arguments (spec 11.3.2)
        parentName = ClassInstanceName(genericParent, TypeArgsOf(baseType.Children, genericParent.Params), baseType);
      } else if (baseType.Children.Count > 0 || baseType.Kind != "Type") {
        throw NotSupported("Generic parent traits", baseType);
      }
      trait.Super = traits.TryGetValue(parentName, out var super)
        ? super
        : throw Error($"unknown parent trait '{baseType.Text}'", baseType);
    }
    foreach (var trait in traits.Values) {
      for (var t = trait.Super; t != null; t = t.Super) {
        if (t == trait) {
          throw Error($"trait '{trait.Name}' extends itself", trait.Node);
        }
      }
    }
    var seen = new HashSet<ClassInfo>();
    void Order(ClassInfo trait) {
      if (seen.Add(trait)) {
        if (trait.Super != null) {
          Order(trait.Super);
        }
        traitOrder.Add(trait);
      }
    }
    foreach (var trait in traits.Values) {
      Order(trait);
    }
    foreach (var trait in traitOrder) {
      RegisterTraitMembers(trait);
    }
  }

  void RegisterTraitMembers(ClassInfo trait) {
    RejectAtEnd(trait.Node);
    var body = trait.Node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children) {
      switch (member.Kind) {
        case "Decl": {
          var (visibility, _, isStatic, isConst, isOverride) = ReadMods(member);
          if (isStatic || isConst || isOverride) {
            throw Error("a trait property cannot be static, const or override", member);
          }
          if (ModsOf(member).Any(mod => mod is "asparent" or "unrestricted")) {
            throw Error("'asparent' and 'unrestricted' apply to functions", member);
          }
          if (member.Children.Any(child => child.Kind == "Size")) {
            throw NotSupported("Sized declarations (arr<T>[n] name)", member);
          }
          var init = member.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
          if (init?.Kind == "WeakInit") {
            throw NotSupported("Ownership", init);
          }
          var typeNode = member.Children.First(child => TypeNodeKinds.Contains(child.Kind));
          if (typeNode is { Kind: "Type", Text: "var" }) {
            throw Error("'var' cannot be used for a property: write its type", member);
          }
          if (FieldCandidates(trait, member.Text!).Any()) {
            throw Error($"the property '{member.Text}' is already declared in {trait.Name} or its parent trait", member);
          }
          // private (no keyword, spec 1.6.1): only the code of the trait and, read-only, its sub-traits (spec 11.2)
          trait.Fields.Add(new FieldInfo(member.Text!, ResolveType(typeNode), trait, member) {
            Visibility = visibility,
            Init = init?.Children[0]
          });
          break;
        }
        case "Constructor": {
          var parameters = member.Children.First(child => child.Kind == "Params");
          if (parameters.Children.Count > 0) {
            throw NotSupported("Trait constructors with parameters (they need secondary constructors)", member);
          }
          if (member.Children.Any(child => child.Kind == "Chain") || trait.CtorBlock != null) {
            throw NotSupported("Trait constructors that chain or repeat", member);
          }
          trait.CtorBlock = member.Children.FirstOrDefault(child => child.Kind == "Block");
          RejectAtEnd(member);
          break;
        }
        case "Fun":
          break;                                   // after the properties
        case "FunVar":
          throw NotSupported("Lambdas", member);
        case "Placeholder":
          throw NotSupported("Placeholders (generics)", member);
        default:
          throw Error($"'{member.Kind}' is not allowed in a trait", member);
      }
    }
    var members = body.Children.Where(member => member.Kind == "Fun").ToList();
    foreach (var member in members.ToList()) {
      if (member.Children.Any(child => child.Kind is "ExecuteBefore" or "ExecuteAfter")) {
        members.Insert(members.IndexOf(member), SplitExecuteBlocks(trait, member));
      }
    }
    foreach (var member in members) {
      RejectAtEnd(member);
      var (visibility, explicitVisibility, isStatic, isConst, isOverride) = ReadMods(member);
      if (isStatic || isConst) {
        throw Error("a trait function cannot be static", member);
      }
      if (isOverride) {
        throw NotSupported("A trait that overrides a function of its parent trait", member);
      }
      if (member.Text == "==") {
        throw NotSupported("The equality operator (fun bool ==(V o))", member);
      }
      if (FieldCandidates(trait, member.Text!).Any()) {
        throw Error($"'{member.Text}' is already a property of {trait.Name}", member);
      }
      var mods = ModsOf(member);
      if (mods.Count(mod => mod is "public" or "internal" or "private" or "asparent" or "unrestricted") > 1) {
        throw Error("a function has one visibility: public, internal, private, asparent or unrestricted", member);
      }
      var f = RegisterFun(member, null, trait)!;
      f.Class = trait;
      f.IsAbstract = member.Children.Any(child => child.Kind == "NoBody");
      f.Visibility = visibility;   // private (no keyword, spec 1.6.1): only the trait, its sub-traits and the class that implements it
      f.IsAsParent = mods.Contains("asparent");
      f.IsUnrestricted = mods.Contains("unrestricted");
      f.TSlot = trait.TraitMethods.Count;
      trait.TraitMethods.Add(f);
      for (var up = trait.Super; up != null; up = up.Super) {
        if (up.TraitMethods.Any(g => g.Name.EndsWith("." + member.Text) && ParamTypes(g).SequenceEqual(ParamTypes(f)))) {
          throw NotSupported($"A trait that redeclares '{member.Text}' of its parent trait {up.Name}", member);
        }
      }
    }
    RegisterTraitInit(trait);
  }

  // ---------------------------------------------------------------- executeBefore / executeAfter (spec 11.2)

  const string ImplPrefix = "__impl_";   // a name no program can write (a name cannot start with _)

  // fun R m(params) executeBefore { B } executeAfter { A } becomes two trait functions:
  //   __impl_m(params): what the class implements (its body is the default body of m, if any); private
  //   m(params) { B; var __result = __impl_m(params); A; return __result }: the default of the trait, the only m there is
  // so B and A run on every call of m, cannot be overridden, and A runs only when the implementation succeeded. The class writes
  // 'override fun m', which RegisterMethods turns into __impl_m. Returns the node of __impl_m.
  Node SplitExecuteBlocks(ClassInfo trait, Node member) {
    string name = member.Text!;
    if (member.Children.Any(child => child.Kind == "GenericParams")) {
      throw NotSupported("executeBefore / executeAfter on a generic function", member);
    }
    var returns = member.Children.FirstOrDefault(child => child.Kind == "Returns");
    if (returns != null && returns.Children.Count > 1) {
      throw NotSupported("executeBefore / executeAfter on a function with several return values", member);
    }
    var before = member.Children.FirstOrDefault(child => child.Kind == "ExecuteBefore")?.Children[0];
    var after = member.Children.FirstOrDefault(child => child.Kind == "ExecuteAfter")?.Children[0];
    var parameters = member.Children.First(child => child.Kind == "Params");
    var implementation = Synthetic("Fun", member, ImplPrefix + name);
    foreach (var child in member.Children.Where(child => child.Kind is "Returns" or "Params" or "Block" or "NoBody")) {
      implementation.Add(child);
    }
    trait.Hooked.Add(name);

    var args = Synthetic("Args", member);
    foreach (var p in parameters.Children) {
      Node arg = Synthetic("Name", p, p.Text);
      if (p.Kind == "WritableRefParam") {
        arg = Synthetic("WritableAddressOf", p).Add(arg);
      } else if (p.Kind == "GiftParam") {
        arg = Synthetic("Gift", p).Add(arg);
      }
      args.Add(arg);
    }
    var call = Synthetic("Call", member).Add(Synthetic("Name", member, ImplPrefix + name)).Add(args);
    var block = Synthetic("Block", member);
    if (before != null) {
      foreach (var statement in before.Children) {
        block.Add(statement);
      }
    }
    if (returns == null) {
      block.Add(Synthetic("ExprStmt", member).Add(call));
    } else {
      block.Add(Synthetic("Decl", member, "__result").Add(Synthetic("Type", member, "var")).Add(Synthetic("Init", member).Add(call)));
    }
    if (after != null) {
      foreach (var statement in after.Children) {
        block.Add(ResultReads(statement, name, returns != null));
      }
    }
    if (returns != null) {
      block.Add(Synthetic("Return", member).Add(Synthetic("Name", member, "__result")));
    }
    member.Children.RemoveAll(child => child.Kind is "Block" or "NoBody" or "ExecuteBefore" or "ExecuteAfter");
    member.Add(block);
    return implementation;
  }

  // m.result in an executeAfter block: the value the implementation returned, read-only
  Node ResultReads(Node node, string method, bool hasResult) {
    bool IsResult(Node n) => n is { Kind: "Member", Text: "result" } && n.Children[0] is { Kind: "Name" } owner && owner.Text == method;
    if (node.Kind is "Assign" or "PreInc" or "PostInc" && node.Children.Count > 0 && IsResult(node.Children[0])
        || node.Kind == "WritableAddressOf" && IsResult(node.Children[0]) || node.Kind == "Gift" && IsResult(node.Children[0])) {
      throw Error($"{method}.result is read-only", node);
    }
    if (IsResult(node)) {
      return hasResult ? Synthetic("Name", node, "__result") : throw Error($"{method} returns nothing: there is no {method}.result", node);
    }
    for (int i = 0; i < node.Children.Count; i++) {
      node.Children[i] = ResultReads(node.Children[i], method, hasResult);
    }
    return node;
  }

  // the property initializers and the constructor of a trait run in a function of the trait (__tinit), so they see its private members
  void RegisterTraitInit(ClassInfo trait) {
    var statements = new List<Node>();
    foreach (var field in trait.Fields.Where(field => field.Init != null)) {
      statements.Add(Assignment(field.Name, field.Init!, field.At, false));
    }
    if (trait.CtorBlock != null) {
      statements.AddRange(trait.CtorBlock.Children);
    }
    if (statements.Count == 0) {
      return;
    }
    var block = Synthetic("Block", trait.Node);
    foreach (var statement in statements) {
      block.Add(statement);
    }
    var node = Synthetic("Fun", trait.Node, "__tinit").Add(Synthetic("Params", trait.Node)).Add(block);
    var f = RegisterFun(node, null, trait)!;
    f.Class = trait;
    trait.InitFun = f;
  }

  // for every class: what answers each method of each trait it implements
  void BuildImpls(ClassInfo cls) {
    foreach (var trait in AllTraits(cls)) {
      var methods = new List<FunSig>();
      foreach (var method in trait.TraitMethods) {
        FunSig? implementation = null;
        for (var c = cls; c != null && implementation == null; c = c.Parent) {
          if (funs.TryGetValue(c.Name + "." + method.Name[(method.Name.IndexOf('.') + 1)..], out var candidates)) {
            implementation = candidates.FirstOrDefault(g => !g.IsStatic && ParamTypes(g).SequenceEqual(ParamTypes(method)) && g.Ret == method.Ret);
          }
        }
        if (implementation == null && !method.IsAbstract) {
          implementation = method;                 // the default implementation of the trait
        }
        if (implementation == null) {
          string shown = method.Name[(method.Name.IndexOf('.') + 1)..];
          throw Error($"class '{cls.Name}' must implement {trait.Name}.{(shown.StartsWith(ImplPrefix) ? shown[ImplPrefix.Length..] : shown)}", cls.Node);
        }
        methods.Add(implementation);
      }
      cls.Impls.Add(new ImplInfo(trait, methods));
    }
  }

  // ---------------------------------------------------------------- properties of traits

  // every field of this name visible from a class or a trait: its own, its parents', those of the traits it implements
  // withPrivate: also the private properties of the traits of a class (the C glue of the collections)
  IEnumerable<FieldInfo> FieldCandidates(ClassInfo start, string name, bool withPrivate = false) {
    if (start.IsTrait) {
      for (var t = start; t != null; t = t.Super) {
        foreach (var field in t.Fields.Where(f => f.Name == name)) {
          yield return field;
        }
      }
      yield break;
    }
    for (var c = start; c != null; c = c.Parent) {
      foreach (var field in c.Fields.Where(f => f.Name == name)) {
        yield return field;
      }
      // the private properties of a trait are not seen from the class: two traits with the same private name never clash (spec 11.2)
      foreach (var trait in TraitClosure(c.Traits)) {
        foreach (var field in trait.Fields.Where(f => f.Name == name && (f.Visibility != Vis.Private || withPrivate || LibraryCode))) {
          yield return field;
        }
      }
    }
  }

  // a property of a value of a trait type: found through the offsets table of the class of the object
  string TraitFieldCode(string objectCode, FieldInfo field) {
    var trait = field.Owner;
    int index = trait.Fields.Where(f => !f.IsStatic).ToList().IndexOf(field);
    string temp = NewTemp();
    return $"(*({{ void* {temp} = (void*)({objectCode}); ({CType(field.Type)}*)((char*){temp} + df_impl_of({temp}, &df_trait_{trait.Name})->offsets[{index}]); }}))";
  }

  // TraitName.name(args) inside a class that implements the trait: the function of the trait itself
  // (its default body), not the one the class provides
  Expr TraitDirectCall(ClassInfo trait, string name, List<Node> args, Scope scope, Node at) {
    var thisType = scope.Find("this")?.Type;
    if (thisType == null || !IsSubtype(thisType, trait.Name)) {
      throw Error($"{trait.Name}.{name}(...) can only be written inside a class that implements {trait.Name}", at);
    }
    var candidates = LookupMethods(trait, name).Where(f => !f.IsStatic).ToList();
    if (candidates.Count == 0) {
      throw Error($"the trait {trait.Name} has no function '{name}'", at);
    }
    var chosen = ResolveOverload(candidates, args, scope, at);
    if (chosen.IsAbstract) {
      throw Error($"{chosen.Class!.Name}.{name} has no body", at);
    }
    var codes = ArgCodes(chosen, args, scope, at);
    codes.Insert(0, "(void*)self");
    string call = $"{chosen.CName}({string.Join(", ", codes)})";
    return new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned);
  }

  // x.TraitName: the object seen as that trait
  Expr? TraitView(Expr obj, string traitName, Node at) {
    if (!traits.TryGetValue(traitName, out var trait)) {
      return null;
    }
    if (!IsSubtype(obj.Type, traitName)) {
      throw Error($"{obj.Type} does not implement the trait {traitName}", at);
    }
    return new Expr(obj.Code, traitName);
  }

  // ---------------------------------------------------------------- construction

  // the traits this class introduces, parent traits first: a call of TraitName.__tinit() each (property initializers, then the constructor body)
  List<Node> TraitInitStatements(ClassInfo cls, Node at) {
    var statements = new List<Node>();
    foreach (var trait in IntroducedTraits(cls).Where(trait => trait.InitFun != null)) {
      var callee = Synthetic("Member", at, "__tinit").Add(Synthetic("Name", at, trait.Name));
      statements.Add(Synthetic("ExprStmt", at).Add(Synthetic("Call", at).Add(callee).Add(Synthetic("Args", at))));
    }
    return statements;
  }

  // ---------------------------------------------------------------- tables

  void EmitTraitTables() {
    foreach (var trait in traitOrder) {
      classCode.Append($"static const df_trait df_trait_{trait.Name} = {{ {2048 + trait.Index}, \"{trait.Name}\" }};\n");
    }
    classCode.Append('\n');
  }

  // the implementations table of a class: one entry per trait
  string EmitImpls(ClassInfo cls) {
    if (cls.Impls.Count == 0) {
      return "NULL, 0";
    }
    var sb = classCode;
    var entries = new List<string>();
    foreach (var impl in cls.Impls) {
      string methods = "NULL";
      if (impl.Methods.Count > 0) {
        methods = $"df_im_{cls.Name}_{impl.Trait.Name}";
        sb.Append($"static const void* const {methods}[] = {{ {string.Join(", ", impl.Methods.Select(f => $"(const void*){f.CName}"))} }};\n");
      }
      string offsets = "NULL";
      var properties = impl.Trait.Fields.Where(field => !field.IsStatic).ToList();
      if (properties.Count > 0) {
        offsets = $"df_of_{cls.Name}_{impl.Trait.Name}";
        sb.Append($"static const uint32_t {offsets}[] = {{ {string.Join(", ", properties.Select(field => $"(uint32_t)offsetof(struct {cls.StructName}, {field.CName})"))} }};\n");
      }
      entries.Add($"{{ &df_trait_{impl.Trait.Name}, {methods}, {offsets} }}");
    }
    sb.Append($"static const df_impl df_impls_{cls.Name}[] = {{ {string.Join(", ", entries)} }};\n");
    return $"df_impls_{cls.Name}, {cls.Impls.Count}";
  }
}
