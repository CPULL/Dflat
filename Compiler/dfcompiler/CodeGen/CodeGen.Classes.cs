using System.Text;
using dfparser;

namespace dfcompiler;

enum Vis {
  Private,
  Internal,
  Public
}

sealed class FieldInfo {
  public FieldInfo(string name, string type, ClassInfo owner, Node at) {
    Name = name;
    Type = type;
    Owner = owner;
    At = at;
  }

  public string Name { get; }
  public string Type { get; }
  public ClassInfo Owner { get; }
  public Node At { get; }
  public Vis Visibility { get; set; } = Vis.Private;
  public bool IsStatic { get; set; }
  public bool IsConst { get; set; }
  public bool FromParam { get; set; }
  public Node? Init { get; set; }
  public FunSig? StaticInit { get; set; }
  public string CName => IsStatic ? $"s_{Owner.Name}_{Name}" : Owner.IsTrait ? $"t_{Owner.Name}_{Name}" : $"m_{Name}";
}

sealed class ClassInfo {
  public ClassInfo(string name, Node node, int index) {
    Name = name;
    Node = node;
    Index = index;
  }

  public string Name { get; }
  public Node Node { get; }
  public int Index { get; }
  public ClassInfo? Parent { get; set; }
  public List<FieldInfo> Fields { get; } = new();            // own fields, instance and static, in declaration order
  public List<ParamSig> PrimaryParams { get; } = new();
  public List<FunSig> Vtable { get; } = new();                // virtual slots, the parent's first
  public FunSig? Ctor { get; set; }
  public FunSig? Init { get; set; }

  // traits: a trait is a ClassInfo that is never instantiated
  public bool IsException { get; set; }                       // derives (directly or not) from Exception
  public FunSig? Dtor { get; set; }                           // the atEnd after the class
  public Node? EqualsNode { get; set; }                       // equals(T other), compiled once for each T
  public bool IsTrait { get; init; }
  public ClassInfo? Super { get; set; }                       // the parent trait
  public List<ClassInfo> Traits { get; } = new();             // the traits a class lists in its header
  public List<ImplInfo> Impls { get; } = new();
  public List<FunSig> TraitMethods { get; } = new();          // the methods of a trait, in slot order
  public Node? CtorBlock { get; set; }                        // the body of the constructor of a trait
  public string Descriptor => $"df_cls_{Name}";
  public string StructName => $"c_{Name}";
}

// Classes (spec 11.1, 11.1.1, 11.1.2): objects, fields, primary constructors, methods, statics, single inheritance.
// An object is a pointer to a struct that starts with its class descriptor; fields follow, the parent's first.
// Methods are C functions taking the object first; overridable methods are called through the class vtable.
public sealed partial class CodeGen {
  readonly Dictionary<string, ClassInfo> classes = new();
  readonly List<ClassInfo> classOrder = new();               // parents before children
  readonly StringBuilder classGlobals = new();
  readonly StringBuilder classCode = new();
  readonly List<FieldInfo> staticOrder = new();              // statics in the order their initializers must run
  ClassInfo? currentClass;
  bool initializing;                                         // compiling a constructor or a static initializer: const fields can be set
  FunSig? currentFun;

  // an object type: a class or a trait
  bool IsClass(string type) => classes.ContainsKey(type) || traits.ContainsKey(type);

  bool IsSubclass(string type, string ancestor) {
    if (!classes.TryGetValue(type, out var cls)) {
      return false;
    }
    for (var c = cls; c != null; c = c.Parent) {
      if (c.Name == ancestor) {
        return true;
      }
    }
    return false;
  }

  // steps from a class up to an ancestor (0 for the same class)
  int ClassDistance(string type, string ancestor) {
    int steps = 0;
    if (!classes.TryGetValue(type, out var start)) {
      return 20;                                // a trait: farther than any parent class
    }
    for (var c = start; c != null; c = c.Parent, steps++) {
      if (c.Name == ancestor) {
        return steps;
      }
    }
    return 15;                                  // a trait the class implements
  }

  bool NullByDefault(string type) => type == "string" || IsTimeType(type) || IsArr(type) || IsClass(type);

  // ---------------------------------------------------------------- declaring and registering

  void DeclareClasses(Node program) {
    DeclareTraits(program);
    foreach (var node in program.Children.Where(child => child.Kind == "Class")) {
      string name = node.Text!;
      if (classes.ContainsKey(name) || traits.ContainsKey(name) || enums.ContainsKey(name) || IsKnownType(name) || name == "Exception"
          || IsSetName(name) || genericClasses.ContainsKey(name)) {
        throw Error($"the name '{name}' is already used by a type", node);
      }
      var info = new ClassInfo(name, node, classes.Count);
      classes[name] = info;
      typeDefs.Append($"typedef struct c_{name} c_{name};\n");
    }
    typeDefs.Append('\n');
  }

  static (Vis Visibility, bool Explicit, bool IsStatic, bool IsConst, bool IsOverride) ReadMods(Node member) {
    var mods = member.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
    Vis visibility = mods.Contains("public") ? Vis.Public : mods.Contains("internal") ? Vis.Internal : Vis.Private;
    bool explicitVisibility = mods.Contains("public") || mods.Contains("internal") || mods.Contains("private");
    return (visibility, explicitVisibility, mods.Contains("static"), mods.Contains("const"), mods.Contains("override"));
  }

