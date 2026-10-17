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
      if (classes.ContainsKey(name) || traits.ContainsKey(name) || enums.ContainsKey(name) || IsKnownType(name)) {
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
      if (baseType.Children.Count > 0 || baseType.Kind != "Type") {
        throw NotSupported("Generic parent traits", baseType);
      }
      trait.Super = traits.TryGetValue(baseType.Text ?? "", out var super)
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
          var (_, _, isStatic, isConst, isOverride) = ReadMods(member);
          if (isStatic || isConst || isOverride) {
            throw Error("a trait property cannot be static, const or override", member);
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
          trait.Fields.Add(new FieldInfo(member.Text!, ResolveType(typeNode), trait, member) {
            Visibility = Vis.Public,
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
    foreach (var member in body.Children.Where(member => member.Kind == "Fun")) {
      RejectAtEnd(member);
      var (_, _, isStatic, isConst, isOverride) = ReadMods(member);
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
      var f = RegisterFun(member, null, trait)!;
      f.Class = trait;
      f.IsAbstract = member.Children.Any(child => child.Kind == "NoBody");
      f.Visibility = Vis.Public;
      f.TSlot = trait.TraitMethods.Count;
      trait.TraitMethods.Add(f);
      for (var up = trait.Super; up != null; up = up.Super) {
        if (up.TraitMethods.Any(g => g.Name.EndsWith("." + member.Text) && ParamTypes(g).SequenceEqual(ParamTypes(f)))) {
          throw NotSupported($"A trait that redeclares '{member.Text}' of its parent trait {up.Name}", member);
        }
      }
    }
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
          throw Error($"class '{cls.Name}' must implement {trait.Name}.{method.Name[(method.Name.IndexOf('.') + 1)..]}", cls.Node);
        }
        methods.Add(implementation);
      }
      cls.Impls.Add(new ImplInfo(trait, methods));
    }
  }

  // ---------------------------------------------------------------- properties of traits

  // every field of this name visible from a class or a trait: its own, its parents', those of the traits it implements
  IEnumerable<FieldInfo> FieldCandidates(ClassInfo start, string name) {
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
      foreach (var trait in TraitClosure(c.Traits)) {
        foreach (var field in trait.Fields.Where(f => f.Name == name)) {
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

  // statements of the traits this class introduces: property initializers, then the trait constructor bodies
  List<Node> TraitInitStatements(ClassInfo cls, Node at) {
    var statements = new List<Node>();
    foreach (var trait in IntroducedTraits(cls)) {
      foreach (var field in trait.Fields.Where(field => field.Init != null)) {
        // this.name, or this.TraitName.name when two traits have a property of this name
        Node owner = Synthetic("Name", at, "this");
        if (FieldCandidates(cls, field.Name).Distinct().Count() > 1) {
          owner = Synthetic("Member", at, trait.Name).Add(owner);
        }
        var target = Synthetic("Member", at, field.Name).Add(owner);
        statements.Add(Synthetic("Assign", at, "=").Add(target).Add(field.Init!));
      }
      if (trait.CtorBlock != null) {
        statements.AddRange(trait.CtorBlock.Children);
      }
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
