using System.Text;
using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// A type parameter of a generic function or class: <Item : Numeric && Named>
sealed class TypeParam {
  public TypeParam(string name, Node at) {
    Name = name;
    At = at;
  }

  public string Name { get; }
  public Node At { get; }
  public List<Node> Constraints { get; } = new();   // type nodes: a trait, a class or a set; all must hold
  public string? ValueType { get; set; }            // a value parameter (11.3.3): its type; null for a type parameter
  public List<(string Placeholder, Node Constraint)> PlaceholderConstraints { get; } = new();   // where Source.Item : Numeric (11.3.4)
}

// a generic function: the template is kept as a tree and a copy is made for each list of type arguments
sealed class GenericFun {
  public GenericFun(string name, Node node, List<TypeParam> ps) {
    Name = name;
    Node = node;
    Params = ps;
  }

  public string Name { get; }
  public Node Node { get; }
  public List<TypeParam> Params { get; }
  public int Copies { get; set; }
}

// a generic class or a generic trait (Node.Kind says which): the copies of both are made the same way
sealed class GenericClass {
  public GenericClass(string name, Node node, List<TypeParam> ps) {
    Name = name;
    Node = node;
    Params = ps;
  }

  public string Name { get; }
  public Node Node { get; }
  public List<TypeParam> Params { get; }
  public bool IsTrait => Node.Kind == "Trait";
  public bool HasPlaceholders { get; init; }   // a trait with 'placeholder Item': the Params are the placeholders, fixed by 'with (Item is u32)' in a class
}

// the instances of generic classes the program needs; they survive from one attempt to the next (see Compile)
sealed class GenericSeeds {
  public List<(GenericClassKey Template, List<string> Args)> Classes { get; } = new();
}

readonly record struct GenericClassKey(string Name);

// thrown when a generic class instance is needed that this attempt has not created: the compiler starts again with it
sealed class NeedInstanceException : Exception {
}

// Generics (spec 11.3): code generated per type arguments (the Rust model). A generic function or class is a template;
// a use with concrete type arguments makes a copy of the tree with the parameters replaced, and the copy is compiled
// like any other function or class. Sets (Generic Name { types }) are compile-time lists used as constraints.
public sealed partial class CodeGen {
  public int MaxChainDepth { get; set; } = 16;
  public int MaxCopies { get; set; } = 16;

  readonly GenericSeeds seeds;
  readonly Dictionary<string, GenericFun> genericFuns = new();
  readonly Dictionary<string, GenericClass> genericClasses = new();
  readonly Dictionary<string, Node> genericSets = new();
  readonly Dictionary<string, (GenericClass Template, List<string> Args)> classInstanceArgs = new();
  readonly Dictionary<string, string> displayNames = new();
  readonly HashSet<GenericFun> busyTemplates = new();

  // the predefined sets (spec 11.3.6); the members this compiler does not have yet (i128, r16, dec, int ...) are left out
  static readonly Dictionary<string, string[]> PredefinedSets = new() {
    ["Signed"] = new[] { "i8", "i16", "i32", "i64" },
    ["Unsigned"] = new[] { "u8", "u16", "u32", "u64" },
    ["Integers"] = new[] { "Signed", "Unsigned" },
    ["Reals"] = new[] { "r32", "r64" },
    ["Numeric"] = new[] { "Integers", "Reals" },
    ["Strings"] = new[] { "char", "string" }
  };

  public CodeGen() : this(new GenericSeeds()) {
  }

  internal CodeGen(GenericSeeds seeds) {
    this.seeds = seeds;
  }

  // Compiles the tree. A generic class instance that the code needs and the attempt has not made (found while resolving
  // a type) is added to the seeds and the whole compilation starts again from a fresh copy of the tree.
  public static (string C, IReadOnlyList<string> Warnings) Compile(Node tree, bool release = false, int nativeBits = 64) {
    ReleaseBuild = release;
    NativeUns = nativeBits >= 64 ? "u64" : "u32";
    var seeds = new GenericSeeds();
    for (int attempt = 0; ; attempt++) {
      var gen = new CodeGen(seeds);
      try {
        string c = gen.Generate(CloneNode(tree));
        return (c, gen.Warnings);
      } catch (NeedInstanceException) {
        if (attempt > 4096) {
          throw new CompileException("too many generic class instances", 0);
        }
      }
    }
  }

  static Node CloneNode(Node node, Func<Node, Node?>? replace = null) {
    var replacement = replace?.Invoke(node);
    if (replacement != null) {
      return replacement;
    }
    var copy = new Node(node.Kind, new Token(TokenKind.Ident, "", node.Line, 0), node.Text);
    foreach (var child in node.Children) {
      copy.Children.Add(CloneNode(child, replace));
    }
    return copy;
  }

  static string MangledName(string name, IEnumerable<string> args) {
    return name + "__" + string.Join("_", args.Select(SanitizeName));
  }

  string DisplayName(string type) => displayNames.TryGetValue(type, out var shown) ? shown : type.StartsWith('#') ? type[1..] : type;

  bool IsSetName(string name) => PredefinedSets.ContainsKey(name) || genericSets.ContainsKey(name);