  void RegisterClasses() {
    // parents
    foreach (var cls in classes.Values) {
      var bases = cls.Node.Children.FirstOrDefault(child => child.Kind == "Bases");
      if (bases == null) {
        continue;
      }
      bool sawTrait = false;
      foreach (var baseType in bases.Children) {
        string baseName = baseType.Text ?? "";
        if (baseName is "Root.Class" or "Class") {
          Warn("Root.Class is the implicit base of every class and is never written", baseType, 1);
          continue;
        }
        if (genericClasses.TryGetValue(baseName, out var genericBase) && genericBase.IsTrait) {
          // Container<string> or Sequence with (Item is u32): the copy of the generic trait
          var withNode = baseType.Children.FirstOrDefault(child => child.Kind == "With");
          var typeArgNodes = baseType.Children.Where(child => child.Kind != "With").ToList();
          List<string> traitArgs;
          if (genericBase.HasPlaceholders) {
            if (typeArgNodes.Count > 0) {
              throw Error($"'{baseName}' has placeholders, not type parameters: write {baseName} with (...)", baseType);
            }
            if (withNode == null) {
              throw Error($"'{baseName}' has placeholders: fix them, as in {baseName} with ({genericBase.Params[0].Name} is type)", baseType);
            }
            foreach (var bind in withNode.Children.Where(bind => !genericBase.Params.Any(p => p.Name == bind.Text))) {
              throw Error($"'{bind.Text}' is not a placeholder of {baseName}", bind);
            }
            traitArgs = new List<string>();
            foreach (var placeholder in genericBase.Params) {
              var bind = withNode.Children.FirstOrDefault(b => b.Text == placeholder.Name)
                ?? throw Error($"the placeholder '{placeholder.Name}' of {baseName} is not fixed: add ({placeholder.Name} is type) to 'with'", baseType);
              traitArgs.Add(ResolveType(bind.Children[0]));
            }
          } else {
            if (withNode != null) {
              throw Error($"'{baseName}' has no placeholders: 'with' does not apply", withNode);
            }
            if (typeArgNodes.Count == 0) {
              throw Error($"'{baseName}' is generic: write its type arguments, {baseName}<...>", baseType);
            }
            traitArgs = TypeArgsOf(typeArgNodes, genericBase.Params);
          }
          baseName = ClassInstanceName(genericBase, traitArgs, baseType);
          if (cls.Traits.Any(existing => classInstanceArgs.TryGetValue(existing.Name, out var other) && other.Template == genericBase)) {
            throw Error($"feature not supported yet (a class that implements the generic trait {genericBase.Name} more than once)", baseType);
          }
        } else if (baseType.Children.Count > 0) {
          throw baseType.Children.Any(child => child.Kind == "With")
            ? Error($"'{baseName}' has no placeholders: 'with' does not apply", baseType)
            : NotSupported("Generic parent types", baseType);
        }
        if (baseName is "Exception" or "Root.Exception") {
          if (cls.Parent != null || sawTrait || cls.IsException) {
            throw Error("Exception is the parent class: it goes first and alone", baseType);
          }
          cls.IsException = true;
          continue;
        }
        if (classes.TryGetValue(baseName, out var parent)) {
          if (sawTrait) {
            throw Error($"the parent class '{baseName}' goes before all the traits", baseType);
          }
          if (cls.Parent != null) {
            throw Error("a class has one parent class", baseType);
          }
          cls.Parent = parent;
        } else if (traits.TryGetValue(baseName, out var trait)) {
          sawTrait = true;
          if (cls.Traits.Contains(trait)) {
            throw Error($"the trait '{baseName}' is listed twice", baseType);
          }
          cls.Traits.Add(trait);
        } else {
          throw Error($"unknown parent class or trait '{baseName}'", baseType);
        }
      }
    }
    RegisterTraits();
    foreach (var cls in classes.Values) {
      for (var c = cls.Parent; c != null; c = c.Parent) {
        if (c == cls) {
          throw Error($"class '{cls.Name}' inherits from itself", cls.Node);
        }
      }
    }
    var visited = new HashSet<ClassInfo>();
    void Order(ClassInfo cls) {
      if (visited.Add(cls)) {
        if (cls.Parent != null) {
          Order(cls.Parent);
        }
        classOrder.Add(cls);
      }
    }
    foreach (var cls in classes.Values) {
      Order(cls);
    }
    RegisterExceptionClasses();

    foreach (var cls in classOrder) {
      RegisterFields(cls);
    }
    foreach (var cls in classOrder) {
      RegisterMethods(cls);
    }
    foreach (var cls in classOrder) {
      WarnEqualityTwice(cls);
      BuildImpls(cls);
      BuildConstructor(cls);
    }
    foreach (var cls in classOrder) {
      EmitStruct(cls);
    }
    PlanStatics();
  }

  void RejectAtEnd(Node node) {
    if (node.Children.Any(child => child.Kind == "AtEnd" && child.Text == "trailing")) {
      throw NotSupported("atEnd after a class or constructor (destructors)", node);
    }
  }

