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
  public bool IsParentRef { get; set; }      // &Parent name as parent (spec 12.5): a reference, not owned
  public bool ReadOnlyRef { get; set; }      // a plain (read-only) parent: only unrestricted and asparent functions are called through it
  public bool Hidden { get; set; }           // a field of the compiler (the children of a parent, the slot of a child)
  public bool IsReadOnly { get; set; }       // readonly (spec 11.1.4): read with its visibility, written only by the class
  public bool IsWeak { get; set; }           // _T name (spec 12.7): does not own its object, read through enforce, set with = _x
  public bool IsCounted { get; set; }        // @T name (spec 12.7): a user of a counted object, the C field holds the counting block (df_rc*)
  public bool IsWritable { get; set; }       // &@T name, &%T name (spec 12.7): read/write, its object has a lock
  public bool HasAccessors { get; set; }     // a property with R: / W: blocks (spec 11.1.4): read and written through get_ and set_
  public bool CanRead { get; set; } = true;
  public bool CanWrite { get; set; } = true;
  public Node? Init { get; set; }
  public FunSig? StaticInit { get; set; }
  // a field of a group is named "group.field" (spec 11.1.4.1): "__" in C
  string CBase => Name.Replace(".", "__");
  public string CName => IsStatic ? $"s_{Owner.Name}_{CBase}" : Owner.IsTrait ? $"t_{Owner.Name}_{CBase}" : $"m_{CBase}";
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
  public List<Node> CtorNodes { get; } = new();              // the constructors written in the body (spec 11.1.1)
  public List<Node> Fragments { get; } = new();              // the atEnd blocks after its constructors: destructor fragments (spec 11.4)

  // traits: a trait is a ClassInfo that is never instantiated
  public bool IsException { get; set; }                       // derives (directly or not) from Exception
  public FunSig? Dtor { get; set; }                           // the atEnd after the class
  public Node? EqualsNode { get; set; }                       // equals(T other), compiled once for each T
  public bool IsTrait { get; init; }
  public ClassInfo? Super { get; set; }                       // the parent trait
  public List<ClassInfo> Traits { get; } = new();             // the traits a class lists in its header
  public List<ImplInfo> Impls { get; } = new();
  public List<FunSig> TraitMethods { get; } = new();          // the methods of a trait, in slot order
  public FunSig? InitFun { get; set; }                        // a trait: its property initializers and constructor body (__tinit)
  public HashSet<string> Hooked { get; } = new();             // a trait: the methods with executeBefore / executeAfter (the class implements __impl_<name>)
  public FieldInfo? ParentField { get; set; }                 // a child class: its reference to the parent (spec 12.5)
  public bool HasChildren { get; set; }                       // a parent class: it keeps the list of its adopted children
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
  readonly Dictionary<string, (string CName, string Type)> globalConsts = new();   // top-level const: seen by every function
  readonly Dictionary<string, System.Numerics.BigInteger> globalConstValues = new();   // the top-level consts with an integer literal value
  readonly List<string> globalConstOrder = new();
  FunSig? globalConstsInit;
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
    Vis visibility = mods.Contains("public") || mods.Contains("unrestricted") ? Vis.Public : mods.Contains("internal") ? Vis.Internal : Vis.Private;
    bool explicitVisibility = mods.Contains("public") || mods.Contains("internal") || mods.Contains("private") || mods.Contains("unrestricted") || mods.Contains("asparent");
    return (visibility, explicitVisibility, mods.Contains("static"), mods.Contains("const"), mods.Contains("replace"));
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
      AddParentChecks(cls);
    }
    foreach (var cls in classOrder) {
      EmitStruct(cls);
    }
    PlanStatics();
  }

  // a plain primary parameter is a field when something uses it after the construction: a method, an accessor, a secondary
  // constructor, the atEnd of the class, a lambda (also one in a field initializer), or a redeclaration in the body. Field initializers
  // only read it while the object is built (spec 11.1.1)
  static bool PrimaryParamUsedAsField(ClassInfo cls, string name) {
    static bool Uses(Node node, string name) {
      if (node.Kind == "Name" && node.Text == name || node is { Kind: "Member", Children: [{ Kind: "Name", Text: "this" }] } && node.Text == name) {
        return true;
      }
      return node.Children.Any(child => Uses(child, name));
    }
    static bool LambdaUses(Node node, string name) =>
      node.Kind == "Lambda" ? Uses(node, name) : node.Children.Any(child => LambdaUses(child, name));
    var body = cls.Node.Children.First(child => child.Kind == "Block");
    foreach (var child in cls.Node.Children) {
      if (child != body && child.Kind != "PrimaryCtor" && Uses(child, name)) {
        return true;   // atEnd of the class
      }
    }
    foreach (var member in body.Children) {
      if (member.Kind != "Decl") {
        if (Uses(member, name)) {
          return true;
        }
        continue;
      }
      if (member.Text == name) {
        return true;
      }
      foreach (var part in member.Children) {
        if (part.Kind == "Init" ? LambdaUses(part, name) : Uses(part, name)) {
          return true;
        }
      }
    }
    return false;
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
        if (p.Children.Any(child => child.Kind == "AsParent")) {
          RegisterParentParam(cls, p, type, def);
          continue;
        }
        if (p.Kind == "WritableRefParam") {
          throw NotSupported("Reference parameters on constructors", p);
        }
        bool weakParam = p.Children.Any(child => child.Kind == "Weak");
        if (weakParam && !IsClass(type)) {
          throw Error("only objects have weak references", p);
        }
        if (p.Children.Any(child => child.Kind == "Counted")) {
          // class Kennel(@Dog dog): the constructor takes a usage and the field becomes a user (spec 12.7)
          string counted = CountedTypeOf(type, p, true);   // class Hits(&@i32 count): a counted value too
          cls.PrimaryParams.Add(new ParamSig(p.Text!, counted, def?.Children[0]));
          CheckFieldName(cls, p.Text!, p);
          bool writableParam = p.Children.Any(child => child.Kind == "ReadWrite");
          cls.PrimaryParams[^1] = new ParamSig(p.Text!, counted, def?.Children[0]) { IsWritable = writableParam };
          cls.Fields.Add(new FieldInfo(p.Text!, type, cls, p) { FromParam = true, IsCounted = true, IsWritable = writableParam });
          continue;
        }
        bool implicitGift = false;
        if (IsClass(type) && p.Kind != "GiftParam" && !weakParam) {
          if (!PrimaryParamUsedAsField(cls, p.Text!)) {
            // read only during the construction: a read-only lend, as for any function, and no field (spec 11.1.1)
            cls.PrimaryParams.Add(new ParamSig(p.Text!, type, def?.Children[0]));
            continue;
          }
          // used as a field: the object is given, as with ->T (level 1 warning, spec 11.1.1)
          Warn($"the {DisplayName(type)} {p.Text} is given implicitly to the constructor of {DisplayName(cls.Name)}: write ->{DisplayName(type)} {p.Text} to give it explicitly (spec 11.1.1)", p, 1);
          implicitGift = true;
        }
        bool writableWeak = weakParam && p.Children.Any(child => child.Kind == "ReadWrite");
        cls.PrimaryParams.Add(new ParamSig(p.Text!, weakParam ? "_" + type : type, def?.Children[0]) {
          IsGift = (p.Kind == "GiftParam" || implicitGift) && (IsClass(type) || type == "string" || IsArr(type)), IsWeak = weakParam, IsWritable = writableWeak,
          IsImplicitGift = implicitGift, GivenTo = implicitGift ? $"the constructor of {DisplayName(cls.Name)}" : null
        });
        CheckFieldName(cls, p.Text!, p);
        cls.Fields.Add(new FieldInfo(p.Text!, type, cls, p) { FromParam = true, IsWeak = weakParam, IsWritable = writableWeak });
      }
    }

    var body = node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children) {
      switch (member.Kind) {
        case "Decl": {
          var (visibility, _, isStatic, isConst, isOverride) = ReadMods(member);
          if (isOverride) {
            if (!member.Children.Any(child => child.Kind == "Property")) {
              throw Error("'replace' applies to functions and to properties with accessor blocks", member);
            }
            break;   // override i32 level { R: ... }: its accessors override the parent's; the storage stays the parent's (spec 11.1.4)
          }
          if (ModsOf(member).Any(mod => mod is "asparent" or "unrestricted")) {
            throw Error("'asparent' and 'unrestricted' apply to functions", member);
          }
          if (member.Children.Any(child => child.Kind == "Size")) {
            throw NotSupported("Sized declarations (arr<T>[n] name)", member);
          }
          bool countedField = IsCountedDecl(member);   // @Dog friend (spec 12.7); with accessor blocks, a counted property (spec 11.1.4)
          var init = member.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
          bool weakField = member.Children.Any(child => child.Kind == "Weak");
          if (init?.Kind == "WeakInit" && !weakField) {
            throw Error($"a weak field is declared with % before its type: %Type {member.Text}", init);
          }
          var typeNode = member.Children.First(child => TypeNodeKinds.Contains(child.Kind));
          if (typeNode is { Kind: "Type", Text: "var" }) {
            throw Error("'var' cannot be used for a field: write its type", member);
          }
          string type = ResolveType(typeNode);
          var existing = cls.Fields.FirstOrDefault(field => field.Name == member.Text && field.FromParam);
          if (existing != null && !isStatic) {
            // redeclaring a primary parameter sets its visibility (spec 11.1.1)
            if (existing.Type != type || existing.IsCounted != countedField) {
              throw Error($"'{member.Text}' is declared with the type {(existing.IsCounted ? "@" : "")}{existing.Type} in the header", member);
            }
            existing.Visibility = visibility;
            existing.IsReadOnly = ModsOf(member).Contains("readonly");
            if (init != null && !(init.Kind == "WeakInit" && init.Text == "default")) {   // the null start of _T name is not written
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
            IsReadOnly = ModsOf(member).Contains("readonly"),
            IsWeak = weakField,
            IsCounted = countedField,
            IsWritable = member.Children.Any(child => child.Kind == "ReadWrite"),
            Init = init?.Children[0]
          };
          if (countedField && !IsClass(type) && !IsCountedValueType(type)) {
            throw NotSupported(IsCountedValueType(type) ? $"Counted values (@{type}) as fields, results and items (only variables and parameters for now, spec 12.7)" : $"Counted values of type {type} (spec 12.7)", member);
          }
          if (member.Children.FirstOrDefault(child => child.Kind == "Property") is Node property) {
            field.HasAccessors = true;
            field.CanRead = property.Text!.Contains('r');
            field.CanWrite = property.Text!.Contains('w');
          }
          if (weakField && !IsClass(type)) {
            throw Error("only objects have weak references", member);
          }
          if (weakField && isStatic) {
            throw NotSupported("Static weak fields", member);
          }
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
          cls.CtorNodes.Add(member);            // built with the constructor (BuildConstructor)
          break;
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
    if (cls.IsException && ExceptionMembers.Contains(name)) {
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

  // an override keeps the read/write markers of the function it answers: a caller that gives a read-only usage (@T, %T) must not reach a
  // function that writes through it (&@T, &%T), and the reverse; the same for a counted result (spec 12.7)
  void CheckOverrideMarkers(FunSig original, FunSig f, Node at) {
    for (int index = 0; index < original.Params.Count && index < f.Params.Count; index++) {
      var (was, now) = (original.Params[index], f.Params[index]);
      if (was.IsWritable != now.IsWritable) {
        string marker = IsWeakType(was.Type) ? "%" : "@";
        string wanted = (was.IsWritable ? "&" : "") + marker;
        throw Error($"a replacement keeps the markers of the function it answers: '{now.Name}' is {wanted}{was.Type.TrimStart('@', '_')} there (spec 12.7)", at);
      }
    }
    if (original.RetWritable != f.RetWritable) {
      throw Error($"a replacement keeps the markers of the function it answers: it returns {(original.RetWritable ? "&" : "")}{original.Ret} (spec 12.7)", at);
    }
  }

  // a new or moved object that changes owner (->, a return, a field, a collection): when it is a child it becomes an adopted child (spec 12.5), its parent
  // keeps it in its list and every call of its class checks the parent
  Expr Adopted(Expr value, Node at) {
    if (!value.Fresh || !classes.TryGetValue(value.Type, out var child) || child.ParentField is not FieldInfo parentField) {
      return value;
    }
    adoptedClasses.Add(child.Name);
    Warn($"a {DisplayName(child.Name)} that changes owner is an adopted child of its {DisplayName(parentField.Type)}: every call of a {DisplayName(child.Name)} checks that the parent still exists", at, 2);
    string taken = NewTemp();
    string parent = $"{taken}->{parentField.CName}";
    return new Expr($"({{ {CType(value.Type)} {taken} = {value.Code}; if ({taken} != NULL && {parent} != NULL && {taken}->m_df_slot == 0) {{ "
      + $"df_kids_add(&{parent}->m_df_children, (void**)&{parent}, &{taken}->m_df_slot); }} {taken}; }})", value.Type, true);
  }

  // the child classes that change owner somewhere (adopted children): only their functions check the parent (DF_ADOPTED_<class>, Assemble)
  readonly HashSet<string> adoptedClasses = new();

  // every function of a child class starts with the check of an adopted child (spec 12.5): when the parent is gone the child runs its own atEnd
  // (once) and raises UsageOfDestroyedObjectException. The check is compiled only for the classes that are adopted somewhere
  void AddParentChecks(ClassInfo cls) {
    if (cls.ParentField is not FieldInfo parent) {
      return;
    }
    var body = cls.Node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children.Where(member => member.Kind == "Fun" && member.Text != "==" && !IsGenericEquals(member))) {
      if (ReadMods(member).IsStatic || member.Children.FirstOrDefault(child => child.Kind == "Block") is not Node block) {
        continue;
      }
      // a body of one expression returns its value: with the check in front it needs the return written
      var sig = funs.TryGetValue(cls.Name + "." + member.Text, out var overloads) ? overloads.FirstOrDefault(f => f.Node == member) : null;
      if (sig != null && sig.Ret != "void" && !IsMulti(sig.Ret) && block.Children.Count == 1 && block.Children[0].Kind == "ExprStmt") {
        block.Children[0] = Synthetic("Return", block.Children[0]).Add(block.Children[0].Children[0]);
      }
      string atEnd = cls.Dtor != null ? $"{cls.Dtor.CName}(self); " : "";
      string snippet = $"if this.{parent.Name} == null {{\n  throw UsageOfDestroyedObjectException(\"Usage Of Destroyed Object Exception\")\n}}\n";
      var check = new Parser(new Lexer(snippet, member.Line).Tokenize()).ParseProgram().Children[0];
      block.Children.InsertRange(0, new[] {
        Synthetic("RawC", member, $"if (DF_ADOPTED_{cls.Name}) {{"),
        Synthetic("RawC", member, $"if (self->{parent.CName} == NULL && self->m_df_slot != DF_UNS_MAX) {{ self->m_df_slot = DF_UNS_MAX; {atEnd}}}"),
        check,
        Synthetic("RawC", member, "}")
      });
    }
  }

  // Parent name as parent (read-only) or &Parent name as parent (writable): the object that guarantees the existence of this one (spec 12.5). The field is a reference: the child does not own its
  // parent. The parent class gets the list of its adopted children (a hidden field) and the child its slot in that list
  void RegisterParentParam(ClassInfo cls, Node p, string type, Node? def) {
    if (cls.ParentField != null) {
      throw Error($"a class has one parent: '{cls.ParentField.Name}' is already 'as parent'", p);
    }
    if (p.Kind is not ("Param" or "WritableRefParam") || p.Children.Any(child => child.Kind is "Counted" or "Weak")) {
      throw Error($"'as parent' goes on a borrowed parameter: {p.Children[0].Text} {p.Text} as parent, or &{p.Children[0].Text} {p.Text} as parent", p);
    }
    if (def != null) {
      throw Error("the parent cannot have a default value", p);
    }
    if (!classes.TryGetValue(type, out var parentClass)) {
      throw traits.ContainsKey(type) ? NotSupported("A trait as the type of the parent", p) : Error($"the parent must be an object, not {type}", p);
    }
    CheckFieldName(cls, p.Text!, p);
    cls.PrimaryParams.Add(new ParamSig(p.Text!, type, null) { Ref = p.Kind == "Param" ? 1 : 2, IsParent = true });
    var field = new FieldInfo(p.Text!, type, cls, p) { FromParam = true, IsParentRef = true, ReadOnlyRef = p.Kind == "Param" };
    cls.Fields.Add(field);
    cls.ParentField = field;
    cls.Fields.Add(new FieldInfo("df_slot", NativeUns, cls, p) { Hidden = true });
    if (!parentClass.HasChildren) {
      parentClass.HasChildren = true;
      parentClass.Fields.Add(new FieldInfo("df_children", "u64", parentClass, p) { Hidden = true });
    }
  }

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
      if (ModsOf(member).Contains("readonly")) {
        throw Error("'readonly' applies to fields", member);
      }
      if (ModsOf(member).Contains("extern")) {
        throw Error("an extern function is declared at the top level, not in a class or an enum", member);
      }
      if (IsGenericEquals(member)) {
        RegisterEquals(cls, member);
        continue;
      }
      if (member.Children.Any(child => child.Kind == "NoBody")) {
        throw Error("a function without a body is virtual and can be used only in traits", member);
      }
      if (member.Children.FirstOrDefault(child => child.Kind is "ExecuteBefore" or "ExecuteAfter") is Node hook) {
        throw Error($"'{(hook.Kind == "ExecuteBefore" ? "executeBefore" : "executeAfter")}' goes on a function of a trait", hook);
      }
      // replace fun m() answering a trait method with executeBefore / executeAfter: the class implements __impl_m, the trait keeps m (spec 11.2)
      if (isOverride && AllTraits(cls).Any(trait => trait.Hooked.Contains(member.Text!))) {
        member.Text = ImplPrefix + member.Text;
      }
      if (cls.Fields.Any(field => field.Name == member.Text)) {
        throw Error($"'{member.Text}' is already a field of {cls.Name}", member);
      }
      if (isOverride && explicitVisibility) {
        throw Error("a replacing function takes the visibility of the original: write none on it", member);
      }
      var f = RegisterFun(member, null, cls)!;
      f.Class = cls;
      f.IsAccessor = member.Children.Any(child => child.Kind == "AccessorFun");
      f.IsStatic = isStatic;
      f.IsOverride = isOverride;
      f.Visibility = visibility;
      var mods = ModsOf(member);
      f.IsAsParent = mods.Contains("asparent");
      f.IsUnrestricted = mods.Contains("unrestricted");
      if (mods.Count(mod => mod is "public" or "internal" or "private" or "asparent" or "unrestricted") > 1) {
        throw Error("a function has one visibility: public, internal, private, asparent or unrestricted", member);
      }
      if (f.IsAsParent && isStatic) {
        throw Error("an asparent function is called through the parent reference: it cannot be static", member);
      }
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
            throw Error($"'{member.Text}' already exists in the trait {ofTrait.Class!.Name}: write 'replace'", member);
          }
          if (ofTrait.Ret != f.Ret) {
            throw Error($"it returns {ofTrait.Ret}, like the trait function it answers", member);
          }
          CheckOverrideMarkers(ofTrait, f, member);
          f.Visibility = ofTrait.Visibility;   // the visibility of the trait function (private: the trait and the class itself)
          f.IsAsParent = ofTrait.IsAsParent;
          f.IsUnrestricted = ofTrait.IsUnrestricted;
        } else if (member.Text == "get_hash" && f.Params.Count == 0) {
          // get_hash() is offered by Root.Class (spec 6.2.1, 11.1.3): the first override in a class chain
          if (!isOverride) {
            throw Error("get_hash is defined by Root.Class: write 'replace'", member);
          }
          if (f.Ret != NativeUns) {
            throw Error("get_hash returns uns", member);
          }
        } else if (isOverride) {
          throw Error($"'{member.Text}' replaces nothing: no function with this signature in a parent class or trait", member);
        }
        f.Slot = cls.Vtable.Count;
        cls.Vtable.Add(f);
        continue;
      }
      if (above.IsStatic) {
        throw Error($"'{member.Text}' hides the static function of {above.Class!.Name}", member);
      }
      if (!isOverride) {
        throw Error($"'{member.Text}' already exists in {above.Class!.Name}: write 'replace'", member);
      }
      if (above.Ret != f.Ret) {
        throw Error($"a replacement returns {above.Ret}, like the function it replaces", member);
      }
      CheckOverrideMarkers(above, f, member);
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
    var at = cls.Node;
    var block = Synthetic("Block", at);
    var ctorFields = cls.Fields.Where(field => !field.IsStatic).ToList();
    foreach (var field in ctorFields.Where(field => field.FromParam)) {
      Node source = Synthetic("Name", at, field.Name);
      if (cls.PrimaryParams.First(param => param.Name == field.Name).IsGift) {
        source = Synthetic("Gift", at).Add(source);   // the object moves from the parameter into the field
      }
      block.Add(Assignment(field.Name, source, at, false, field.IsWeak));
    }
    foreach (var field in ctorFields.Where(field => field.IsCounted && field.Init == null && !field.FromParam && !field.Hidden
        && IsCountedValueType(field.Type) && !IsNullable(field.Type))) {
      // &@i32 hits: a counted value field starts with a cell of its own holding the zero value (spec 12.7)
      string plain = BaseOf(field.Type);
      block.Add(Assignment(field.Name, Synthetic("CValue", at, ZeroValue(plain)).Add(Synthetic("Type", at, plain)), at, false, false));
    }
    foreach (var field in ctorFields.Where(field => field.Init != null)) {
      if (field.HasAccessors && !field.CanWrite) {
        // a start value goes through W: (spec 11.1.4); without one, straight into the storage
        block.Add(Synthetic("Assign", at, "=").Add(Synthetic("RawField", at, field.Name)).Add(field.Init!));
        continue;
      }
      block.Add(Assignment(field.Name, field.Init!, at, false, field.IsWeak));
    }
    // the init functions of the class (Foo.__ctor, what a chain : Foo(...) calls) and its constructors (Foo)
    var inits = new List<FunSig>();
    var ctors = new List<FunSig>();
    funs[cls.Name + InitsSuffix] = inits;
    funs[cls.Name] = ctors;
    if (cls.Parent != null && !cls.Parent.IsException && funs[cls.Parent.Name].All(ctor => ctor.Visibility == Vis.Private)) {
      throw Error($"{DisplayName(cls.Parent.Name)} has only private constructors: it cannot be inherited", cls.Node);
    }
    var implicitParent = ImplicitParentInit(cls);
    bool hasPrimary = cls.Node.Children.Any(child => child.Kind == "PrimaryCtor");
    if (hasPrimary || cls.CtorNodes.Count == 0) {
      if (cls.Parent != null && implicitParent == null && PrivateParentInit(cls)) {
        throw Error($"the constructor of {DisplayName(cls.Parent.Name)} without parameters is private: a subclass cannot call it", cls.Node);
      }
      if (cls.Parent != null && implicitParent == null) {
        throw NotSupported($"Passing arguments to the constructor of the parent class {cls.Parent.Name}", cls.Node);
      }
      // the traits first (their constructors without parameters), then the fields
      var primaryBlock = Synthetic("Block", at);
      foreach (var statement in TraitInitStatements(cls, at, null).Concat(block.Children)) {
        primaryBlock.Add(statement);
      }
      var initNode = Synthetic("Fun", at, "__init").Add(Synthetic("Params", at)).Add(primaryBlock);

      var ctorParams = cls.IsException ? ExceptionConstructorParams(cls) : cls.PrimaryParams.ToList();
      var initSig = new FunSig(cls.Name + ".__init", "void", ctorParams, initNode, null) {
        Class = cls,
        IsInit = true,
        Visibility = Vis.Public,
        ParentInit = implicitParent
      };
      inits.Add(initSig);
      var ctorSig = new FunSig(cls.Name, cls.Name, ctorParams.ToList(), initNode, null) {
        Class = cls,
        IsCtor = true,
        Visibility = Vis.Public,
        CtorInit = initSig
      };
      ctors.Add(ctorSig);
    }
    if (cls.CtorNodes.Count > 0) {
      BuildBodyConstructors(cls, block, hasPrimary, implicitParent, inits, ctors);
    }
    // what a child runs when its constructor names no constructor of this class: the one without parameters
    cls.Init = inits.FirstOrDefault(init => init.Params.Count == 0) ?? (cls.IsException ? inits[0] : null);
    cls.Ctor = ctors[0];

    // the atEnd after the class is its destructor: a function of the object, run when it ends
    // the destructor fragments run first, the last registered first, then the atEnd of the class (spec 11.4)
    var trailing = cls.Node.Children.FirstOrDefault(child => child.Kind == "AtEnd" && child.Text == "trailing");
    if (trailing != null || cls.Fragments.Count > 0) {
      var dtorBlock = Synthetic("Block", at);
      if (cls.Fragments.Count > 0) {
        dtorBlock.Add(FragmentRunner(cls, at));
      }
      foreach (var statement in trailing?.Children[0].Children ?? new List<Node>()) {
        dtorBlock.Add(statement);
      }
      var dtorNode = Synthetic("Fun", at, "__dtor").Add(Synthetic("Params", at)).Add(dtorBlock);
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

  // The constructors written in the class body (spec 11.1.1): overloads of the class name (13.2). Each one has its own init
  // function. Without a chain: the parent's constructor without parameters, the fields as in the primary constructor (traits,
  // initializers), then its body. With : Foo(...): that constructor of the class does all of it, then the body. With : Parent(...):
  // that constructor of the parent, the fields, then the body. There is no default constructor.
  void BuildBodyConstructors(ClassInfo cls, Node fieldBlock, bool hasPrimary, FunSig? implicitParent, List<FunSig> inits, List<FunSig> ctors) {
    if (cls.IsException) {
      throw NotSupported("Constructors in the body of an exception class", cls.CtorNodes[0]);
    }
    int index = 0;
    foreach (var member in cls.CtorNodes) {
      Node? selfCall = null;
      Node? parentCall = null;
      var traitCalls = new Dictionary<string, Node>();
      var chain = member.Children.FirstOrDefault(child => child.Kind == "Chain");
      foreach (var call in chain?.Children ?? new List<Node>()) {
        string target = call.Text!;
        if (target == cls.Name) {
          selfCall = selfCall == null ? call : throw Error($"a constructor calls one other constructor of {cls.Name}", call);
        } else if (cls.Parent != null && target == cls.Parent.Name) {
          parentCall = parentCall == null ? call : throw Error($"a constructor calls one constructor of {cls.Parent.Name}", call);
        } else if (IntroducedTraits(cls).Any(trait => trait.Name == target)) {
          if (!traitCalls.TryAdd(target, call)) {
            throw Error($"a constructor calls one constructor of the trait {target}", call);
          }
        } else if (AllTraits(cls).Any(trait => trait.Name == target)) {
          throw Error($"the trait {target} comes from {cls.Parent!.Name}: its constructor runs in the constructor of {cls.Parent.Name}", call);
        } else {
          throw Error($"'{target}' is not {cls.Name}, its parent or one of its traits: a constructor chain calls only their constructors", call);
        }
      }
      if (selfCall != null && (parentCall ?? traitCalls.Values.FirstOrDefault()) is Node extra) {
        throw Error($"a constructor that calls another constructor of {cls.Name} cannot also call {extra.Text}: the other constructor does it", extra);
      }
      if (hasPrimary && selfCall == null) {
        // no field may be left in an unpredictable state: every constructor reaches the primary one
        throw Error($"a constructor of {cls.Name} must reach the primary constructor: write : {cls.Name}(...) after its parameters", member);
      }
      if (cls.Parent != null && selfCall == null && parentCall == null && implicitParent == null && PrivateParentInit(cls)) {
        throw Error($"the constructor of {DisplayName(cls.Parent.Name)} without parameters is private: a constructor of {cls.Name} must call another one, with : {cls.Parent.Name}(...)", member);
      }
      if (cls.Parent != null && selfCall == null && parentCall == null && implicitParent == null) {
        throw Error($"a constructor of {cls.Name} must call a constructor of {cls.Parent.Name} (it has none without parameters): write : {cls.Parent.Name}(...) after its parameters", member);
      }
      var (visibility, _, isStatic, isConst, isOverride) = ReadMods(member);
      if (isStatic || isConst || isOverride || ModsOf(member).Any(mod => mod is "readonly" or "asparent" or "unrestricted" or "extern")) {
        throw Error("a constructor takes only a visibility (public, internal or private)", member);
      }
      var paramsNode = member.Children.First(child => child.Kind == "Params");
      if (paramsNode.Children.FirstOrDefault(p => p.Kind == "VariadicParam") is Node variadic) {
        throw NotSupported("Variadic constructor parameters", variadic);
      }
      var block = Synthetic("Block", member);
      if ((selfCall ?? parentCall) is Node called) {
        block.Add(Synthetic("ExprStmt", called).Add(Synthetic("CtorChain", called, called.Text).Add(called.Children[0])));
      }
      if (selfCall == null) {
        foreach (var statement in TraitInitStatements(cls, member, traitCalls)) {
          block.Add(statement);
        }
        foreach (var statement in fieldBlock.Children) {
          block.Add(CloneNode(statement));
        }
      }
      // } atEnd { }: registered here, after the chain, the traits and the fields, before the body (spec 11.4)
      foreach (var statement in DestructorFragment(cls, member, paramsNode)) {
        block.Add(statement);
      }
      if (member.Children.FirstOrDefault(child => child.Kind == "Block") is Node body) {
        foreach (var statement in body.Children) {
          block.Add(statement);
        }
      }
      var initNode = Synthetic("Fun", member, "__init" + index++).Add(paramsNode).Add(block);
      var init = RegisterFun(initNode, null, cls)!;
      funs.Remove(init.Name);           // listed with the other init functions of the class
      init.Class = cls;
      init.IsInit = true;
      init.Visibility = visibility == Vis.Private ? Vis.Private : Vis.Public;   // private: only the code of the class calls it (spec 11.1.1)
      init.ChainsToSelf = selfCall != null;
      init.ParentInit = selfCall == null && parentCall == null ? implicitParent : null;
      if (ctors.Any(existing => existing.Params.Select(param => param.Type).SequenceEqual(init.Params.Select(param => param.Type)))) {
        throw Error($"a constructor of {cls.Name} with these parameter types is already defined", member);
      }
      inits.Add(init);
      ctors.Add(new FunSig(cls.Name, cls.Name, init.Params.ToList(), initNode, null) {
        Class = cls,
        IsCtor = true,
        Visibility = init.Visibility,
        CtorInit = init,
        OverloadIndex = ctors.Count
      });
    }
  }

  const string InitsSuffix = ".__ctor";

  // A destructor fragment (spec 11.4): the atEnd after a constructor. It runs when the object dies and sees the class members and the
  // parameters and locals of its constructor: the ones it uses become hidden fields of the object (__f<n>_<name>), so they live until it
  // runs; the constructor uses those fields in their place. Registering it stamps the order (__fseq): at death the last one runs first.
  // Gives the statements that register it and keep its parameters; the fragment itself goes to cls.Fragments.
  List<Node> DestructorFragment(ClassInfo cls, Node member, Node paramsNode) {
    var statements = new List<Node>();
    if (member.Children.FirstOrDefault(child => child.Kind == "AtEnd") is not Node trailing) {
      return statements;
    }
    var fragment = trailing.Children[0];
    var body = member.Children.FirstOrDefault(child => child.Kind == "Block");
    int number = cls.Fragments.Count;
    string Hidden(string name) => $"__f{number}_{name}";
    var used = new HashSet<string>();
    CollectNames(fragment, used);
    var renames = new Dictionary<string, string>();

    foreach (var p in paramsNode.Children.Where(p => used.Contains(p.Text!))) {
      if (p.Kind is not ("Param" or "GiftParam") || p.Children.Any(child => child.Kind is "Weak")) {
        throw NotSupported("A destructor fragment that uses a reference or weak parameter", p);
      }
      string type = ResolveType(p.Children[0]);
      if (p.Children.Any(child => child.Kind == "Counted")) {
        // @Dog d: the fragment keeps a usage of its own in a hidden counted field, released with the object (spec 12.7)
        cls.Fields.Add(new FieldInfo(Hidden(p.Text!), type, cls, p) { Hidden = true, IsCounted = true });
        statements.Add(Assignment(Hidden(p.Text!), Synthetic("Name", p, p.Text), p, false));
        renames[p.Text!] = Hidden(p.Text!);
        continue;
      }
      if (IsArr(type)) {
        throw NotSupported("A destructor fragment that uses an array parameter", p);
      }
      if (IsClass(type) && p.Kind != "GiftParam") {
        throw Error($"the destructor fragment keeps '{p.Text}' until the object dies: a borrowed object cannot be kept, take it with ->{type} {p.Text}", p);
      }
      AddHiddenField(cls, Hidden(p.Text!), type, p);
      Node source = Synthetic("Name", p, p.Text);
      if (p.Kind == "GiftParam" && (IsClass(type) || type == "string")) {
        source = Synthetic("Gift", p).Add(source);   // the object moves into the object, for the fragment
      }
      statements.Add(Assignment(Hidden(p.Text!), source, p, false));
      renames[p.Text!] = Hidden(p.Text!);
    }

    if (body != null) {
      var decls = new List<(Node Parent, Node Decl)>();
      CollectDecls(body, decls);
      foreach (var group in decls.Where(d => used.Contains(d.Decl.Text!) && !renames.ContainsKey(d.Decl.Text!)).GroupBy(d => d.Decl.Text!)) {
        if (group.Count() > 1) {
          throw NotSupported("A destructor fragment that uses a name declared twice in its constructor", group.Last().Decl);
        }
        var (parent, decl) = group.First();
        var typeNode = decl.Children[0];
        if (typeNode is { Kind: "Type", Text: "var" }) {
          throw Error($"the destructor fragment keeps '{decl.Text}': write its type instead of var", decl);
        }
        if (decl.Children.Any(child => child.Kind is "Weak" or "Size")) {
          throw NotSupported("A destructor fragment that uses a weak or sized local", decl);
        }
        string type = ResolveType(typeNode);
        if (IsArr(type)) {
          throw NotSupported("A destructor fragment that uses an array local", decl);
        }
        bool countedLocal = decl.Children.Any(child => child.Kind == "Counted");
        if (countedLocal) {
          cls.Fields.Add(new FieldInfo(Hidden(decl.Text!), type, cls, decl) { Hidden = true, IsCounted = true });   // a counted local: a hidden counted field
        } else {
          AddHiddenField(cls, Hidden(decl.Text!), type, decl);
        }
        renames[decl.Text!] = Hidden(decl.Text!);
        // the declaration becomes the assignment of the hidden field (nothing when it has no value: the field starts as zero)
        int at = parent.Children.IndexOf(decl);
        parent.Children.RemoveAt(at);
        if (decl.Children.FirstOrDefault(child => child.Kind == "Init") is Node init) {
          parent.Children.Insert(at, Assignment(Hidden(decl.Text!), init.Children[0], decl, false));
        }
      }
      RenameToFields(body, renames);
    }
    RenameToFields(fragment, renames);

    if (FindField(cls, "__fseq") == null) {
      AddHiddenField(cls, "__fseq", "i32", member);
    }
    AddHiddenField(cls, Hidden("at"), "i32", member);
    Node Field(string name) => Synthetic("Member", member, name).Add(Synthetic("Name", member, "this"));
    statements.Insert(0, Assignment("__fseq", Synthetic("Binary", member, "+").Add(Field("__fseq")).Add(Synthetic("Int", member, "1")), member, false));
    statements.Insert(1, Assignment(Hidden("at"), Field("__fseq"), member, false));
    cls.Fragments.Add(fragment);
    return statements;
  }

  // while __fseq > 0 { if __f0_at == __fseq { fragment 0 } ... __fseq = __fseq - 1 }: the fragments in reverse order of registration
  Node FragmentRunner(ClassInfo cls, Node at) {
    Node Field(string name) => Synthetic("Member", at, name).Add(Synthetic("Name", at, "this"));
    var loop = Synthetic("Block", at);
    for (int number = 0; number < cls.Fragments.Count; number++) {
      var test = Synthetic("Binary", at, "==").Add(Field($"__f{number}_at")).Add(Field("__fseq"));
      loop.Add(Synthetic("If", at).Add(test).Add(cls.Fragments[number]));
    }
    loop.Add(Assignment("__fseq", Synthetic("Binary", at, "-").Add(Field("__fseq")).Add(Synthetic("Int", at, "1")), at, false));
    return Synthetic("While", at).Add(Synthetic("Binary", at, ">").Add(Field("__fseq")).Add(Synthetic("Int", at, "0"))).Add(loop);
  }

  static void AddHiddenField(ClassInfo cls, string name, string type, Node at) {
    cls.Fields.Add(new FieldInfo(name, type, cls, at) { Hidden = true });
  }

  static void CollectNames(Node node, HashSet<string> names) {
    if (node.Kind == "Name") {
      names.Add(node.Text!);
    }
    foreach (var child in node.Children) {
      CollectNames(child, names);
    }
  }

  static void CollectDecls(Node node, List<(Node Parent, Node Decl)> decls) {
    foreach (var child in node.Children) {
      if (child.Kind == "Decl") {
        decls.Add((node, child));
      }
      CollectDecls(child, decls);
    }
  }

  // name -> this.__f<n>_name, wherever the variable is read, written, moved or ended
  static void RenameToFields(Node node, Dictionary<string, string> renames) {
    if (node.Kind == "Name" && renames.TryGetValue(node.Text!, out var field)) {
      node.Kind = "Member";
      node.Text = field;
      node.Children.Add(Synthetic("Name", node, "this"));
      return;
    }
    foreach (var child in node.Children) {
      RenameToFields(child, renames);
    }
  }


  // the init function a constructor of the child runs when it names no constructor of the parent: the one without parameters
  // (an exception class: its own, which receives the message)
  FunSig? ImplicitParentInit(ClassInfo cls) {
    if (cls.Parent == null) {
      return null;
    }
    return cls.Parent.IsException ? (cls.Parent.PrimaryParams.Count == 0 ? cls.Parent.Init : null)
      : funs[cls.Parent.Name + InitsSuffix].FirstOrDefault(init => init.Params.Count == 0 && init.Visibility != Vis.Private);
  }

  // the parent has a constructor without parameters, but it is private (a subclass cannot call it)
  bool PrivateParentInit(ClassInfo cls) =>
    !cls.Parent!.IsException && funs[cls.Parent.Name + InitsSuffix].Any(init => init.Params.Count == 0 && init.Visibility == Vis.Private);

  // : Foo(args) or : Parent(args) at the start of a constructor: the init function of that constructor, on the same object
  readonly Dictionary<FunSig, FunSig> ctorChains = new();
  Expr CtorChainCall(Node n, Scope scope) {
    var args = n.Children[0].Children;
    if (traits.TryGetValue(n.Text!, out var trait)) {
      // : Trait(args): the init function of that constructor of the trait (its property initializers, then its body)
      FunSig tinit;
      List<string> traitCodes;
      string traitPrefix;
      try {
        tinit = ResolveOverload(funs[trait.Name + TraitInitSuffix], args, scope, n);
        traitCodes = ArgCodes(tinit, args, scope, n, out traitPrefix);
      } catch (CompileException e) {
        throw new CompileException(e.Message.Replace($"'{trait.Name}{TraitInitSuffix}'", $"'{trait.Name}'"), e.Line);
      }
      traitCodes.Insert(0, "(void*)self");
      return ArgsFirst(new Expr(UserCallCode(tinit, $"{tinit.CName}({string.Join(", ", traitCodes)})"), "void"), traitPrefix);
    }
    var target = classes[n.Text!];
    var chosenCtor = ResolveOverload(funs[target.Name], args, scope, n);
    if (target != currentClass && chosenCtor.Visibility == Vis.Private) {
      throw Error($"this constructor of {DisplayName(target.Name)} is private: a subclass cannot call it", n);
    }
    var chosen = chosenCtor.CtorInit!;
    if (target == currentClass && currentFun != null) {
      // a cycle of chains would never end: a compile error (spec 11.1.1)
      ctorChains[currentFun] = chosen;
      for (var next = chosen; ; ) {
        if (next == currentFun) {
          throw Error($"the constructors of {DisplayName(target.Name)} call each other in a cycle", n);
        }
        if (!ctorChains.TryGetValue(next, out var after)) {
          break;
        }
        next = after;
      }
    }
    var codes = ArgCodes(chosen, args, scope, n, out string prefix);
    codes.Insert(0, $"(({CType(target.Name)})self)");
    return ArgsFirst(new Expr(UserCallCode(chosen, $"{chosen.CName}({string.Join(", ", codes)})"), "void"), prefix);
  }

  // this.name = value for an instance field, name = value for a static one; _= for a weak field
  static Node Assignment(string field, Node value, Node at, bool isStatic, bool weak = false) {
    Node target = isStatic
      ? Synthetic("Name", at, field)
      : Synthetic("Member", at, field).Add(Synthetic("Name", at, "this"));
    return Synthetic("Assign", at, weak ? "_=" : "=").Add(target).Add(value);
  }

  // struct, descriptor and storage of statics
  void EmitStruct(ClassInfo cls) {
    var fields = new StringBuilder();
    foreach (var field in AllInstanceFields(cls)) {
      // the first and the last node of a list are node pointers in C (CodeGen.List.cs)
      string fieldType = IsList(cls.Name) && field.Name is "first" or "last" ? ListNode(cls.Name) + "*"
        : IsListIterator(cls.Name) && field.Name == "node" ? ListNode(cls.ParentField!.Type) + "*"
        : (IsMap(cls.Name) || IsSet(cls.Name)) && field.Name == "tree" ? (IsHmap(cls.Name) ? "df_ht" : "df_bt")
        : field.Hidden && field.Name == "df_children" ? "df_kids*" : field.IsCounted || field.IsWeak ? "df_rc*" : CType(field.Type);
      fields.Append($"  {fieldType} {field.CName};\n");
    }
    typeDefs.Append($"struct {cls.StructName} {{\n  const df_class* cls;\n{(cls.IsException ? ExceptionFields : "")}{fields}}};\n\n");
    foreach (var field in cls.Fields.Where(field => field.IsStatic)) {
      classGlobals.Append($"static {(field.IsCounted || field.IsWeak ? "df_rc*" : CType(field.Type))} {field.CName};\n");   // a counted static holds its counting block
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
    if (found.Count == 0 && at != null && !cls.IsTrait && FieldCandidates(cls, name, true).FirstOrDefault() is FieldInfo hidden) {
      throw Error($"'{name}' is private in the trait {DisplayName(hidden.Owner.Name)}: only the code of the trait sees it", at);
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

  // top-level const: C globals set at program start, in source order, before the statics (which may read them);
  // they leave the top-level code, so their values can use literals, other consts, functions and statics only
  void DeclareGlobalConsts(Node program) {
    var decls = program.Children.Where(statement => statement.Kind == "Decl" && !IsGlobalDecl(statement)   // global const: an error of DeclareGlobals
      && statement.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("const") == true).ToList();
    if (decls.Count == 0) {
      return;
    }
    var at = decls[0];
    var block = Synthetic("Block", at);
    foreach (var decl in decls) {
      string name = decl.Text!;
      if (globalConsts.ContainsKey(name)) {
        throw Error($"'{name}' is already declared in this scope", decl);
      }
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init") ?? throw Error("a const needs a value", decl);
      string type = ResolveDeclType(decl, null);
      globalConsts[name] = ($"g_{name}", type);
      if (IntLiteralValue(init.Children[0]) is System.Numerics.BigInteger constValue) {
        globalConstValues[name] = constValue;   // a tuple index can use it (spec 3.2)
      }
      globalConstOrder.Add(name);
      classGlobals.Append($"static {CType(type)} g_{name};\n");
      block.Add(Synthetic("Assign", decl, "=").Add(Synthetic("Name", decl, name)).Add(init.Children[0]));
      program.Children.Remove(decl);
    }
    var node = Synthetic("Fun", at, "__consts").Add(Synthetic("Params", at)).Add(block);
    globalConstsInit = new FunSig("__consts", "void", new List<ParamSig>(), node, null) { IsStaticInit = true };
    funs[globalConstsInit.Name] = new List<FunSig> { globalConstsInit };
  }

  // C statements run at program start: zero values, then the initializers in dependency order
  string StaticInitCode() {
    var sb = new StringBuilder();
    foreach (var name in globalConstOrder) {
      sb.Append($"  {globalConsts[name].CName} = {ZeroValue(globalConsts[name].Type)};\n");
    }
    foreach (var field in staticOrder) {
      if (field.IsCounted && IsCountedValueType(field.Type) && field.Init == null && !IsNullable(field.Type)) {
        // static &@i32 total: a cell of its own holding the zero value (spec 12.7)
        string plain = BaseOf(field.Type);
        sb.Append($"  {field.CName} = {NewCell(new Expr(ZeroValue(plain), plain), field.Type, field.At)};\n");
        continue;
      }
      sb.Append($"  {field.CName} = {(field.IsCounted || field.IsWeak ? "NULL" : ZeroValue(field.Type))};\n");   // a counted static holds a block
    }
    if (globalConstsInit != null) {
      sb.Append(PairReturn(globalConstsInit)
        ? $"  {{\n    df_exception* error = {globalConstsInit.CName}();\n    if (error) {{\n      df_panic_unhandled(error);\n    }}\n  }}\n"
        : $"  {globalConstsInit.CName}();\n");
    }
    foreach (var field in staticOrder.Where(field => field.StaticInit != null)) {
      if (PairReturn(field.StaticInit!)) {
        sb.Append($"  {{\n    df_exception* error = {field.StaticInit!.CName}();\n    if (error) {{\n      df_panic_unhandled(error);\n    }}\n  }}\n");
      } else {
        sb.Append($"  {field.StaticInit!.CName}();\n");
      }
    }
    sb.Append(GlobalsInitCode());   // the globals, in declaration order (spec 18.2)
    return sb.ToString();
  }

  // ---------------------------------------------------------------- access rules

  static string[] ModsOf(Node member) =>
    member.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();

  // the C of the parent reference of the class being compiled, written as the bare name or as this.name, or null
  FieldInfo? ParentReceiver(string code) {
    if (currentClass?.ParentField is not FieldInfo parent) {
      return null;
    }
    return code == $"self->{parent.CName}" || code == $"(self)->{parent.CName}" ? parent : null;
  }

  // the parent reference when it is read-only &, or null
  FieldInfo? ReadOnlyParent(string code) => ParentReceiver(code) is { ReadOnlyRef: true } parent ? parent : null;

  // the variable a receiver names when it is a read-only reference (&Item o, &arr<T> a), or null
  VarInfo? ReadOnlyVariable(Node receiverNode, Scope scope) =>
    receiverNode.Kind == "Name" && scope.Find(receiverNode.Text!) is { ReadOnlyRef: true } variable ? variable : null;

  // the members of the collections of the compiler that do not change them: the only ones a read-only reference can call (spec 1.6.2)
  static readonly HashSet<string> ReadOnlyCollectionMembers = new() {
    "has", "indexOf", "indexOfFrom", "getIterator", "hasKey", "tryGet", "keyOf", "clone", "read_pos", "viewHasKey", "viewHasValue", "viewLen",
    "union", "intersection", "difference", "symmetricDifference", "isSubsetOf", "isSupersetOf", "isDisjointFrom", "filter", "map", "reduce"
  };

  // the members of File that do not change the File object (it only holds a path): they can be called through a read-only reference
  static readonly HashSet<string> ReadOnlyFileMembers = new() {
    "exists", "copyTo", "getAllFiles", "readAll", "readAllText", "writeAll", "writeAllText", "delete", "open", "createDirectory"
  };

  // spec 1.6.2: through a read-only reference & only the unrestricted functions (and static ones) can be called; for the collections of the compiler
  // the members that do not change them
  void CheckReadOnlyCall(Node receiverNode, Expr receiver, string name, Scope scope, Node at) {
    if (ReadOnlyCounted(receiverNode, scope) is CountedRefInfo counted) {
      // through a read-only @T only the unrestricted and static methods (spec 12.7)
      var counts = IsClass(receiver.Type) ? LookupMethods(ObjInfo(receiver.Type), name) : new List<FunSig>();
      if (counts.Count > 0 && !counts.All(f => f.IsUnrestricted || f.IsStatic) && name != "getIterator") {
        throw Error($"'{counted.Name}' is a read-only counted object (@{counted.Type}): '{name}' needs a read/write one (mark it unrestricted, "
          + $"or use a &@{counted.Type}){DefinedAt(counts, name)} (spec 12.7)", at);
      }
      return;
    }
    if (ReadOnlyVariable(receiverNode, scope) is null) {
      if (IsArr(receiver.Type) && !ReadOnlyCollectionMembers.Contains(name) && structs.Count > 0) {
        CheckBufferWrite(receiverNode, scope, at);   // s.m.fill(0) through a read-only struct (spec 11.1.5.4)
      }
      return;
    }
    string what = receiverNode.Text!;
    bool library = IsArr(receiver.Type) || IsVec(receiver.Type) || IsList(receiver.Type)
      || (classes.TryGetValue(receiver.Type, out var libraryClass) && libraryClass.Node.Line >= LibraryLine)
      || (traits.TryGetValue(receiver.Type, out var libraryTrait) && libraryTrait.Node.Line >= LibraryLine);
    bool allowed;
    if (receiver.Type == "File" && ReadOnlyFileMembers.Contains(name)) {
      allowed = true;
    } else if (library) {
      allowed = ReadOnlyCollectionMembers.Contains(name);
    } else {
      var methods = IsClass(receiver.Type) ? LookupMethods(ObjInfo(receiver.Type), name) : new List<FunSig>();
      allowed = methods.Count == 0 || methods.All(f => f.IsUnrestricted || f.IsStatic) || name == "getIterator";   // iterating reads (6.7)
    }
    if (!allowed) {
      throw Error(library
        ? $"'{what}' is a read-only reference: '{name}' can change it, so it needs an owned or writable reference (take &{what})"
        : scope.Find(what) is { IsLocal: true, IsParam: false }
        ? $"'{what}' is a read-only borrow: '{name}' needs a read/write one (mark it unrestricted, or declare &{DisplayName(receiver.Type)} {what})"
        : $"'{what}' is a read-only reference: '{name}' needs an owned or writable reference (mark it unrestricted, or take &{what})"
          + DefinedAt(LookupMethods(ObjInfo(receiver.Type), name), name), at);
    }
  }

  // a read-only counted object a receiver names (@T variable, field, parameter or item, spec 12.7), or null; &@T is read/write
  CountedRefInfo? ReadOnlyCounted(Node receiverNode, Scope scope) =>
    !LibraryCode && receiverNode.Kind is "Name" or "Member" or "Index" or "Paren" && !(receiverNode is { Kind: "Name", Text: "this" })
      && CountedRef(receiverNode, scope) is CountedRefInfo counted && !IsWritableRef(counted) ? counted : null;

  // where the function that is not unrestricted is written, for the errors of the read-only places: ", 'bark' is defined at dog.df(4)"
  string DefinedAt(IEnumerable<FunSig> candidates, string name) =>
    candidates.FirstOrDefault(f => !f.IsUnrestricted && !f.IsStatic && f.Node.Line > 0 && f.Node.Line < LibraryLine) is FunSig found
      ? $"; '{name}' is defined at {SourceMap.Short(found.Node.Line)}" : "";

  // spec 12.5: an asparent function is called only by a child, through its parent reference; through a read-only reference & only the
  // unrestricted and asparent functions can be called
  void CheckParentAccess(FunSig chosen, string? receiver, string name, Node at) {
    var parent = receiver == null ? null : ParentReceiver(receiver);
    if (chosen.IsAsParent) {
      // the object itself: code of its class, of a subclass, or of a trait (asparent in a trait: the class that implements it)
      bool itself = receiver == "self" && currentClass != null && (currentClass == chosen.Class || IsSubtype(currentClass.Name, chosen.Class!.Name));
      if (parent == null && !itself) {
        throw Error($"'{name}' is asparent: only a {DisplayName(chosen.Class!.Name)} itself and its children (through their parent reference) call it", at);
      }
      return;
    }
    if (parent is { ReadOnlyRef: true } && !chosen.IsUnrestricted && !chosen.IsStatic && !name.StartsWith("get_")) {   // properties can be read
      throw Error($"'{parent.Name}' is a read-only reference: '{name}' needs an owned or writable reference (mark it unrestricted, or declare &{parent.Name})"
        + DefinedAt(new[] { chosen }, name), at);
    }
    CheckAccess(chosen.Visibility, chosen.Class!, name, at);
  }

  void CheckAccess(Vis visibility, ClassInfo owner, string what, Node at) {
    if (visibility != Vis.Private) {
      return;                                   // internal is as open as public until namespaces exist
    }
    if (owner.IsTrait) {
      // private in a trait (spec 11.2): the code of the trait and of its sub-traits, not the class that implements it
      if (currentClass != null && (currentClass == owner || currentClass.IsTrait && IsSubtype(currentClass.Name, owner.Name)) || LibraryCode) {
        return;
      }
      throw Error($"'{what}' is private in the trait {DisplayName(owner.Name)}: only the code of the trait sees it", at);
    }
    // a subclass sees everything of its parents (their source is available)
    if (currentClass != null && (currentClass == owner || IsSubclass(currentClass.Name, owner.Name))) {
      return;
    }
    throw Error($"'{what}' is private in {owner.Name}", at);
  }

  // readonly (spec 11.1.4): the class (and its subclasses, as for private) writes the field, everyone else only reads it
  void CheckReadOnlyWrite(FieldInfo field, Node at) {
    if (field.IsReadOnly && !(currentClass != null && (currentClass == field.Owner || IsSubtype(currentClass.Name, field.Owner.Name)))) {
      throw Error($"'{field.Name}' is readonly: only {field.Owner.Name} can change it", at);
    }
  }

  // the code of the collections of the compiler (VecPrelude) uses the private members of the collection traits
  bool LibraryCode => currentFun != null && currentFun.Node.Line >= LibraryLine;

  // a sub-trait reads the private properties of its parent traits but does not change them (spec 11.2)
  void CheckTraitFieldWrite(FieldInfo field, Node at) {
    if (currentClass is { IsTrait: true } && field.Owner.IsTrait && field.Owner != currentClass && field.Visibility == Vis.Private && !LibraryCode) {
      throw Error($"'{field.Name}' belongs to the parent trait {DisplayName(field.Owner.Name)}: a sub-trait reads it and changes it through a function of {DisplayName(field.Owner.Name)}", at);
    }
  }

  // ---------------------------------------------------------------- names, fields, members

  // a field of the current class used by its bare name
  Expr? BareField(string name, Scope scope, Node at, bool raw = false) {
    if (currentClass == null || FindField(currentClass, name, at) is not FieldInfo field) {
      return null;
    }
    if (field.IsCounted && !raw) {
      return CountedFieldRead(BareField(name, scope, at, true)!.Value, field, at);   // the object of the counting block (spec 12.7)
    }
    if (field.IsStatic && field.HasAccessors && !raw) {
      return PropertyGet(new Expr("", field.Owner.Name), field, scope, at);   // a static property by its bare name (spec 11.1.4, 11.1.2)
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

  Expr ObjectField(Expr obj, string name, Node at, bool raw = false) {
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
    var stored = new Expr($"({NullChecked(obj.Code, obj.Type)})->{field.CName}", field.Type);
    return field.IsCounted && !raw ? CountedFieldRead(stored, field, at) : stored;
  }

  Expr StaticField(ClassInfo cls, string name, Node at, Scope? scope = null) {
    var field = FindField(cls, name) ?? throw Error($"{cls.Name} has no member '{name}'", at);
    if (!field.IsStatic) {
      throw Error($"'{name}' is an instance field: it needs an object", at);
    }
    if (field.HasAccessors) {
      return PropertyGet(new Expr("", cls.Name), field, scope ?? new Scope(null), at);   // C.count through its R: (spec 11.1.4)
    }
    CheckAccess(field.Visibility, field.Owner, name, at);
    if (field.IsCounted) {
      return CountedFieldRead(new Expr(field.CName, field.Type), field, at);   // its object (spec 12.7)
    }
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
      CheckReadOnlyWrite(field, target);
      return new LValueInfo(field.CName, field.Type, field.IsConst && !initializing, null, false, field.IsWeak, true);   // a static: not tracked
    }
    var owner = ExprOf(target.Children[0], scope);
    if (!IsClass(owner.Type)) {
      return null;
    }
    if (ReadOnlyParent(owner.Code) is FieldInfo readOnly) {
      throw Error($"'{readOnly.Name}' is a read-only reference: its fields cannot be assigned", target);
    }
    if (ReadOnlyVariable(target.Children[0], scope) is not null) {
      throw Error($"'{target.Children[0].Text}' is a read-only reference: its fields cannot be assigned", target);
    }
    if (ReadOnlyCounted(target.Children[0], scope) is CountedRefInfo readOnlyCounted) {
      throw Error($"'{readOnlyCounted.Name}' is a read-only counted object (@{readOnlyCounted.Type}): its fields are written through a &@{readOnlyCounted.Type} (spec 12.7)", target);
    }
    var instance = ObjectField(owner, target.Text!, target, true);
    var info = FindField(ObjInfo(owner.Type), target.Text!)!;
    if (info.IsCounted && !countedFieldWrite && IsCountedValueType(info.Type)) {
      return CountedValueFieldPlace(instance.Code, info, target);   // k.hits += 1 on a &@i32 field: its cell (spec 12.7)
    }
    if (info.IsCounted && !countedFieldWrite) {
      throw NotSupported($"This use of the counted field '{info.Name}' (->, &, swap, out arguments)", target);
    }
    if (info.IsWeak && !countedFieldWrite) {
      throw Error($"'{info.Name}' is a weak reference: copy it into a weak variable and use that through enforce (spec 12.7)", target);
    }
    CheckTraitFieldWrite(info, target);
    CheckReadOnlyWrite(info, target);
    if (info.HasAccessors) {
      throw NotSupported($"this kind of write to the property '{info.Name}' (it would skip its W:)", target);
    }
    if (info.IsParentRef) {
      // the reference to the parent: set once, by the constructor
      return initializing ? new LValueInfo(instance.Code, instance.Type, false) : throw Error($"'{target.Text}' is the reference to the parent: only the constructor sets it", target);
    }
    return new LValueInfo(instance.Code, instance.Type, info.IsConst && !initializing, null, true, info.IsWeak);
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

  List<string> ArgCodes(FunSig f, List<Node> args, Scope scope, Node at, out string prefix) {
    var (slots, problem) = MapArgs(f, args);
    if (slots == null) {
      throw Error(problem!, at);
    }
    var codes = new List<string>();
    var given = new List<Node?>();
    for (int index = 0; index < f.Params.Count; index++) {
      var param = f.Params[index];
      Node arg = slots[index] ?? param.Default!;
      given.Add(arg);
      if (VariadicLend(param, arg, scope) is string lent) {
        codes.Add(lent);   // an arr, a vec or a view given alone to T name... (spec 13.4)
        continue;
      }
      codes.Add(ArgCode(param, arg, scope, f, index));
      CheckStoredFunction(f, param, arg, scope);   // after the value: a lambda says whether it borrows once it is compiled
    }
    return ArgsInOrder(given, codes, out prefix);
  }

  // &T x gets the address of a variable, or of a temporary holding the value; &T x gets the address of a variable, a field or an
  // array item, written &x in the call
  string ReferenceArg(ParamSig param, Node arg, Scope scope) {
    if (param.Ref == 2) {
      if (arg.Kind != "WritableAddressOf") {
        throw Error($"'{param.Name}' is written by the function: pass it as &variable", arg);
      }
      return WritableAddress(arg.Children[0], scope, param.Type, arg);
    }
    if (arg.Kind == "WritableAddressOf") {
      throw Error($"'{param.Name}' is a read-only reference: remove the &", arg);
    }
    var value = ExprOf(arg, scope, param.Type);
    string code = Convert(value, param.Type, arg);
    if (param.Type == "string") {
      code = StrRead(new Expr(code, "string", FreshCode(value, code) == "true" && !IsStaticStr(code)));
    }
    if (arg.Kind == "Name" && value.Type == param.Type && scope.Find(arg.Text!) is VarInfo variable) {
      ReadCheck(variable, arg.Text!, arg);
      return "&" + variable.CName;
    }
    return $"((const {CType(param.Type)}[1]){{ {code} }})";
  }

  // the C address of what &x names; the type must be the one of the parameter
  string WritableAddress(Node target, Scope scope, string type, Node at) {
    if (target.Kind is not ("Name" or "Member" or "Index")) {
      throw Error("& needs a variable, a field or an array item", at);
    }
    if (target.Kind == "Index") {
      var obj = ExprOf(target.Children[0], scope);
      if (!IsArr(obj.Type) || target.Children[0].Kind == "GenericRef") {
        throw Error("& needs a variable, a field or an array item", at);
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
    if (target is { Kind: "Name", Text: "this" }) {
      throw Error("&this is not allowed: the function could replace the object while its method runs; pass the fields that change", at);
    }
    CheckNotWritableBorrowOfCounted(target, scope, at);
    var lvalue = LValue(target, scope);
    if (lvalue.IsConst) {
      throw Error($"'{target.Text}' is const and cannot be passed as &", at);
    }
    if (IsClass(type)) {
      // the function can replace the object (spec 13.1): what it names must own its object, or be a & parameter that does it for its caller
      if (ReadOnlyVariable(target, scope) is not null) {
        throw Error($"'{target.Text}' is a read-only reference: it cannot be passed as &", at);
      }
      if (lvalue.Variable is VarInfo holder && !holder.Own && !holder.RefSlot) {
        throw Error($"'{target.Text}' only borrows its object: & needs the variable that owns it, a field, or a & parameter (spec 13.1)", at);
      }
    }
    if (lvalue.Type != type) {
      throw Error($"the parameter is a {type}, but '{target.Text}' is a {lvalue.Type}", at);
    }
    if (IsArr(type) && !LibraryCode && lvalue.Variable is VarInfo arrHolder && !arrHolder.OwnsArray && !arrHolder.RefSlot) {
      // the function can replace the array and end the old one (spec 13.1, 6.8.1): only its owner hands it out
      throw Error($"'{target.Text}' only borrows its array: & needs the variable that owns it, a field, or a & parameter (spec 13.1)", at);
    }
    MarkAssigned(lvalue.Variable);
    if (type == "string" && lvalue.Variable?.OwnFlag is string flag) {
      // the function can replace the string and end the old one (spec 13.1): a variable that borrows its string takes a copy of its own first
      return $"({{ if (!{flag}) {{ {lvalue.Code} = df_str_own({lvalue.Code}, false); {flag} = true; }} &{lvalue.Code}; }})";
    }
    return "&" + lvalue.Code;
  }

  // the marker of a counted or weak argument repeats the one of its parameter (spec 13.1): @x, &@x, %x, &%x. ->x gives the caller's usage;
  // a new object, a result and null need no marker. Returns the argument without the &
  Node CallMarker(Node arg, string marker, bool writable, string what, Scope scope) {
    string kind = marker == "@" ? "CountedOf" : "WeakOf";
    string wanted = (writable ? "&" : "") + marker;
    if (arg.Kind == "WritableAddressOf" && arg.Children[0].Kind is "CountedOf" or "WeakOf") {
      var inner = arg.Children[0];
      if (inner.Kind != kind) {
        throw Error($"{what} takes {wanted}: write {wanted}{Shown(inner.Children[0])} (spec 13.1)", arg);
      }
      if (!writable) {
        throw Error($"{what} is read-only: write {marker}{Shown(inner.Children[0])}, without & (spec 13.1)", arg);
      }
      return inner;
    }
    if (arg.Kind is "CountedOf" or "WeakOf") {
      if (arg.Kind != kind || writable) {
        throw Error($"{what} takes {wanted}: write {wanted}{Shown(arg.Children[0])} (spec 13.1)", arg);
      }
      return arg;
    }
    if (arg.Kind is "Name" or "Member" or "Index" && arg is not { Kind: "Name", Text: "this" }) {
      throw Error($"{what} takes {wanted}: write {wanted}{Shown(arg)} (spec 13.1)", arg);
    }
    return arg;   // null, ->x, a new object, a result
  }

  static string Shown(Node node) => node.Kind switch {
    "Name" => node.Text ?? "x",
    "Member" when node.Children.Count > 0 => Shown(node.Children[0]) + "." + node.Text,
    "Index" when node.Children.Count > 0 => Shown(node.Children[0]) + "[...]",
    _ => "x"
  };

  // one argument: an object given to a parameter written ->T moves (the call writes ->x); the others only borrow
  string ArgCode(ParamSig param, Node arg, Scope scope, FunSig? callee = null, int position = -1) {
    if (arg.Kind == "WritableAddressOf" && arg.Children.Count > 0 && arg.Children[0].Kind is not ("CountedOf" or "WeakOf")) {
      CheckUnrestrictedWrite(arg.Children[0], scope);   // f(&field) can change the object (spec 1.6.2)
    }
    if (param.Pointer != 0) {
      return StructPointerArg(param, arg, scope);   // f(*v), f(&*v) (spec 11.1.5.6)
    }
    if (BoxInner(param.Type) != null) {
      if (!param.IsGift) {
        return LookupBox(arg, param.Type, scope);   // pack.has(first): compared, not kept
      }
      arg = CallMarker(arg, "@", writableBoxes.Contains(param.Type), "the item", scope);   // pack.add(@first), team.add(&@p)
      arg = BoxCall(arg, param.Type);   // a new box takes a usage (spec 12.7)
    }
    bool boxed = arg.Kind == "Boxed";
    if (boxed) {
      arg = arg.Children[0];
    }
    if (IsCountedType(param.Type)) {
      if (!boxed) {
        arg = CallMarker(arg, "@", param.IsWritable, $"'{param.Name}'", scope);
      }
      return CountedArg(arg, CountedBase(param.Type), scope, param.IsWritable);   // @T name, &@T name (spec 12.7)
    }
    if (IsWeakType(param.Type)) {
      arg = CallMarker(arg, "%", param.IsWritable, $"'{param.Name}'", scope);
      return WeakValue(arg, scope, param.Type[1..], arg, param.IsWritable);   // %T name: one more weak reference for the call (spec 12.7)
    }
    if (IsClass(param.Type) && CountedRef(arg.Kind == "WritableAddressOf" ? arg.Children[0] : arg, scope) is CountedRefInfo counted) {
      throw arg.Kind == "WritableAddressOf"
        ? Error($"& is not allowed on the counted object '{counted.Name}': writing needs a read/write usage, &@T (spec 12.7)", arg)
        : Error($"'{counted.Name}' is counted and cannot be borrowed: the parameter '{param.Name}' takes a counted object only if declared @{counted.Type} {param.Name} (spec 12.7)", arg);
    }
    if (param.IsGift && arg.Kind == "Gift" && CountedRef(arg.Children[0], scope) is CountedRefInfo given) {
      throw PlainGiftOfCounted(given, arg.Children[0], arg);   // a ->T parameter receives a plain owned object (spec 12.7)
    }
    if (IsFunType(param.Type)) {
      return FnArg(param, arg, scope);   // spec 13.3
    }
    if (param.Type == "string?") {
      if (IsByPointer(param) || param.IsGift) {
        throw NotSupported("&string? and ->string? parameters (a string? parameter borrows, spec 7.1)", arg);
      }
      if (arg.Kind is "Gift" or "WritableAddressOf") {
        throw Error($"'{param.Name}' borrows the string?: remove the {(arg.Kind == "Gift" ? "->" : "&")}", arg);
      }
      return OptStrBorrowed(ExprOf(arg, scope, param.Type), arg);
    }
    if (IsByPointer(param)) {
      return ReferenceArg(param, arg, scope);
    }
    if (param.Ref == 2 && IsClass(param.Type)) {
      // &Parent name as parent: the object pointer; the call still writes &o
      if (arg.Kind != "WritableAddressOf") {
        throw Error($"'{param.Name}' is a writable borrow: pass it as &object", arg);
      }
      arg = arg.Children[0];
      CheckNotWritableBorrowOfCounted(arg, scope, arg);
      if (ReadOnlyVariable(arg, scope) is not null) {
        throw Error($"'{arg.Text}' is a read-only reference: it cannot be passed as &", arg);
      }
    } else if (arg.Kind == "WritableAddressOf") {
      throw Error($"'{param.Name}' is not a & parameter: remove the &", arg);
    }
    if (IsArr(param.Type) && !LibraryCode) {
      // spec 6.8.1: ->arr<T> p takes the array (->a, a new array, a!, or a silent copy of plain items), arr<T> p borrows it
      if (param.IsGift && !GivesOwnArray(arg)) {
        if (!HasPlainItems(param.Type)) {
          throw Error($"'{param.Name}' takes ownership of the array: write ->{arg.Text ?? "a"} to move it, or {arg.Text ?? "a"}! to give a copy", arg);
        }
        var copied = ExprOf(arg, scope, param.Type);
        return $"{ArrCloneHelper(param.Type, arg)}({Convert(copied, param.Type, arg)})";
      }
      if (!param.IsGift && arg.Kind == "Gift") {
        throw Error($"'{param.Name}' only borrows the array: remove the ->", arg);
      }
    }
    if (StructHoldsObjects(param.Type) && !LibraryCode) {
      return StructArg(param, arg, scope);   // spec 11.1.5.3
    }
    if (param.IsImplicitGift && arg.Kind is "Name" or "Member" or "Index" && arg is not { Kind: "Name", Text: "this" }) {
      // Ken(rex) for a plain primary parameter used as a field: rex is given as with ->rex, and cannot be used afterwards (spec 11.1.1)
      if (arg.Kind == "Name" && scope.Find(arg.Text!) is VarInfo givenVar) {
        pendingMoves.Add((givenVar, param.GivenTo!));
      }
      arg = Synthetic("Gift", arg).Add(arg);
    }
    // a weak parameter takes any existing object, also another weak reference (spec 12)
    var value = ExprOf(arg, scope, param.Type);
    if (param.Type == "string") {
      string converted = Convert(value, param.Type, arg);
      return param.IsGift ? StrOwnedAs(value, "string", converted) : StrRead(new Expr(converted, "string", FreshCode(value, converted) == "true" && !IsStaticStr(converted)));
    }
    if (IsClass(param.Type)) {
      if (param.IsGift && !value.Fresh && value.Type != "null" && LibraryCode) {
        cloneUsed = true;
        return CloneValue(Convert(value, param.Type, arg), param.Type, arg);   // the copying paths of the library (s.add(other)): a clone goes in
      }
      if (param.IsGift && !value.Fresh && value.Type != "null") {
        throw Error($"'{param.Name}' takes ownership of the object: write ->x", arg);
      }
      if (!param.IsGift && arg.Kind == "Gift") {
        throw Error($"'{param.Name}' only borrows the object: remove the ->", arg);
      }
      if (!param.IsGift) {
        value = TempObject(value);   // f(Dog("a")): the new object is borrowed by the call and ends with the statement
      }
    }
    if (!param.IsGift) {
      value = TempTuple(value, arg);   // g(f()): a borrowed tuple copies what it keeps, so its arrays end with the statement (spec 6.8.1)
    }
    if (TempArrayArg(param, arg, callee, position)) {
      // f([Dog("a")]): a new array lent to the call ends with the statement, as a new object does (spec 6.8.1, 12)
      return TempBoxCode(param.Type, Convert(value, param.Type, arg), $"{ArrDropHelper(param.Type)}(*t);");
    }
    return Convert(value, param.Type, arg);
  }

  // cls.name(args): a static function (receiver null) or a function of an object
  // direct: no virtual dispatch (parent.name())
  Expr MethodCall(ClassInfo cls, string name, string? receiver, bool direct, List<Node> args, Scope scope, Node at) {
    CheckCountedItemsMember(cls, name, at);
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
    if (receiver != null) {
      CheckUnrestrictedCall(chosen, name, receiver, at);   // inside an unrestricted method (spec 1.6.2)
    }
    CheckParentAccess(chosen, receiver, name, at);
    var codes = ArgCodes(chosen, args, scope, at, out string prefix);
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
        return ArgsFirst(new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned), prefix);
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
    var result = ArgsFirst(new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned), prefix);
    return BoxInner(chosen.Ret) != null && result.Fresh ? UnboxTaken(result) : UnboxTupleResult(result);   // pack.replace(i, x) gives the usage of the old item
  }

  // x.name(args) on an object, or name(args) inside a method
  Expr? ObjectCall(Expr receiver, string name, List<Node> args, Scope scope, Node at) {
    if (!IsClass(receiver.Type)) {
      return null;
    }
    if (!LibraryCode && IsMap(receiver.Type) && IsFunType(MapValue(receiver.Type)) && name is "has" or "remove" or "removeAll" or "keyOf") {
      throw Error($"function values are not compared: {name} needs values that have == (spec 13.3.2)", at);
    }
    return MethodCall(ObjInfo(receiver.Type), name, TempObject(receiver).Code, false, args, scope, at);   // make().bark()
  }

  // name(args) written inside a class: a function of this class (or a parent), before free functions
  Expr? BareMethodCall(string name, List<Node> args, Scope scope, Node at) {
    if (currentStruct != null && funs.TryGetValue(currentStruct.Name + "." + name, out var structFuns)) {
      // len() inside a method of a struct: this.len(), or a static function of the struct (spec 11.1.5.2)
      var chosen = ResolveOverload(structFuns, args, scope, at);
      if (chosen.IsStatic) {
        return StructStaticCall(currentStruct, name, args, scope, at);
      }
      if (scope.Find("this") == null) {
        throw Error($"'{name}' is a method: a static function has no struct to call it on", at);
      }
      return StructMethodCall(Synthetic("Member", at, name).Add(Synthetic("Name", at, "this")), args, scope, at);
    }
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
    // a trait method with executeBefore / executeAfter: the parent's implementation, without running the blocks again (spec 11.2)
    if (AllTraits(currentClass.Parent).FirstOrDefault(trait => trait.Hooked.Contains(name)) is ClassInfo hooked) {
      name = ImplPrefix + name;
      if (LookupMethods(currentClass.Parent, name).All(f => f.Class!.IsTrait)) {
        return TraitDirectCall(hooked, name, args, scope, at);   // the parent uses the default body of the trait
      }
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
    if (IsSetValue(peek.Type)) {
      return new Expr($"(({peek.Code}).tag == {SetMemberTag(peek.Type, n.Children[1], n)}u)", "bool");   // k is i32 (spec 11.3.6.1)
    }
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
    return (subject, IsTarget(n, subject.Type));
  }

  // the class or trait named after 'is', for a subject of this type
  ClassInfo IsTarget(Node n, string subjectType) {
    var subject = new Expr("", subjectType);
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
    return target;
  }

  // if x is Circle c { }: c is the object as a Circle inside the block. When x is counted (@T, &@T, or a weak reference inside its
  // enforce) c keeps its marker: Usage is the declaration of c as a counted variable with a usage of its own, put first in the block, so
  // it is released on every way out of it (spec 12.7)
  (string Condition, Scope BlockScope, Node? Usage) IsBinding(Node n, Scope scope) {
    if (!LibraryCode && CountedRef(n.Children[0], scope) is CountedRefInfo counted) {
      return CountedIsBinding(n, counted, scope);
    }
    if (!LibraryCode && WeakRef(n.Children[0], scope) is CountedRefInfo weak) {
      return WeakIsBinding(n, weak, scope);
    }
    if (ExprOf(n.Children[0], scope) is { } setSubject && IsSetValue(setSubject.Type)) {
      var (setCondition, setScope) = SetIsBinding(n, setSubject, scope);   // if k is i32 i { } (spec 11.3.6.1)
      return (setCondition, setScope, null);
    }
    var (condition, inner) = PlainIsBinding(n, scope);
    return (condition, inner, null);
  }

  (string Condition, Scope BlockScope, Node? Usage) CountedIsBinding(Node n, CountedRefInfo counted, Scope scope) {
    var (subject, target) = IsParts(n, scope);
    CheckNotShadowing(n.Text!, scope, n);
    string block = NewTemp();
    fn!.Decls.Append($"  df_rc* {block} = NULL;\n");
    string held = NewTemp();
    string obj = NewTemp();
    string condition = $"({{ df_rc* {held} = {counted.Code}; {CType(subject.Type)} {obj} = DF_RC_OBJ({CType(subject.Type)}, {held}); "
      + $"bool ok_{obj} = {IsTest(obj, target, subject.Type, n)}; {block} = ok_{obj} ? {held} : NULL; ok_{obj}; }})";
    var use = Synthetic("Decl", n, n.Text).Add(Synthetic("Type", n, target.Name)).Add(Synthetic("Counted", n))
      .Add(Synthetic("Init", n).Add(Synthetic("CValue", n, $"df_rc_use({block})").Add(Synthetic("Type", n, "@" + target.Name))));
    if (IsWritableRef(counted)) {
      use.Add(Synthetic("ReadWrite", n));   // from a &@T: a read/write usage (spec 12.7)
    }
    return (condition, scope, use);
  }

  // if w is Dog d { } with a weak w: d is a new weak reference, %Dog (or &%Dog from a &%T); the check needs the object alive (spec 12.7)
  (string Condition, Scope BlockScope, Node? Usage) WeakIsBinding(Node n, CountedRefInfo weak, Scope scope) {
    var target = IsTarget(n, weak.Type);
    CheckNotShadowing(n.Text!, scope, n);
    string block = NewTemp();
    fn!.Decls.Append($"  df_rc* {block} = NULL;\n");
    string held = NewTemp();
    string obj = NewTemp();
    string condition = $"({{ df_rc* {held} = {weak.Code}; {CType(weak.Type)} {obj} = DF_RC_ALIVE({held}) ? DF_RC_OBJ({CType(weak.Type)}, {held}) : NULL; "
      + $"bool ok_{obj} = {IsTest(obj, target, weak.Type, n)}; {block} = ok_{obj} ? {held} : NULL; ok_{obj}; }})";
    var use = Synthetic("Decl", n, n.Text).Add(Synthetic("Type", n, target.Name)).Add(Synthetic("Weak", n))
      .Add(Synthetic("WeakInit", n).Add(Synthetic("CValue", n, $"df_rc_weak({block})").Add(Synthetic("Type", n, target.Name))));
    if (IsWritableRef(weak)) {
      use.Add(Synthetic("ReadWrite", n));   // from a &%T: &%T (spec 12.7)
    }
    return (condition, scope, use);
  }

  // the block of an if with a counted binding: the declaration of the binding first, then the statements
  Node WithUsage(Node body, Node? usage) {
    if (usage == null) {
      return body;
    }
    var wrapped = Synthetic("Block", body).Add(usage);
    if (body.Kind == "Block") {
      wrapped.Children.AddRange(body.Children);
    } else {
      wrapped.Add(body);
    }
    return wrapped;
  }

  (string Condition, Scope BlockScope) PlainIsBinding(Node n, Scope scope) {
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
      // the tree of a map is a C struct: a map prints through MapStrFunction
      var all = AllInstanceFields(cls).Where(field => !field.Hidden && !((IsMap(cls.Name) || IsSet(cls.Name)) && field.Name == "tree")).ToList();
      var sb = classCode;
      sb.Append($"static df_string df_str_{cls.Name}(const {cls.StructName}* o) {{\n");
      sb.Append("  if (o == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      if (boxInner.ContainsKey(cls.Name)) {
        // the box of an item of list<@T> prints as its object (spec 12.7)
        all.Clear();
        string boxed = boxInner[cls.Name];
        sb.Append(IsCountedValueType(boxed)
          ? $"  return {StrOf(new Expr(CellRead($"o->{FindField(cls, "value")!.CName}", boxed), boxed), cls.Node)};\n"   // a counted value prints its value
          : $"  return df_str_obj(DF_RC_OBJ(const void*, o->{FindField(cls, "value")!.CName}));\n");
      }
      sb.Append($"  df_string r ={CString(CodePoints(DisplayName(cls.Name) + (all.Count == 0 ? " {}" : " { ")))};\n");
      for (int index = 0; index < all.Count && index < 3; index++) {
        var field = all[index];
        string label = (index > 0 ? ", " : "") + field.Name + ": ";
        string value;
        if (IsClass(field.Type)) {
          string isNull = field.IsWeak || field.IsCounted ? $"!DF_RC_ALIVE(o->{field.CName})" : $"o->{field.CName} == NULL";   // a weak reference to a gone object prints null
          value = $"({isNull} ? df_str_lit(\"null\", 4) : {CString(CodePoints(DisplayName(field.Type) + "{}"))})";
        } else if (field.IsCounted && IsCountedValueType(field.Type)) {
          value = StrOf(new Expr(CellRead($"o->{field.CName}", field.Type), field.Type), cls.Node);   // a counted value prints its value (spec 12.7)
        } else if (IsFunType(field.Type)) {
          // a function value has no text: null, or its type (spec 13.3.2)
          value = $"(o->{field.CName}.code == NULL ? df_str_lit(\"null\", 4) : {CString(CodePoints(FunDisplay(field.Type)))})";
        } else {
          value = StrOf(new Expr($"o->{field.CName}", field.Type), cls.Node);
        }
        sb.Append($"  r = df_str_cat(r, {CString(CodePoints(label))});\n");
        sb.Append($"  r = {(field.Type == "string" ? "df_str_cat" : "df_str_catx")}(r, {value});\n");
      }
      if (all.Count > 3) {
        sb.Append($"  r = df_str_cat(r, {CString(CodePoints(", ..."))});\n");
      }
      if (all.Count > 0) {
        sb.Append($"  r = df_str_cat(r, {CString(CodePoints(" }"))});\n");
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
      if (hashUsed) {
        EmitHashFunction(cls, sb);
      }
      string hash = hashUsed ? $"df_hash_{cls.Name}" : "NULL";
      string clone = EmitCloneFunction(cls, sb);
      sb.Append($"static const df_class {cls.Descriptor} = {{ {256 + cls.Index}, \"{DisplayName(cls.Name)}\", {parent}, {slots}, df_strp_{cls.Name}, {impls}, df_dropall_{cls.Name}, {hash}, {clone} }};\n\n");
    }
  }

  // the C function that allocates an object and runs the init functions (parents first)
  string EmitConstructor(FunSig ctor) {
    var cls = ctor.Class!;
    var init = ctor.CtorInit ?? cls.Init!;
    var args = ctor.Params.Select(ParamName);
    var callArgs = string.Join(", ", new[] { "self" }.Concat(args));
    var sb = new StringBuilder();
    sb.Append(Signature(ctor)).Append(" {\n");
    sb.Append($"  {CType(cls.Name)} self = ({CType(cls.Name)})df_calloc(1, sizeof(struct {cls.StructName}));\n");
    sb.Append($"  self->cls = &{cls.Descriptor};\n");
    if (cls.IsException) {
      sb.Append($"  self->descriptor = &df_desc_{cls.Name};\n  self->id = df_desc_{cls.Name}.id;\n");
    }
    // a constructor returns the object with its exception when the init functions can let one out
    string initCall = $"{init.CName}({callArgs})";
    if (PairReturn(init)) {
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
  string InitPrologue(FunSig f) {
    var cls = f.Class!;
    var sb = new StringBuilder();
    if (f.ChainsToSelf) {
      return "";   // the other constructor of the class did all of it
    }
    if (cls.IsException && cls.Parent == null) {
      sb.Append("  df_exc_live++;\n");
      sb.Append("  self->message = df_cstr_truncated(df_view(v_message).p, df_str_len(v_message), 2048);\n");
      sb.Append("  self->function = df_cstr_truncated(\"\", 0, 1024);\n");
    }
    if (f.ParentInit != null) {
      string forwarded = cls.Parent!.IsException ? ", v_message" : "";
      string parentInit = $"{f.ParentInit.CName}(({CType(cls.Parent.Name)})self{forwarded})";
      if (PairReturn(f.ParentInit)) {
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