  // ---------------------------------------------------------------- registering templates and sets

  bool IsGenericFunNode(Node fun) {
    if (fun.Children.Any(child => child.Kind == "GenericParams")) {
      return true;
    }
    var ps = fun.Children.FirstOrDefault(child => child.Kind == "Params");
    return ps != null && ps.Children.Any(p => p.Children.Count > 0 && p.Children[0] is { Kind: "Type", Children.Count: 0 } type && IsSetName(type.Text!));
  }

  // Generic sets and generic classes are registered before the classes are declared
  void RegisterGenerics(Node program) {
    foreach (var statement in program.Children.Where(child => child.Kind == "GenericSet")) {
      string name = statement.Text!;
      if (!char.IsUpper(name[0])) {
        throw Error($"the name of a set starts with an uppercase letter: '{name}'", statement);
      }
      if (PredefinedSets.ContainsKey(name)) {
        throw Error($"'{name}' is a predefined set", statement);
      }
      if (!genericSets.TryAdd(name, statement)) {
        throw Error($"the set '{name}' is already declared", statement);
      }
    }
    foreach (var plain in program.Children.Where(child => child.Kind == "Class" && child.Children.Any(c => c.Kind == "Where") && !child.Children.Any(c => c.Kind == "GenericParams"))) {
      throw Error($"'where' is for generic classes: '{plain.Text}' has no type parameters", plain);
    }
    foreach (var node in program.Children.Where(child => child.Kind is "Class" or "Trait" && (child.Children.Any(c => c.Kind == "GenericParams") || PlaceholdersOf(child).Count > 0)).ToList()) {
      string name = node.Text!;
      if (genericClasses.ContainsKey(name) || IsSetName(name)) {
        throw Error($"the name '{name}' is already used", node);
      }
      var placeholders = PlaceholdersOf(node);
      if (placeholders.Count > 0) {
        // a trait with placeholders: copied for each class that fixes them (the placeholders play the part of type parameters)
        if (node.Children.Any(c => c.Kind is "GenericParams" or "Where")) {
          throw Error("feature not supported yet (a trait with both type parameters and placeholders)", node);
        }
        var placeholderParams = new List<TypeParam>();
        foreach (var placeholder in placeholders) {
          if (placeholderParams.Any(existing => existing.Name == placeholder.Text)) {
            throw Error($"duplicate placeholder '{placeholder.Text}'", placeholder);
          }
          placeholderParams.Add(new TypeParam(placeholder.Text!, placeholder));
        }
        genericClasses[name] = new GenericClass(name, node, placeholderParams) { HasPlaceholders = true };
        program.Children.Remove(node);
        continue;
      }
      genericClasses[name] = new GenericClass(name, node, ReadTypeParams(node, name));
      program.Children.Remove(node);
    }
    // the copies of generic classes the earlier attempts found out the program needs
    foreach (var (template, args) in seeds.Classes) {
      var generic = genericClasses[template.Name];
      string mangled = MangledName(generic.Name, args);
      var map = generic.Params.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second);
      var copy = CloneNode(generic.Node, SubstituteTypes(map, generic.HasPlaceholders));
      copy.Text = mangled;
      copy.Children.RemoveAll(child => child.Kind is "GenericParams" or "Where");
      if (generic.HasPlaceholders) {
        copy.Children.First(child => child.Kind == "Block").Children.RemoveAll(member => member.Kind == "Placeholder");
      }
      program.Children.Add(copy);
      classInstanceArgs[mangled] = (generic, args);
      displayNames[mangled] = $"{generic.Name}<{string.Join(", ", args.Select(DisplayName))}>";
    }
  }

  // the 'placeholder Item' members of a trait
  static List<Node> PlaceholdersOf(Node node) {
    var block = node.Kind == "Trait" ? node.Children.FirstOrDefault(child => child.Kind == "Block") : null;
    return block == null ? new List<Node>() : block.Children.Where(member => member.Kind == "Placeholder").ToList();
  }

  static readonly HashSet<string> ValueParamTypes = new() { "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "bool" };

  // <Item : Numeric, Key> and the entries of 'where' that are on a plain parameter
  List<TypeParam> ReadTypeParams(Node node, string name) {
    var ps = new List<TypeParam>();
    var generics = node.Children.First(child => child.Kind == "GenericParams");
    foreach (var param in generics.Children) {
      if (ps.Any(existing => existing.Name == param.Text)) {
        throw Error($"duplicate type parameter '{param.Text}'", param);
      }
      var typeParam = new TypeParam(param.Text!, param);
      if (param.Kind == "ValueParam") {
        // <u64 Width>: a compile-time constant of an integer type or bool (the other types of 11.3.3 are not built)
        string valueType = param.Children[0] is { Kind: "Type", Children.Count: 0 } declared ? Normalize(declared.Text!) : "";
        if (!ValueParamTypes.Contains(valueType)) {
          throw valueType is "char" or "r16" or "r32" or "r64" or "r128" or "dec"
            ? NotSupported($"Value parameters of type {valueType} (11.3.3)", param)
            : Error($"a value parameter is an integer or a bool, not '{param.Children[0].Text}'", param);
        }
        typeParam.ValueType = valueType;
        ps.Add(typeParam);
        continue;
      }
      typeParam.Constraints.AddRange(param.Children);
      ps.Add(typeParam);
    }
    var where = node.Children.FirstOrDefault(child => child.Kind == "Where");
    if (where != null) {
      foreach (var entry in where.Children) {
        var target = entry.Children[0];
        // where Source.Item : Numeric: a constraint on the placeholder of the trait that Source must implement
        if (target is { Kind: "Type", Children.Count: 0 } && target.Text!.Contains('.')) {
          string[] parts = target.Text.Split('.', 2);
          var ownerParam = ps.FirstOrDefault(p => p.Name == parts[0]) ?? throw Error($"'{parts[0]}' is not a type parameter of {name}", target);
          foreach (var constraint in entry.Children.Skip(1)) {
            ownerParam.PlaceholderConstraints.Add((parts[1], constraint));
          }
          continue;
        }
        var owner = target is { Kind: "Type", Children.Count: 0 } ? ps.FirstOrDefault(p => p.Name == target.Text) : null;
        if (owner == null) {
          throw NotSupported("'where' on a placeholder or a type expression (11.3.4)", entry);
        }
        foreach (var constraint in entry.Children.Skip(1)) {
          if (owner.Constraints.Any(existing => ConstraintText(existing) == ConstraintText(constraint))) {
            Warn($"the constraint {ConstraintText(constraint)} on '{owner.Name}' is written twice", constraint, 1);
          } else if (owner.Constraints.Count > 0 && HasInline(generics, owner.Name)) {
            throw Error($"'{owner.Name}' already has constraints inline: join the new one with && there", constraint);
          } else {
            owner.Constraints.Add(constraint);
          }
        }
      }
    }
    return ps;
  }

  static bool HasInline(Node generics, string name) {
    return generics.Children.Any(param => param.Text == name && param.Children.Count > 0);
  }

  // Container<i32> as written in a message
  string ConstraintShown(Node constraint) {
    return constraint.Children.Count == 0 ? constraint.Text! : $"{constraint.Text}<{string.Join(", ", constraint.Children.Select(arg => DisplayName(ResolveType(arg))))}>";
  }

  static string ConstraintText(Node constraint) => constraint.Text ?? constraint.Kind;

  // A function with a set as the type of a parameter is generic too: each such parameter gets a type parameter of its own
  void RegisterGenericFun(Node fun) {
    string name = fun.Text!;
    if (genericFuns.ContainsKey(name) || funs.ContainsKey(name)) {
      throw Error($"'{name}' is already defined: a generic function cannot share its name with another function", fun);
    }
    if (classes.ContainsKey(name) || genericClasses.ContainsKey(name)) {
      throw Error($"a function cannot be named like the class '{name}'", fun);
    }
    var ps = fun.Children.Any(child => child.Kind == "GenericParams") ? ReadTypeParams(fun, name) : new List<TypeParam>();
    var paramsNode = fun.Children.First(child => child.Kind == "Params");
    var implicitNames = new Dictionary<Node, string>();
    foreach (var param in paramsNode.Children) {
      if (param.Children.Count > 0 && param.Children[0] is { Kind: "Type", Children.Count: 0 } type && IsSetName(type.Text!)) {
        string implicitName = "_" + param.Text;
        var typeParam = new TypeParam(implicitName, param);
        typeParam.Constraints.Add(type);
        ps.Add(typeParam);
        implicitNames[type] = implicitName;
      }
    }
    var template = CloneNode(fun, node => implicitNames.TryGetValue(node, out var implicitName)
      ? new Node("Type", new Token(TokenKind.Ident, "", node.Line, 0), implicitName)
      : null);
    template.Children.RemoveAll(child => child.Kind == "Where");
    genericFuns[name] = new GenericFun(name, template, ps);
  }

  // the tree of a copy: every use of a type parameter becomes the concrete type
  Func<Node, Node?> SubstituteTypes(Dictionary<string, string> map, bool allowNullable = false) {
    return node => {
      if (!allowNullable && node.Kind == "Nullable" && node.Children[0] is { Kind: "Type", Children.Count: 0 } inner && map.ContainsKey(inner.Text!)) {
        throw Error($"'{inner.Text}?' is not allowed in generic code: list the nullable types in a set instead (11.3.8)", node);
      }
      var at = new Token(TokenKind.Ident, "", node.Line, 0);
      // Item? in a trait with placeholders: a type that is nullable already (string, a class) stays as it is
      if (allowNullable && node.Kind == "Nullable" && node.Children[0] is { Kind: "Type", Children.Count: 0 } placeholder
          && map.TryGetValue(placeholder.Text!, out var fixedType) && !fixedType.StartsWith('#') && NullByDefault(fixedType)) {
        return new Node("ResolvedType", at, fixedType);
      }
      if (node.Kind == "Type" && node.Children.Count == 0 && node.Text != null && map.TryGetValue(node.Text, out var concrete)) {
        // a value parameter written as a type argument: Grid<Width, Cell>
        return concrete.StartsWith('#') ? new Node("ValueArg", at).Add(ValueNode(concrete, node)) : new Node("ResolvedType", at, concrete);
      }
      // a value parameter used as a value: arr<Cell>[Width * Height]
      if (node.Kind == "Name" && node.Text != null && map.TryGetValue(node.Text, out var constant) && constant.StartsWith('#')) {
        return ValueNode(constant, node);
      }
      return null;
    };
  }

  // the literal of a value argument "#12", "#-3" or "#true"
  static Node ValueNode(string value, Node at) {
    var token = new Token(TokenKind.Ident, "", at.Line, 0);
    string text = value[1..];
    if (text is "true" or "false") {
      return new Node("Bool", token, text);
    }
    return text.StartsWith('-') ? new Node("Neg", token).Add(new Node("Int", token, text[1..])) : new Node("Int", token, text);
  }

  // ---------------------------------------------------------------- value parameters (11.3.3)

  // the value of a value argument written in a type: a number, true/false, or + * << of those, as "#value"
  string ValueArgOf(Node node, TypeParam param) {
    var token = new Token(TokenKind.Ident, "", node.Line, 0);
    var written = node.Kind switch {
      "Count" => new Node("Int", token, node.Text),
      "ValueArg" => node.Children[0],
      "Type" when node is { Children.Count: 0, Text: "true" or "false" } => new Node("Bool", token, node.Text),
      _ => throw Error($"'{param.Name}' is a value parameter: write a constant, as in 4 or Width * 2", node)
    };
    Int128 value = EvalConstant(written);
    if (param.ValueType == "bool") {
      return "#" + (value != 0 ? "true" : "false");
    }
    var (min, max) = IntegerRange(param.ValueType!);
    if (value < min || value > max) {
      throw Error($"{value} does not fit in {param.ValueType}, the type of the value parameter '{param.Name}'", node);
    }
    return "#" + value;
  }

  static (Int128 Min, Int128 Max) IntegerRange(string type) => type switch {
    "i8" => (sbyte.MinValue, sbyte.MaxValue),
    "i16" => (short.MinValue, short.MaxValue),
    "i32" => (int.MinValue, int.MaxValue),
    "i64" => (long.MinValue, long.MaxValue),
    "u8" => (0, byte.MaxValue),
    "u16" => (0, ushort.MaxValue),
    "u32" => (0, uint.MaxValue),
    _ => (0, ulong.MaxValue)
  };

  // + * << on unsigned values saturate at the largest u64 (11.3.3); a literal -n is allowed for the signed types
  Int128 EvalConstant(Node node) {
    switch (node.Kind) {
      case "Int": {
        string digits = node.Text!.Replace("_", "");
        bool hex = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? digits[2..] : digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
              null, out var number)) {
          throw Error($"'{node.Text}' is not a valid value for a value parameter", node);
        }
        return number;
      }
      case "Bool":
        return node.Text == "true" ? 1 : 0;
      case "Paren":
        return EvalConstant(node.Children[0]);
      case "Neg" when node.Children[0].Kind == "Int":
        return -EvalConstant(node.Children[0]);
      case "Binary" when node.Text is "+" or "*" or "<<": {
        Int128 left = EvalConstant(node.Children[0]);
        Int128 right = EvalConstant(node.Children[1]);
        Int128 a = left < 0 ? 0 : left;
        Int128 b = right < 0 ? 0 : right;
        Int128 result = node.Text switch {
          "+" => a + b,
          "*" => a * b,
          _ => b >= 64 ? (a == 0 ? 0 : ulong.MaxValue) : a << (int)b
        };
        return result > ulong.MaxValue ? ulong.MaxValue : result;
      }
      default:
        throw Error("a value argument is a constant: numbers, true, false and + * << of them", node);
    }
  }

  // ---------------------------------------------------------------- sets and constraints

  // is this type a member of the set? A class member means the class and its subclasses, a trait member every implementer
  bool InSet(string type, string setName, HashSet<string>? visited = null) {
    visited ??= new HashSet<string>();
    if (!visited.Add(setName)) {
      return false;
    }
    if (PredefinedSets.TryGetValue(setName, out var members)) {
      foreach (string member in members) {
        if (PredefinedSets.ContainsKey(member) ? InSet(type, member, visited) : type == member) {
          return true;
        }
      }
      return false;
    }
    foreach (var member in genericSets[setName].Children) {
      if (member is { Kind: "Type", Children.Count: 0 } && IsSetName(member.Text!)) {
        if (InSet(type, member.Text!, visited)) {
          return true;
        }
        continue;
      }
      string resolved = ResolveType(member);
      if (type == resolved || (IsClass(resolved) && IsClass(type) && IsSubtype(type, resolved))) {
        return true;
      }
    }
    return false;
  }

  bool Satisfies(string type, Node constraint) {
    if (constraint is not { Kind: "Type" }) {
      throw NotSupported("a constraint that is not a plain name", constraint);
    }
    if (constraint.Children.Count == 0 && IsSetName(constraint.Text!)) {
      return InSet(type, constraint.Text!);
    }
    // a trait with placeholders: any class that implements one of its copies
    if (constraint.Children.Count == 0 && genericClasses.TryGetValue(constraint.Text!, out var placeholderTrait) && placeholderTrait is { IsTrait: true, HasPlaceholders: true }) {
      return classes.TryGetValue(type, out var implementer)
        && AllTraits(implementer).Any(trait => classInstanceArgs.TryGetValue(trait.Name, out var copy) && copy.Template == placeholderTrait);
    }
    string required = ResolveType(constraint);
    if (!IsClass(required)) {
      throw Error($"'{constraint.Text}' is not a trait, a class or a set: it cannot be a constraint", constraint);
    }
    return IsClass(type) && IsSubtype(type, required);
  }

  void CheckConstraints(List<TypeParam> ps, List<string> args, Node at, string what) {
    for (int index = 0; index < ps.Count; index++) {
      foreach (var written in ps[index].Constraints) {
        // a constraint with type arguments (Container<Item>) is read with the arguments of this copy
        var constraint = written.Children.Count > 0
          ? CloneNode(written, SubstituteTypes(ps.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second)))
          : written;
        if (!Satisfies(args[index], constraint)) {
          string parameter = ps[index].Name.StartsWith('_') ? "the parameter " + ps[index].Name[1..] : "the type parameter " + ps[index].Name;
          throw Error($"{DisplayName(args[index])} does not satisfy {ConstraintShown(constraint)}, required by {parameter} of {what}", at);
        }
      }
      foreach (var (placeholder, written) in ps[index].PlaceholderConstraints) {
        string? fixedType = PlaceholderValue(args[index], placeholder);
        if (fixedType == null) {
          throw Error($"{DisplayName(args[index])} has no placeholder '{placeholder}', named in 'where {ps[index].Name}.{placeholder}' of {what}", at);
        }
        var constraint = written.Children.Count > 0
          ? CloneNode(written, SubstituteTypes(ps.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second)))
          : written;
        if (!Satisfies(fixedType, constraint)) {
          throw Error($"{ps[index].Name}.{placeholder} of {DisplayName(args[index])} is {DisplayName(fixedType)}, which does not satisfy {ConstraintShown(constraint)}, required by {what}", at);
        }
      }
    }
  }

  // the type a class fixed for a placeholder with 'with (Item is u32)', or null when none of its traits has that placeholder
  string? PlaceholderValue(string type, string placeholder) {
    if (!classes.TryGetValue(type, out var cls)) {
      return null;
    }
    foreach (var trait in AllTraits(cls)) {
      if (classInstanceArgs.TryGetValue(trait.Name, out var copy) && copy.Template.HasPlaceholders) {
        int position = copy.Template.Params.FindIndex(p => p.Name == placeholder);
        if (position >= 0) {
          return copy.Args[position];
        }
      }
    }
    return null;
  }

  // ---------------------------------------------------------------- generic classes

  // Stack<i32> as a type: the name of the copy of the class; a copy that does not exist yet makes the attempt start again
  string ClassInstanceName(GenericClass generic, List<string> args, Node at) {
    if (args.Count != generic.Params.Count) {
      throw Error($"{generic.Name} takes {generic.Params.Count} type argument(s), not {args.Count}", at);
    }
    string mangled = MangledName(generic.Name, args);
    if (classes.ContainsKey(mangled) || classInstanceArgs.ContainsKey(mangled)) {
      return mangled;
    }
    var key = new GenericClassKey(generic.Name);
    if (seeds.Classes.Any(seed => seed.Template == key && seed.Args.SequenceEqual(args))) {
      throw new CompileException($"internal error: the generic class instance {mangled} was not created", at.Line);
    }
    if (seeds.Classes.Count(seed => seed.Template == key) >= MaxCopies) {
      throw Error($"Generic {generic.Name} defined in line {generic.Node.Line} was used with more than {MaxCopies} instances"
        + $" in {(currentFun != null ? "function " + currentFun.Name : "the program")}", at);
    }
    int nesting = args.Max(NestingOf);
    if (nesting + 1 > MaxChainDepth) {
      throw Error($"Generic {generic.Name} defined in line {generic.Node.Line} is nested more than {MaxChainDepth} levels deep", at);
    }
    seeds.Classes.Add((key, args));
    throw new NeedInstanceException();
  }

  // how deep generic copies are nested in a type name: Box__Box__i32 → 2
  static int NestingOf(string type) => Regex.Matches(type, "__").Count + 1;

  // the type arguments written in a Type node
  List<string> TypeArgsOf(IEnumerable<Node> nodes, List<TypeParam> ps) {
    var args = new List<string>();
    foreach (var node in nodes) {
      var param = args.Count < ps.Count ? ps[args.Count] : null;
      if (param?.ValueType != null) {
        args.Add(ValueArgOf(node, param));
      } else if (node.Kind is "Count" or "ValueArg") {
        throw Error(param != null ? $"'{param.Name}' is a type parameter: write a type" : "a type is expected here, not a value", node);
      } else {
        args.Add(ResolveType(node));
      }
    }
    return args;
  }

  // Stack<i32> written in an expression (a constructor call or a static member): the copy of the class.
  // Box<Num>(x): a set as type argument is replaced by the member the argument of the constructor selects (11.3.9)
  string? GenericRefClass(Node reference, List<Node>? constructorArgs = null, Scope? scope = null) {
    if (reference.Children[0] is not { Kind: "Name" } target || !genericClasses.TryGetValue(target.Text!, out var generic)) {
      return null;
    }
    var written = reference.Children.Skip(1).ToList();
    if (HasSetArg(reference)) {
      if (constructorArgs == null || scope == null) {
        throw Error($"a set as a type argument needs a constructor call that gives the value, as in {generic.Name}<Set>(value) (11.3.9)", reference);
      }
      written = ChooseSetArgs(generic, written, constructorArgs, scope);
    }
    return ClassInstanceName(generic, TypeArgsOf(written, generic.Params), reference);
  }

  bool HasSetArg(Node reference) {
    return reference.Children.Skip(1).Any(arg => arg is { Kind: "Type", Children.Count: 0 } && IsSetName(arg.Text!));
  }

  // the type arguments with each set replaced by the member its constructor argument selects
  List<Node> ChooseSetArgs(GenericClass generic, List<Node> written, List<Node> constructorArgs, Scope scope) {
    var chosen = new List<Node>();
    for (int index = 0; index < written.Count; index++) {
      var node = written[index];
      var param = index < generic.Params.Count ? generic.Params[index] : null;
      if (param == null || param.ValueType != null || node is not { Kind: "Type", Children.Count: 0 } || !IsSetName(node.Text!)) {
        chosen.Add(node);
        continue;
      }
      var constructor = generic.Node.Children.FirstOrDefault(child => child.Kind == "PrimaryCtor");
      var bound = new Dictionary<string, string>();
      var names = new HashSet<string> { param.Name };
      if (constructor != null) {
        foreach (var (pattern, argument) in PatternPairs(constructor, constructorArgs)) {
          if (!MentionsParam(pattern, names)) {
            continue;
          }
          string actual = ExprOf(argument, scope).Type;
          if (actual is "null" or "void" || IsMulti(actual)) {
            continue;
          }
          MatchType(pattern, actual, bound, names, argument);
        }
      }
      if (!bound.TryGetValue(param.Name, out var valueType)) {
        throw Error($"cannot choose a member of {node.Text}: no argument of the constructor of {generic.Name} gives the value for '{param.Name}'", node);
      }
      chosen.Add(new Node("ResolvedType", new Token(TokenKind.Ident, "", node.Line, 0), ChooseSetMember(valueType, node.Text!, node)));
    }
    return chosen;
  }

  // the members of a set, nested sets opened, in the order written
  List<string> SetMembers(string setName, HashSet<string>? visited = null) {
    visited ??= new HashSet<string>();
    var members = new List<string>();
    if (!visited.Add(setName)) {
      return members;
    }
    void Add(string type) {
      if (!members.Contains(type)) {
        members.Add(type);
      }
    }
    if (PredefinedSets.TryGetValue(setName, out var names)) {
      foreach (string member in names) {
        if (PredefinedSets.ContainsKey(member)) {
          members.AddRange(SetMembers(member, visited).Where(type => !members.Contains(type)));
        } else {
          Add(member);
        }
      }
      return members;
    }
    foreach (var member in genericSets[setName].Children) {
      if (member is { Kind: "Type", Children.Count: 0 } && IsSetName(member.Text!)) {
        members.AddRange(SetMembers(member.Text!, visited).Where(type => !members.Contains(type)));
      } else {
        Add(ResolveType(member));
      }
    }
    return members;
  }

  static string MacroType(string type) {
    string baseType = type.EndsWith('?') ? type[..^1] : type;
    string kind = SignedTypes.Contains(baseType) ? "signed" : UnsignedTypes.Contains(baseType) ? "unsigned" : RealTypes.Contains(baseType) ? "real" : baseType;
    return type.EndsWith('?') ? kind + "?" : kind;
  }

  // 11.3.9: the member of the set a value of this type becomes
  string ChooseSetMember(string type, string setName, Node at) {
    var members = SetMembers(setName);
    if (members.Contains(type)) {
      return type;
    }
    if (IsClass(type)) {
      // the nearest parent class that is a member, then the members left to right
      for (var parent = classes.TryGetValue(type, out var info) ? info.Parent : null; parent != null; parent = parent.Parent) {
        if (members.Contains(parent.Name)) {
          return parent.Name;
        }
      }
      var viaTrait = members.FirstOrDefault(member => IsClass(member) && IsSubtype(type, member));
      if (viaTrait != null) {
        return viaTrait;
      }
    } else {
      // a member of the same macrotype that holds the value, then one of a compatible macrotype (section 6)
      var sameKind = members.FirstOrDefault(member => MacroType(member) == MacroType(type) && Assignable(member, type));
      if (sameKind != null) {
        return sameKind;
      }
      var compatible = members.FirstOrDefault(member => Assignable(member, type));
      if (compatible != null) {
        return compatible;
      }
    }
    throw Error($"{DisplayName(type)} does not convert implicitly to any member of {setName}; tried: {string.Join(", ", members.Select(DisplayName))}", at);
  }

  // after the classes are registered: the type arguments of each copy must satisfy the constraints
  void CheckClassInstances() {
    foreach (var (_, (generic, args)) in classInstanceArgs) {
      CheckConstraints(generic.Params, args, generic.Node, generic.Name);
    }
  }

  // ---------------------------------------------------------------- type switch (11.3.7)

  // value is ? { Numeric: a, string: b, default: c }: the first case that matches the type of the value is the only one compiled
  Expr TypeSwitch(Node n, Scope scope, string? expected) {
    var subject = ExprOf(n.Children[0], scope);
    string type = subject.Type;
    Node? chosen = null;
    bool sawDefault = false;
    var seen = new HashSet<string>();
    foreach (var arm in n.Children.Skip(1)) {
      bool matches = false;
      foreach (var label in arm.Children[0].Children) {
        if (label.Kind == "Default") {
          if (sawDefault) {
            throw Error("a type switch can have only one 'default'", label);
          }
          matches = true;
          if (chosen == null) {
            chosen = arm;
          }
          sawDefault = true;
          continue;
        }
        if (sawDefault || !seen.Add(TypeNodeText(label))) {
          Warn("a case of the type switch can never match: an earlier case already takes its types", label, 2);
        }
        if (chosen == null && CaseMatches(type, label)) {
          chosen = arm;
        }
      }
    }
    if (chosen == null) {
      throw Error($"no case of the type switch matches {DisplayName(type)}: add a case for it or a 'default'", n);
    }
    return ExprOf(chosen.Children[1], scope, expected);
  }

  static string TypeNodeText(Node type) => type.Kind + ":" + type.Text + "(" + string.Join(",", type.Children.Select(TypeNodeText)) + ")";

  // a case is a type, a class (and its subclasses), a trait or a set
  bool CaseMatches(string type, Node label) {
    if (label is { Kind: "Type", Children.Count: 0 } && IsSetName(label.Text!)) {
      return InSet(type, label.Text!);
    }
    string resolved = ResolveType(label);
    return type == resolved || (IsClass(resolved) && IsClass(type) && IsSubtype(type, resolved));
  }

  // ---------------------------------------------------------------- generic functions

  FunSig InstantiateFun(GenericFun generic, List<string> args, Node at) {
    string mangled = MangledName(generic.Name, args);
    if (funs.TryGetValue(mangled, out var existing)) {
      return existing[0];
    }
    string where = currentFun != null ? "function " + currentFun.Name : "the program";
    if (generic.Copies >= MaxCopies) {
      throw Error($"Generic {generic.Name} defined in line {generic.Node.Line} was used with more than {MaxCopies} instances in {where}", at);
    }
    int depth = (currentFun?.InstanceDepth ?? 0) + 1;
    string chain = currentFun != null ? currentFun.InstanceChain + " -> " + mangled : mangled;
    if (depth > MaxChainDepth) {
      throw Error($"Generic {generic.Name} defined in line {generic.Node.Line}: more than {MaxChainDepth} nested instantiations ({chain})", at);
    }
    CheckConstraints(generic.Params, args, at, generic.Name);
    var map = generic.Params.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second);
    var copy = CloneNode(generic.Node, SubstituteTypes(map));
    copy.Text = mangled;
    copy.Children.RemoveAll(child => child.Kind == "GenericParams");
    var sig = RegisterFun(copy, null)!;
    generic.Copies++;
    sig.InstanceDepth = depth;
    sig.InstanceChain = depth == 1 ? mangled : chain;
    // the copy is made while other code is being compiled: it is checked for exceptions and ownership on its own
    failingFuns.Add(sig);
    Classify(sig, new Dictionary<FunSig, int>());
    pendingInstances.Add(sig);
    return sig;
  }

  // name<u8>(args) or name(args): the type arguments written, then the ones the arguments tell
  FunSig InstantiateForCall(GenericFun generic, List<Node> written, List<Node> args, Scope scope, Node at) {
    var bound = new Dictionary<string, string>();
    if (written.Count > generic.Params.Count) {
      throw Error($"{generic.Name} takes {generic.Params.Count} type argument(s), not {written.Count}", at);
    }
    var writtenTypes = TypeArgsOf(written, generic.Params);
    for (int index = 0; index < writtenTypes.Count; index++) {
      bound[generic.Params[index].Name] = writtenTypes[index];
    }
    var names = generic.Params.Select(p => p.Name).ToHashSet();
    foreach (var (pattern, argument) in PatternPairs(generic, args)) {
      if (!MentionsParam(pattern, names)) {
        continue;
      }
      string actual = ExprOf(argument, scope).Type;
      if (actual is "null" or "void" || IsMulti(actual)) {
        continue;
      }
      MatchType(pattern, actual, bound, names, argument);
    }
    var inferred = new List<string>();
    foreach (var param in generic.Params) {
      if (!bound.TryGetValue(param.Name, out var type)) {
        string shown = param.Name.StartsWith('_') ? param.Name[1..] : param.Name;
        throw Error($"cannot infer the type argument for '{shown}' of {generic.Name}: write it, as in {generic.Name}<...>(...)", at);
      }
      inferred.Add(type);
    }
    return InstantiateFun(generic, inferred, at);
  }

  // the declared type of each parameter next to the argument given to it (positional, named and variadic)
  static List<(Node Pattern, Node Argument)> PatternPairs(GenericFun generic, List<Node> args) {
    return PatternPairs(generic.Node.Children.First(child => child.Kind == "Params"), args);
  }

  // the same for any list of parameters (the primary constructor of a generic class)
  static List<(Node Pattern, Node Argument)> PatternPairs(Node parameterList, List<Node> args) {
    var parameters = parameterList.Children;
    var pairs = new List<(Node, Node)>();
    var filled = new Node?[parameters.Count];
    int variadicIndex = parameters.Count > 0 && parameters[^1].Kind == "VariadicParam" ? parameters.Count - 1 : -1;
    foreach (var named in args.Where(arg => arg.Kind == "Named")) {
      int index = parameters.FindIndex(param => param.Text == named.Text);
      if (index >= 0) {
        filled[index] = named.Children[0];
      }
    }
    int next = 0;
    foreach (var positional in args.Where(arg => arg.Kind != "Named")) {
      while (next < filled.Length && filled[next] != null) {
        next++;
      }
      if (variadicIndex >= 0 && next >= variadicIndex && filled[variadicIndex] == null) {
        pairs.Add((parameters[variadicIndex].Children[0], positional));
        continue;
      }
      if (next >= filled.Length) {
        break;
      }
      filled[next++] = positional;
    }
    for (int index = 0; index < filled.Length; index++) {
      if (filled[index] != null && index != variadicIndex) {
        pairs.Add((parameters[index].Children[0], filled[index]!));
      }
    }
    return pairs;
  }

  static bool MentionsParam(Node pattern, HashSet<string> names) {
    if (pattern.Kind == "Type" && pattern.Children.Count == 0 && pattern.Text != null && names.Contains(pattern.Text)) {
      return true;
    }
    return pattern.Children.Any(child => MentionsParam(child, names));
  }

  // The declared type (a pattern with type parameters) against the type of the argument: binds the parameters
  void MatchType(Node pattern, string actual, Dictionary<string, string> bound, HashSet<string> names, Node at) {
    switch (pattern.Kind) {
      case "Type" when pattern.Children.Count == 0 && pattern.Text != null && names.Contains(pattern.Text):
        if (!bound.TryGetValue(pattern.Text, out var current)) {
          bound[pattern.Text] = actual;
        } else if (current != actual) {
          if (Assignable(current, actual)) {
            return;
          }
          if (Assignable(actual, current)) {
            bound[pattern.Text] = actual;
            return;
          }
          string shown = pattern.Text.StartsWith('_') ? pattern.Text[1..] : pattern.Text;
          throw Error($"the arguments give '{shown}' two types, {DisplayName(current)} and {DisplayName(actual)}: write the type argument or cast", at);
        }
        return;
      case "Type" when pattern.Text == "arr" && pattern.Children.Count == 1:
        if (IsArr(actual)) {
          MatchType(pattern.Children[0], ArrElem(actual), bound, names, at);
        }
        return;
      case "Type" when pattern.Children.Count > 0 && pattern.Text != null && genericClasses.ContainsKey(pattern.Text):
        if (classInstanceArgs.TryGetValue(actual, out var instance) && instance.Template.Name == pattern.Text
            && instance.Args.Count == pattern.Children.Count) {
          for (int index = 0; index < instance.Args.Count; index++) {
            MatchType(pattern.Children[index], instance.Args[index], bound, names, at);
          }
        }
        return;
      case "TupleType":
        if (IsTuple(actual) && TupleElems(actual).Count == pattern.Children.Count) {
          var elems = TupleElems(actual);
          for (int index = 0; index < elems.Count; index++) {
            MatchType(pattern.Children[index].Children[0], elems[index].Type, bound, names, at);
          }
        }
        return;
      case "Nullable":
        MatchType(pattern.Children[0], IsNullable(actual) ? BaseOf(actual) : actual, bound, names, at);
        return;
    }
  }

  // does a call of a generic function give a new object? (decided from the returns of the template)
  bool TemplateReturnsMoves(GenericFun generic) {
    if (!busyTemplates.Add(generic)) {
      return true;
    }
    try {
      var locals = new Dictionary<string, Node>();
      CollectDeclarations(generic.Node, locals);
      var state = new Dictionary<FunSig, int>();
      bool sawOwned = false;
      bool sawBorrowed = false;
      foreach (var returned in CollectReturns(generic.Node)) {
        if (returned.Children.Count == 0) {
          continue;
        }
        var kind = ReturnKind(returned.Children[0], locals, state);
        sawOwned |= kind == true;
        sawBorrowed |= kind == false;
      }
      return sawOwned && !sawBorrowed;
    } finally {
      busyTemplates.Remove(generic);
    }
  }
}