  void RegisterFields(ClassInfo cls) {
    var node = cls.Node;
    var primary = node.Children.FirstOrDefault(child => child.Kind == "PrimaryCtor");
    if (primary != null) {
      foreach (var p in primary.Children) {
        if (ParamFeatures.TryGetValue(p.Kind, out var feature)) {
          throw NotSupported(feature, p);
        }
        if (p.Kind == "VariadicParam") {
          throw NotSupported("Variadic constructor parameters", p);
        }
        var def = p.Children.FirstOrDefault(child => child.Kind == "Default");
        string type = ResolveType(p.Children[0]);
        if (cls.PrimaryParams.Any(existing => existing.Name == p.Text)) {
          throw Error($"duplicate parameter '{p.Text}'", p);
        }
        if (p.Kind is "RefParam" or "WritableRefParam") {
          throw NotSupported("Reference parameters on constructors", p);
        }
        if (IsClass(type) && p.Kind != "GiftParam") {
          throw Error($"the object '{p.Text}' goes into a field of {cls.Name}, which owns it: write ->{type} {p.Text}", p);
        }
        cls.PrimaryParams.Add(new ParamSig(p.Text!, type, def?.Children[0]) { IsGift = p.Kind == "GiftParam" && IsClass(type) });
        CheckFieldName(cls, p.Text!, p);
        cls.Fields.Add(new FieldInfo(p.Text!, type, cls, p) { FromParam = true });
      }
    }

    var body = node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children) {
      switch (member.Kind) {
        case "Decl": {
          var (visibility, _, isStatic, isConst, isOverride) = ReadMods(member);
          if (isOverride) {
            throw Error("'override' applies to functions", member);
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
            throw Error("'var' cannot be used for a field: write its type", member);
          }
          string type = ResolveType(typeNode);
          var existing = cls.Fields.FirstOrDefault(field => field.Name == member.Text && field.FromParam);
          if (existing != null && !isStatic) {
            // redeclaring a primary parameter sets its visibility (spec 11.1.1)
            if (existing.Type != type) {
              throw Error($"'{member.Text}' is declared with the type {existing.Type} in the header", member);
            }
            existing.Visibility = visibility;
            if (init != null) {
              Warn($"the header parameter '{member.Text}' has an initializer in the body; the body value wins", member, 3);
              existing.Init = init.Children[0];
            }
            break;
          }
          CheckFieldName(cls, member.Text!, member);
          var field = new FieldInfo(member.Text!, type, cls, member) {
            Visibility = visibility,
            IsStatic = isStatic,
            IsConst = isConst,
            Init = init?.Children[0]
          };
          if (isConst && !isStatic) {
            throw Error("'const' on a field needs 'static'", member);
          }
          cls.Fields.Add(field);
          break;
        }
        case "Fun":
        case "FunVar":
          break;                                // registered after all fields
        case "Constructor":
          throw NotSupported("Secondary constructors", member);
        case "Class":
        case "Trait":
        case "Enum":
          throw NotSupported("Nested types", member);
        case "AtEnd":
          throw NotSupported("atEnd in a class body", member);
        default:
          throw Error($"'{member.Kind}' is not allowed in a class body", member);
      }
    }
  }

  void CheckFieldName(ClassInfo cls, string name, Node at) {
    if (name == "this" || name == "parent") {
      throw Error($"'{name}' cannot be a field name", at);
    }
    if (cls.IsException && name is "Id" or "Message" or "Function" or "Line" or "message") {
      throw Error($"'{name}' is reserved in an exception class", at);
    }
    for (var c = cls; c != null; c = c.Parent) {
      if (c.Fields.Any(field => field.Name == name)) {
        throw Error($"the field '{name}' is already declared{(c == cls ? "" : " in " + c.Name)}", at);
      }
    }
    if (FieldCandidates(cls, name).Any()) {
      throw Error($"'{name}' is already a property of a trait of {cls.Name}", at);
    }
  }

  static List<string> ParamTypes(FunSig f) => f.Params.Select(param => param.Type).ToList();

  void RegisterMethods(ClassInfo cls) {
    if (cls.Parent != null) {
      cls.Vtable.AddRange(cls.Parent.Vtable);
    }
    var body = cls.Node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children) {
      if (member.Kind == "FunVar") {
        throw NotSupported("Lambdas", member);
      }
      if (member.Kind != "Fun") {
        continue;
      }
      var (visibility, explicitVisibility, isStatic, isConst, isOverride) = ReadMods(member);
      if (isConst) {
        throw Error("'const' applies to static fields", member);
      }
      if (IsGenericEquals(member)) {
        RegisterEquals(cls, member);
        continue;
      }
      if (member.Children.Any(child => child.Kind == "NoBody")) {
        throw Error("a function without a body is virtual and can be used only in traits", member);
      }
      if (cls.Fields.Any(field => field.Name == member.Text)) {
        throw Error($"'{member.Text}' is already a field of {cls.Name}", member);
      }
      if (isOverride && explicitVisibility) {
        throw Error("an overriding function takes the visibility of the original: write none on it", member);
      }
      var f = RegisterFun(member, null, cls)!;
      f.Class = cls;
      f.IsStatic = isStatic;
      f.IsOverride = isOverride;
      f.Visibility = visibility;
      if (member.Text == "==") {
        CheckOperatorEquals(f, member);
      }

      // the same signature higher up: an override of an instance function, an error for a static one
      FunSig? above = null;
      for (var c = cls.Parent; c != null && above == null; c = c.Parent) {
        if (funs.TryGetValue(c.Name + "." + member.Text, out var candidates)) {
          above = candidates.FirstOrDefault(g => ParamTypes(g).SequenceEqual(ParamTypes(f)));
        }
      }
      if (isStatic) {
        if (isOverride) {
          throw Error("a static function cannot be overridden", member);
        }
        if (above != null) {
          throw Error($"the static function '{member.Text}' is already declared in {above.Class!.Name}", member);
        }
        continue;
      }
      if (above == null) {
        // a function that answers a method of one of the traits (it is written with override too)
        FunSig? ofTrait = null;
        foreach (var trait in AllTraits(cls)) {
          if (funs.TryGetValue(trait.Name + "." + member.Text, out var methods)) {
            ofTrait ??= methods.FirstOrDefault(g => ParamTypes(g).SequenceEqual(ParamTypes(f)));
          }
        }
        if (ofTrait != null) {
          if (!isOverride) {
            throw Error($"'{member.Text}' already exists in the trait {ofTrait.Class!.Name}: write 'override'", member);
          }
          if (ofTrait.Ret != f.Ret) {
            throw Error($"it returns {ofTrait.Ret}, like the trait function it answers", member);
          }
          f.Visibility = Vis.Public;
        } else if (isOverride) {
          throw Error($"'{member.Text}' overrides nothing: no function with this signature in a parent class or trait", member);
        }
        f.Slot = cls.Vtable.Count;
        cls.Vtable.Add(f);
        continue;
      }
      if (above.IsStatic) {
        throw Error($"'{member.Text}' hides the static function of {above.Class!.Name}", member);
      }
      if (!isOverride) {
        throw Error($"'{member.Text}' already exists in {above.Class!.Name}: write 'override'", member);
      }
      if (above.Ret != f.Ret) {
        throw Error($"an override returns {above.Ret}, like the function it overrides", member);
      }
      f.Slot = above.Slot;
      f.Root = above.Root;
      f.Root.GroupSize++;
      f.Visibility = above.Visibility;
      cls.Vtable[f.Slot] = f;
    }
  }

  // The primary constructor: an init function (fills the fields of one class on an existing object) and the
  // constructor that allocates the object and calls it. Construction order: parent, then the class body.
  void BuildConstructor(ClassInfo cls) {
    if (cls.Parent != null && cls.Parent.PrimaryParams.Count > 0) {
      throw NotSupported($"Passing arguments to the constructor of the parent class {cls.Parent.Name}", cls.Node);
    }
    var at = cls.Node;
    var block = Synthetic("Block", at);
    var ctorFields = cls.Fields.Where(field => !field.IsStatic).ToList();
    foreach (var statement in TraitInitStatements(cls, at)) {
      block.Add(statement);
    }
    foreach (var field in ctorFields.Where(field => field.FromParam)) {
      Node source = Synthetic("Name", at, field.Name);
      if (cls.PrimaryParams.First(param => param.Name == field.Name).IsGift) {
        source = Synthetic("Gift", at).Add(source);   // the object moves from the parameter into the field
      }
      block.Add(Assignment(field.Name, source, at, false));
    }
    foreach (var field in ctorFields.Where(field => field.Init != null)) {
      block.Add(Assignment(field.Name, field.Init!, at, false));
    }
    var initNode = Synthetic("Fun", at, "__init").Add(Synthetic("Params", at)).Add(block);

    var ctorParams = cls.IsException ? ExceptionConstructorParams(cls) : cls.PrimaryParams.ToList();
    var initSig = new FunSig(cls.Name + ".__init", "void", ctorParams, initNode, null) {
      Class = cls,
      IsInit = true,
      Visibility = Vis.Public
    };
    funs[initSig.Name] = new List<FunSig> { initSig };
    var ctorSig = new FunSig(cls.Name, cls.Name, ctorParams.ToList(), initNode, null) {
      Class = cls,
      IsCtor = true,
      Visibility = Vis.Public
    };
    funs[cls.Name] = new List<FunSig> { ctorSig };
    cls.Init = initSig;
    cls.Ctor = ctorSig;

    // the atEnd after the class is its destructor: a function of the object, run when it ends
    var trailing = cls.Node.Children.FirstOrDefault(child => child.Kind == "AtEnd" && child.Text == "trailing");
    if (trailing != null) {
      var dtorNode = Synthetic("Fun", at, "__dtor").Add(Synthetic("Params", at)).Add(trailing.Children[0]);
      var dtor = new FunSig(cls.Name + ".__dtor", "void", new List<ParamSig>(), dtorNode, null) {
        Class = cls,
        IsDtor = true,
        Visibility = Vis.Private
      };
      funs[dtor.Name] = new List<FunSig> { dtor };
      cls.Dtor = dtor;
    }

    // statics: one initializer function per static with a value
    foreach (var field in cls.Fields.Where(field => field.IsStatic && field.Init != null)) {
      var staticBlock = Synthetic("Block", at).Add(Assignment(field.Name, field.Init!, at, true));
      var staticNode = Synthetic("Fun", at, "__static").Add(Synthetic("Params", at)).Add(staticBlock);
      var staticSig = new FunSig($"{cls.Name}.__static_{field.Name}", "void", new List<ParamSig>(), staticNode, null) {
        Class = cls,
        IsStatic = true,
        IsStaticInit = true,
        Visibility = Vis.Public
      };
      funs[staticSig.Name] = new List<FunSig> { staticSig };
      field.StaticInit = staticSig;
    }
  }

  // this.name = value for an instance field, name = value for a static one
  static Node Assignment(string field, Node value, Node at, bool isStatic) {
    Node target = isStatic
      ? Synthetic("Name", at, field)
      : Synthetic("Member", at, field).Add(Synthetic("Name", at, "this"));
    return Synthetic("Assign", at, "=").Add(target).Add(value);
  }

  // struct, descriptor and storage of statics
  void EmitStruct(ClassInfo cls) {
    var fields = new StringBuilder();
    foreach (var field in AllInstanceFields(cls)) {
      fields.Append($"  {CType(field.Type)} {field.CName};\n");
    }
    typeDefs.Append($"struct {cls.StructName} {{\n  const df_class* cls;\n{(cls.IsException ? ExceptionFields : "")}{fields}}};\n\n");
    foreach (var field in cls.Fields.Where(field => field.IsStatic)) {
      classGlobals.Append($"static {CType(field.Type)} {field.CName};\n");
    }
  }

  List<FieldInfo> AllInstanceFields(ClassInfo cls) {
    var all = cls.Parent != null ? AllInstanceFields(cls.Parent) : new List<FieldInfo>();
    foreach (var trait in IntroducedTraits(cls)) {
      all.AddRange(trait.Fields.Where(field => !field.IsStatic));
    }
    all.AddRange(cls.Fields.Where(field => !field.IsStatic));
    return all;
  }

  // at: report a name found in two traits (write TraitName.name)
  FieldInfo? FindField(ClassInfo cls, string name, Node? at = null) {
    var found = FieldCandidates(cls, name).Distinct().ToList();
    if (found.Count > 1 && at != null) {
      throw Error($"'{name}' is in {string.Join(" and ", found.Select(f => f.Owner.Name))}: write TraitName.{name}", at);
    }
    return found.FirstOrDefault();
  }

  // ---------------------------------------------------------------- statics

  // the order the static initializers run: a static that reads another one runs after it; a cycle is an error
  void PlanStatics() {
    var state = new Dictionary<FieldInfo, int>();   // 1 = running, 2 = done
    void Visit(FieldInfo field, ClassInfo context) {
      if (state.TryGetValue(field, out int s)) {
        if (s == 1) {
          throw Error($"the static initializers of '{field.Owner.Name}.{field.Name}' depend on each other in a cycle", field.At);
        }
        return;
      }
      state[field] = 1;
      if (field.Init != null) {
        foreach (var dependency in StaticDependencies(field.Init, field.Owner)) {
          Visit(dependency, dependency.Owner);
        }
      }
      state[field] = 2;
      staticOrder.Add(field);
    }
    foreach (var cls in classOrder) {
      foreach (var field in cls.Fields.Where(field => field.IsStatic)) {
        Visit(field, cls);
      }
    }
  }

  IEnumerable<FieldInfo> StaticDependencies(Node node, ClassInfo context) {
    if (node.Kind == "Member" && node.Children.Count == 1 && node.Children[0].Kind == "Name"
        && classes.TryGetValue(node.Children[0].Text!, out var named)
        && FindField(named, node.Text!) is { IsStatic: true } other) {
      yield return other;
    } else if (node.Kind == "Name" && FindField(context, node.Text!) is { IsStatic: true } own) {
      yield return own;
    }
    foreach (var child in node.Children) {
      foreach (var dependency in StaticDependencies(child, context)) {
        yield return dependency;
      }
    }
  }

  // C statements run at program start: zero values, then the initializers in dependency order
  string StaticInitCode() {
    var sb = new StringBuilder();
    foreach (var field in staticOrder) {
      sb.Append($"  {field.CName} = {ZeroValue(field.Type)};\n");
    }
    foreach (var field in staticOrder.Where(field => field.StaticInit != null)) {
      if (PairReturn(field.StaticInit!)) {
        sb.Append($"  {{\n    df_exception* error = {field.StaticInit!.CName}();\n    if (error) {{\n      df_panic_unhandled(error);\n    }}\n  }}\n");
      } else {
        sb.Append($"  {field.StaticInit!.CName}();\n");
      }
    }
    return sb.ToString();
  }

  // ---------------------------------------------------------------- access rules

  void CheckAccess(Vis visibility, ClassInfo owner, string what, Node at) {
    if (visibility != Vis.Private) {
      return;                                   // internal is as open as public until namespaces exist
    }
    // a subclass sees everything of its parents (their source is available)
    if (currentClass != null && (currentClass == owner || IsSubclass(currentClass.Name, owner.Name))) {
      return;
    }
    throw Error($"'{what}' is private in {owner.Name}", at);
  }

  // ---------------------------------------------------------------- names, fields, members

  // a field of the current class used by its bare name
  Expr? BareField(string name, Scope scope, Node at) {
    if (currentClass == null || FindField(currentClass, name, at) is not FieldInfo field) {
      return null;
    }
    if (field.IsStatic) {
      return new Expr(field.CName, field.Type);
    }
    if (scope.Find("this") == null) {
      throw Error($"'{name}' is an instance field; a static function has no object", at);
    }
    if (currentClass.IsTrait) {
      return new Expr(TraitFieldCode("self", field), field.Type);
    }
    return new Expr($"self->{field.CName}", field.Type);
  }

  // the object, or a null check (NullPointerException) when it comes from anywhere but this
  string NullChecked(string code, string type) {
    if (code == "self") {
      return code;
    }
    string temp = NewTemp();
    return $"({{ {CType(type)} {temp} = {code}; if ({temp} == NULL || DF_IS_DEAD({temp})) {{ df_raise(&df_desc_NullPointerException, {fn!.NameConst}); {RaiseJump} }} {temp}; }})";
  }

  Expr ObjectField(Expr obj, string name, Node at) {
    var cls = ObjInfo(obj.Type);
    if (cls.IsException && ExceptionObjectMember(obj, name, at) is Expr exceptionMember) {
      return exceptionMember;
    }
    var field = FindField(cls, name, at) ?? throw Error($"{cls.Name} has no field '{name}'", at);
    if (field.IsStatic) {
      throw Error($"'{name}' is static: write {field.Owner.Name}.{name}", at);
    }
    CheckAccess(field.Visibility, field.Owner, name, at);
    if (cls.IsTrait) {
      return new Expr(TraitFieldCode(NullChecked(obj.Code, obj.Type), field), field.Type);
    }
    return new Expr($"({NullChecked(obj.Code, obj.Type)})->{field.CName}", field.Type);
  }

  Expr StaticField(ClassInfo cls, string name, Node at) {
    var field = FindField(cls, name) ?? throw Error($"{cls.Name} has no member '{name}'", at);
    if (!field.IsStatic) {
      throw Error($"'{name}' is an instance field: it needs an object", at);
    }
    CheckAccess(field.Visibility, field.Owner, name, at);
    return new Expr(field.CName, field.Type);
  }

  // writable fields
  LValueInfo? ClassLValue(Node target, Scope scope) {
    string? path = Path(target);
    string[]? parts = path?.Split('.');
    if (parts is { Length: 2 } && scope.Find(parts[0]) == null && classes.TryGetValue(parts[0], out var named)) {
      var field = FindField(named, parts[1]) ?? throw Error($"{named.Name} has no member '{parts[1]}'", target);
      if (!field.IsStatic) {
        throw Error($"'{parts[1]}' is an instance field: it needs an object", target);
      }
      CheckAccess(field.Visibility, field.Owner, parts[1], target);
      return new LValueInfo(field.CName, field.Type, field.IsConst && !initializing);   // a static: not tracked
    }
    var owner = ExprOf(target.Children[0], scope);
    if (!IsClass(owner.Type)) {
      return null;
    }
    var instance = ObjectField(owner, target.Text!, target);
    var info = FindField(ObjInfo(owner.Type), target.Text!)!;
    return new LValueInfo(instance.Code, instance.Type, info.IsConst && !initializing, null, true);
  }

  // ---------------------------------------------------------------- calls

  // the functions named 'name' visible from a class: its own, then those of its parents that it does not replace
  List<FunSig> LookupMethods(ClassInfo cls, string name) {
    var result = new List<FunSig>();
    void AddFrom(ClassInfo owner) {
      if (!funs.TryGetValue(owner.Name + "." + name, out var list)) {
        return;
      }
      foreach (var f in list) {
        // the same function in two traits stays twice: it is ambiguous unless a class function replaces both
        if (!result.Any(existing => ParamTypes(existing).SequenceEqual(ParamTypes(f))
            && !(existing.Class!.IsTrait && f.Class!.IsTrait && existing.Class != f.Class && !cls.IsTrait))) {
          result.Add(f);
        }
      }
    }
    if (cls.IsTrait) {
      for (var t = cls; t != null; t = t.Super) {
        AddFrom(t);
      }
      return result;
    }
    for (var c = cls; c != null; c = c.Parent) {
      AddFrom(c);
    }
    // the functions of the traits it implements: a class function with the same signature replaces them
    foreach (var trait in AllTraits(cls)) {
      AddFrom(trait);
    }
    return result;
  }

  List<string> ArgCodes(FunSig f, List<Node> args, Scope scope, Node at) {
    var (slots, problem) = MapArgs(f, args);
    if (slots == null) {
      throw Error(problem!, at);
    }
    var codes = new List<string>();
    for (int index = 0; index < f.Params.Count; index++) {
      var param = f.Params[index];
      Node arg = slots[index] ?? param.Default!;
      if (param.IsVariadic && arg.Kind == "List" && arg.Children.Count == 1
          && ExprOf(arg.Children[0], scope).Type == param.Type) {
        arg = arg.Children[0];
      }
      codes.Add(ArgCode(param, arg, scope));
    }
    return codes;
  }

  // &T x gets the address of a variable, or of a temporary holding the value; (&)T x gets the address of a variable, a field or an
  // array item, written (&)x in the call
  string ReferenceArg(ParamSig param, Node arg, Scope scope) {
    if (param.Ref == 2) {
      if (arg.Kind != "WritableAddressOf") {
        throw Error($"'{param.Name}' is written by the function: pass it as (&)variable", arg);
      }
      return WritableAddress(arg.Children[0], scope, param.Type, arg);
    }
    if (arg.Kind == "WritableAddressOf") {
      throw Error($"'{param.Name}' is a read-only reference: remove the (&)", arg);
    }
    var value = ExprOf(arg, scope, param.Type);
    string code = Convert(value, param.Type, arg);
    if (arg.Kind == "Name" && value.Type == param.Type && scope.Find(arg.Text!) is VarInfo variable) {
      ReadCheck(variable, arg.Text!, arg);
      return "&" + variable.CName;
    }
    return $"((const {CType(param.Type)}[1]){{ {code} }})";
  }

  // the C address of what (&)x names; the type must be the one of the parameter
  string WritableAddress(Node target, Scope scope, string type, Node at) {
    if (target.Kind is not ("Name" or "Member" or "Index")) {
      throw Error("(&) needs a variable, a field or an array item", at);
    }
    if (target.Kind == "Index") {
      var obj = ExprOf(target.Children[0], scope);
      if (!IsArr(obj.Type) || target.Children[0].Kind == "GenericRef") {
        throw Error("(&) needs a variable, a field or an array item", at);
      }
      if (ArrElem(obj.Type) != type) {
        throw Error($"the parameter is a {type}, not a {ArrElem(obj.Type)}", at);
      }
      var index = ExprOf(target.Children[1], scope, "u64");
      string array = NewTemp();
      string position = NewTemp();
      return $"({{ {CType(obj.Type)} {array} = {obj.Code}; df_uns {position} = {IndexValue(index, target)}; "
        + $"if (!df_idx_ok({array}.len, {position}, {fn!.NameConst})) {RaiseJump} &{array}.p[{position}]; }})";
    }
    var lvalue = LValue(target, scope);
    if (lvalue.IsConst) {
      throw Error($"'{target.Text}' is const and cannot be passed as (&)", at);
    }
    if (lvalue.Type != type) {
      throw Error($"the parameter is a {type}, but '{target.Text}' is a {lvalue.Type}", at);
    }
    MarkAssigned(lvalue.Variable);
    return "&" + lvalue.Code;
  }

  // one argument: an object given to a parameter written ->T moves (the call writes ->x); the others only borrow
  string ArgCode(ParamSig param, Node arg, Scope scope) {
    if (IsByPointer(param)) {
      return ReferenceArg(param, arg, scope);
    }
    if (param.Ref == 2 && IsClass(param.Type)) {
      // (&)Item o: the object is already a pointer; the call still writes (&)o
      if (arg.Kind != "WritableAddressOf") {
        throw Error($"'{param.Name}' is a writable borrow: pass it as (&)object", arg);
      }
      arg = arg.Children[0];
    } else if (arg.Kind == "WritableAddressOf") {
      throw Error($"'{param.Name}' is not a (&) parameter: remove the (&)", arg);
    }
    var value = ExprOf(arg, scope, param.Type);
    if (IsClass(param.Type)) {
      if (param.IsGift && !value.Fresh && value.Type != "null") {
        throw Error($"'{param.Name}' takes ownership of the object: write ->x", arg);
      }
      if (!param.IsGift && arg.Kind == "Gift") {
        throw Error($"'{param.Name}' only borrows the object: remove the ->", arg);
      }
    }
    return Convert(value, param.Type, arg);
  }

  // cls.name(args): a static function (receiver null) or a function of an object
  // direct: no virtual dispatch (parent.name())
  Expr MethodCall(ClassInfo cls, string name, string? receiver, bool direct, List<Node> args, Scope scope, Node at) {
    var all = LookupMethods(cls, name);
    if (all.Count == 0) {
      throw Error($"{DisplayName(cls.Name)} has no function '{name}'", at);
    }
    var candidates = all.Where(f => f.IsStatic == (receiver == null)).ToList();
    if (candidates.Count == 0) {
      throw Error(receiver == null
        ? $"'{name}' is an instance function: it needs an object"
        : $"'{name}' is static: write {cls.Name}.{name}(...)", at);
    }
    var chosen = ResolveOverload(candidates, args, scope, at);
    CheckAccess(chosen.Visibility, chosen.Class!, name, at);
    var codes = ArgCodes(chosen, args, scope, at);
    string call;
    if (receiver == null) {
      call = $"{chosen.CName}({string.Join(", ", codes)})";
    } else {
      string self = NullChecked(receiver, cls.Name);
      string declarer = CType(chosen.Class!.Name);
      bool virtualCall = !direct && chosen.Root.GroupSize > 0;
      if (chosen.Class!.IsTrait) {
        // a function of a trait: the class of the object says which function answers
        string temp = NewTemp();
        var paramTypes = new List<string> { "void*" };
        paramTypes.AddRange(chosen.Params.Select(param => ParamCType(param)));
        codes.Insert(0, temp);
        call = $"({{ void* {temp} = (void*)({self}); (({RetCType(chosen)}(*)({string.Join(", ", paramTypes)}))"
          + $"(df_impl_of({temp}, &df_trait_{chosen.Class.Name})->methods[{chosen.TSlot}]))({string.Join(", ", codes)}); }})";
        // the trait function and its implementations are one group (same name and parameters): they fail together
        return new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned);
      }
      if (!virtualCall) {
        codes.Insert(0, $"(({declarer}){self})");
        call = $"{chosen.CName}({string.Join(", ", codes)})";
      } else {
        string temp = NewTemp();
        var paramTypes = new List<string> { CType(chosen.Class!.Name) };
        paramTypes.AddRange(chosen.Params.Select(param => ParamCType(param)));
        codes.Insert(0, $"(({declarer}){temp})");
        call = $"({{ {CType(cls.Name)} {temp} = {self}; (({RetCType(chosen)}(*)({string.Join(", ", paramTypes)}))({temp}->cls->vtable[{chosen.Slot}]))({string.Join(", ", codes)}); }})";
      }
    }
    return new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned);
  }

  // x.name(args) on an object, or name(args) inside a method
  Expr? ObjectCall(Expr receiver, string name, List<Node> args, Scope scope, Node at) {
    if (!IsClass(receiver.Type)) {
      return null;
    }
    return MethodCall(ObjInfo(receiver.Type), name, receiver.Code, false, args, scope, at);
  }

  // name(args) written inside a class: a function of this class (or a parent), before free functions
  Expr? BareMethodCall(string name, List<Node> args, Scope scope, Node at) {
    if (currentClass == null) {
      return null;
    }
    var found = LookupMethods(currentClass, name);
    if (found.Count == 0) {
      return null;
    }
    bool hasThis = scope.Find("this") != null;
    bool staticOnly = !hasThis || found.All(f => f.IsStatic);
    if (!staticOnly && found.Any(f => f.IsStatic)) {
      // mixed: pick among all, then use the right form
      var chosen = ResolveOverload(found, args, scope, at);
      return chosen.IsStatic
        ? MethodCall(currentClass, name, null, false, args, scope, at)
        : MethodCall(currentClass, name, "self", false, args, scope, at);
    }
    return staticOnly
      ? MethodCall(currentClass, name, null, false, args, scope, at)
      : MethodCall(currentClass, name, "self", false, args, scope, at);
  }

  // Foo.create(args)
  Expr? StaticCall(string path, List<Node> args, Scope scope, Node at) {
    string[] parts = path.Split('.');
    if (parts.Length != 2 || !classes.TryGetValue(parts[0], out var cls)) {
      return null;
    }
    return MethodCall(cls, parts[1], null, false, args, scope, at);
  }

  // parent.name(args): the parent's version, not the override
  Expr ParentCall(string name, List<Node> args, Scope scope, Node at) {
    if (currentClass?.Parent == null || scope.Find("this") == null) {
      throw Error("'parent' needs an object of a class with a parent class", at);
    }
    return MethodCall(currentClass.Parent, name, "self", true, args, scope, at);
  }

  // ---------------------------------------------------------------- type checks: x is Circle c

  Expr IsExpr(Node n, Scope scope) {
    if (n.Text != null) {
      throw Error("'is Type name' can only be used as the condition of an 'if'", n);
    }
    // a value that is not an object (the other of equals when T is a number): the answer is known now
    var peek = ExprOf(n.Children[0], scope);
    if (!IsClass(peek.Type) && peek.Type != "Exception") {
      return new Expr(peek.Type == ResolveType(n.Children[1]) ? "true" : "false", "bool");
    }
    var (subject, target) = IsParts(n, scope);
    return new Expr(IsTest(subject.Code, target, subject.Type, n), "bool");
  }

  string IsTest(string subject, ClassInfo target, string subjectType, Node at) {
    if (subjectType == "Exception") {
      // the exception held by a catch: its descriptor chain, or the silent flag
      if (target.IsTrait) {
        return target.Name == "SilentException" ? $"(({subject})->descriptor->silent)" : throw Error("a trait other than SilentException cannot be tested on an Exception", at);
      }
      return target.IsException ? $"df_exc_is({subject}, {customExceptionIds[target.Name]})" : throw Error($"{target.Name} is not an exception class", at);
    }
    return target.IsTrait
      ? $"df_has_trait({subject}, &df_trait_{target.Name})"
      : $"df_is({subject}, &{target.Descriptor})";
  }

  (Expr Subject, ClassInfo Target) IsParts(Node n, Scope scope) {
    var subject = ExprOf(n.Children[0], scope);
    if (!IsClass(subject.Type) && subject.Type != "Exception") {
      throw Error($"'is' needs an object, not {subject.Type}", n);
    }
    string targetName = n.Children[1].Kind is "Type" or "ResolvedType" ? n.Children[1].Text! : "";
    if (!classes.TryGetValue(targetName, out var target) && !traits.TryGetValue(targetName, out target)) {
      throw Error($"'is' needs a class or trait name, not '{targetName}'", n);
    }
    if (currentGenericT == null && subject.Type != "Exception" && IsSubtype(subject.Type, target.Name)) {
      Warn($"{subject.Type} is always a {target.Name}: the check is upward", n, 2);
    }
    return (subject, target);
  }

  // if x is Circle c { }: c is the object as a Circle inside the block
  (string Condition, Scope BlockScope) IsBinding(Node n, Scope scope) {
    var (subject, target) = IsParts(n, scope);
    CheckNotShadowing(n.Text!, scope, n);
    tempCounter++;
    string cname = $"v_{n.Text}_{tempCounter}";
    fn!.Decls.Append($"  {CType(target.Name)} {cname} = NULL;\n");
    string temp = NewTemp();
    string condition = $"({{ {CType(subject.Type)} {temp} = {subject.Code}; bool ok_{temp} = {IsTest(temp, target, subject.Type, n)}; "
      + $"if (ok_{temp}) {{ {cname} = ({CType(target.Name)}){temp}; }} ok_{temp}; }})";
    var inner = new Scope(scope);
    inner.Vars[n.Text!] = new VarInfo(target.Name, cname, false);
    return (condition, inner);
  }

  // ---------------------------------------------------------------- tables, printing

  // descriptors, virtual tables and the printing function of every class (after the prototypes)
  void EmitClassTables() {
    EmitTraitTables();
    EmitExceptionDescriptors();
    EmitEqualsTables();
    foreach (var cls in classOrder) {
      var all = AllInstanceFields(cls);
      var sb = classCode;
      sb.Append($"static df_string df_str_{cls.Name}(const {cls.StructName}* o) {{\n");
      sb.Append("  if (o == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      sb.Append($"  df_string r = {CString(CodePoints(DisplayName(cls.Name) + (all.Count == 0 ? " {}" : " { ")))};\n");
      for (int index = 0; index < all.Count && index < 3; index++) {
        var field = all[index];
        string label = (index > 0 ? ", " : "") + field.Name + ": ";
        string value;
        if (IsClass(field.Type)) {
          value = $"(o->{field.CName} == NULL ? df_str_lit(\"null\", 4) : {CString(CodePoints(DisplayName(field.Type) + "{}"))})";
        } else {
          value = StrOf(new Expr($"o->{field.CName}", field.Type), cls.Node);
        }
        sb.Append($"  r = df_str_concat(r, {CString(CodePoints(label))});\n");
        sb.Append($"  r = df_str_concat(r, {value});\n");
      }
      if (all.Count > 3) {
        sb.Append($"  r = df_str_concat(r, {CString(CodePoints(", ..."))});\n");
      }
      if (all.Count > 0) {
        sb.Append($"  r = df_str_concat(r, {CString(CodePoints(" }"))});\n");
      }
      sb.Append("  return r;\n}\n");
      sb.Append($"static df_string df_strp_{cls.Name}(const void* o) {{\n  return df_str_{cls.Name}((const {cls.StructName}*)o);\n}}\n\n");

      string slots = cls.Vtable.Count == 0 ? "NULL" : $"df_vt_{cls.Name}";
      if (cls.Vtable.Count > 0) {
        sb.Append($"static const void* const df_vt_{cls.Name}[] = {{ {string.Join(", ", cls.Vtable.Select(f => $"(const void*){f.CName}"))} }};\n");
      }
      string parent = cls.Parent == null ? "NULL" : "&" + cls.Parent.Descriptor;
      string impls = EmitImpls(cls);
      EmitDropGlue(cls);
      sb.Append($"static const df_class {cls.Descriptor} = {{ {256 + cls.Index}, \"{DisplayName(cls.Name)}\", {parent}, {slots}, df_strp_{cls.Name}, {impls}, df_dropall_{cls.Name} }};\n\n");
    }
  }

  // the C function that allocates an object and runs the init functions (parents first)
  string EmitConstructor(FunSig ctor) {
    var cls = ctor.Class!;
    var args = ctor.Params.Select(param => $"v_{param.Name}");
    var callArgs = string.Join(", ", new[] { "self" }.Concat(args));
    var sb = new StringBuilder();
    sb.Append(Signature(ctor)).Append(" {\n");
    sb.Append($"  {CType(cls.Name)} self = ({CType(cls.Name)})df_calloc(1, sizeof(struct {cls.StructName}));\n");
    sb.Append($"  self->cls = &{cls.Descriptor};\n");
    if (cls.IsException) {
      sb.Append($"  self->descriptor = &df_desc_{cls.Name};\n  self->id = df_desc_{cls.Name}.id;\n");
    }
    // a constructor returns the object with its exception when the init functions can let one out
    string initCall = $"{cls.Init!.CName}({callArgs})";
    if (PairReturn(cls.Init)) {
      sb.Append($"  df_exception* error = {initCall};\n");
      sb.Append(PairReturn(ctor)
        ? $"  if (error) {{\n    df_drop((void*)self);   /* the half-built object ends: the objects its fields already hold are dropped */\n    return ({ResultPair(CType(cls.Name))}){{ NULL, error }};\n  }}\n"
        : "  if (error) {\n    df_panic_unhandled(error);\n  }\n");
    } else {
      sb.Append($"  {initCall};\n");
    }
    sb.Append(PairReturn(ctor) ? $"  return ({ResultPair(CType(cls.Name))}){{ self, NULL }};\n}}\n" : "  return self;\n}\n");
    return sb.ToString();
  }

  // at the start of an init function: the parent's init, then the zero values that are not all-zero bits
  string InitPrologue(ClassInfo cls) {
    var sb = new StringBuilder();
    if (cls.IsException && cls.Parent == null) {
      sb.Append("  df_exc_live++;\n");
      sb.Append("  self->message = df_cstr_truncated(v_message.p, v_message.len, 2048);\n");
      sb.Append("  self->function = df_cstr_truncated(\"\", 0, 1024);\n");
    }
    if (cls.Parent != null) {
      string forwarded = cls.Parent.IsException ? ", v_message" : "";
      string parentInit = $"{cls.Parent.Init!.CName}(({CType(cls.Parent.Name)})self{forwarded})";
      if (PairReturn(cls.Parent.Init)) {
        sb.Append($"  df_err = {parentInit};\n");
        sb.Append($"  if (df_err) {{ df_err_thrown = false; goto {fn!.FailLabel}; }}\n");
      } else {
        sb.Append($"  {parentInit};\n");
      }
    }
    var ownFields = IntroducedTraits(cls).SelectMany(trait => trait.Fields).Concat(cls.Fields).Where(field => !field.IsStatic);
    foreach (var field in ownFields) {
      if (!IsAllZeroBits(field.Type)) {
        sb.Append($"  self->{field.CName} = {ZeroValue(field.Type)};\n");
      }
    }
    return sb.ToString();
  }

  bool IsAllZeroBits(string type) {
    return IsInt(type) || IsNumeric(type) || type is "bool" or "char" or "string" or "clock" or "lapse"
      || IsClass(type) || IsArr(type);
  }
}
