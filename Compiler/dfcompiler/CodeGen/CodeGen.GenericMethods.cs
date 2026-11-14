using dfparser;

namespace dfcompiler;

// Generic methods of classes (spec 11.3.1): fun addSize<T>(T t) { } inside a class, and methods with a set as the type of a parameter
// (fun a(Ints x)). As for the generic functions, a copy is made for each list of type arguments the program uses; the copy is an
// ordinary method of the class (its name mangled, addSize__i32), so calls, visibility, this and dispatch work as for any method.
// The copies are made while the methods of a class are registered: a call that needs a copy that does not exist yet records it in
// the seeds (against the topmost class that declares the method) and the compilation starts again, as for generic classes. Every
// class of the hierarchy that declares or replaces the generic method gets every copy: a replacing method replaces every T.
public sealed partial class CodeGen {
  readonly Dictionary<(string Class, string Name), GenericFun> genericMethods = new();
  readonly Dictionary<string, string> methodCopyNames = new();   // addSize__i32 -> addSize<i32>, for the messages

  // the name of a method as the program wrote it: a copy of a generic method shows its type arguments
  string MethodCopyName(string name) => methodCopyNames.TryGetValue(name, out var shown) ? shown : name;

  // the template of a generic function or method: its own type parameters, and one for each parameter whose type is a set
  GenericFun MakeGenericTemplate(Node fun, string name) {
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
    return new GenericFun(name, template, ps);
  }

  // the classes and traits cls inherits from, nearest first: its parents, then the traits of the whole chain (and their parent traits)
  IEnumerable<ClassInfo> GenericMethodOwners(ClassInfo cls) {
    for (var c = cls; c != null; c = c.Parent) {
      yield return c;
    }
    for (var t = cls.Super; t != null; t = t.Super) {
      yield return t;   // cls is a trait: its parent traits
    }
    foreach (var trait in AllTraits(cls)) {
      yield return trait;
    }
  }

  // the topmost class or trait (cls or what it inherits) that declares the generic method name, or null; a trait comes before a class
  string? GenericMethodRoot(ClassInfo? cls, string name) {
    return cls == null ? null : GenericMethodOwners(cls).LastOrDefault(owner => genericMethods.ContainsKey((owner.Name, name)))?.Name;
  }

  // what cls inherits declares the generic method name: the root of it, or null
  string? InheritedGenericRoot(ClassInfo cls, string name) {
    var inherited = GenericMethodOwners(cls).Where(owner => owner != cls);
    return inherited.LastOrDefault(owner => genericMethods.ContainsKey((owner.Name, name)))?.Name;
  }

  // the nearest template of the generic method name, seen from cls
  GenericFun? GenericMethodOf(ClassInfo cls, string name) {
    foreach (var owner in GenericMethodOwners(cls)) {
      if (genericMethods.TryGetValue((owner.Name, name), out var template)) {
        return template;
      }
    }
    return null;
  }

  // before the methods of cls are registered: each generic method becomes its template, and its copies (the seeds) take its place in the
  // body, so they are registered, replaced and dispatched like the other methods
  void ExpandGenericMethods(ClassInfo cls, Node body) {
    for (int index = 0; index < body.Children.Count; index++) {
      var member = body.Children[index];
      if (member.Kind != "Fun" || IsGenericEquals(member)) {
        continue;
      }
      if (!IsGenericFunNode(member)) {
        if (ReadMods(member).IsOverride && InheritedGenericRoot(cls, member.Text!) is string generic) {
          throw Error($"'{member.Text}' is a generic method of {generic}: the replacing method is generic too, and replaces it for every type (spec 11.3.1)", member);
        }
        continue;
      }
      string name = member.Text!;
      bool isOverride = ReadMods(member).IsOverride;
      string? parentRoot = InheritedGenericRoot(cls, name);
      if (isOverride && parentRoot == null) {
        throw Error($"'{name}' replaces nothing: no parent class or trait has the generic method {name}", member);
      }
      if (!isOverride && parentRoot != null) {
        throw Error($"'{name}' is a generic method of {parentRoot}: write replace fun to replace it (for every type, spec 11.3.1)", member);
      }
      if (cls.Parent != null && parentRoot == null && LookupMethods(cls.Parent, name).Count > 0) {
        throw Error($"'{name}' is a method of a parent class: a generic method cannot share its name with it", member);
      }
      // a static generic method: its copies are static methods of the class (Util.pick<i32>(...), spec 11.3.1.1)
      var template = MakeGenericTemplate(member, name);
      genericMethods[(cls.Name, name)] = template;
      string root = parentRoot ?? cls.Name;
      body.Children.RemoveAt(index);
      foreach (var (seedRoot, seedName, args) in seeds.Methods) {
        if (seedRoot != root || seedName != name) {
          continue;
        }
        CheckConstraints(template.Params, args, member, name);
        var map = template.Params.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second);
        var copy = CloneNode(template.Node, SubstituteTypes(map));
        copy.Text = MangledName(name, args);
        methodCopyNames[copy.Text] = $"{name}<{string.Join(", ", args.Select(DisplayName))}>";
        copy.Children.RemoveAll(child => child.Kind == "GenericParams");
        body.Children.Insert(index++, copy);
      }
      index--;
    }
  }

  // the generic methods of a struct (spec 11.1.5.2, 11.3.1.1): as in a class, each one becomes its template and its copies (the seeds, under the
  // name of the struct) take its place among the methods; a struct has no inheritance, so there is nothing to replace
  void ExpandStructGenericMethods(StructInfo info) {
    var body = info.Node;
    for (int index = 0; index < body.Children.Count; index++) {
      var member = body.Children[index];
      if (member.Kind != "Fun" || !IsGenericFunNode(member)) {
        continue;
      }
      string name = member.Text!;
      var template = MakeGenericTemplate(member, name);
      genericMethods[(info.Name, name)] = template;
      body.Children.RemoveAt(index);
      foreach (var (seedRoot, seedName, args) in seeds.Methods) {
        if (seedRoot != info.Name || seedName != name) {
          continue;
        }
        CheckConstraints(template.Params, args, member, name);
        var map = template.Params.Zip(args).ToDictionary(pair => pair.First.Name, pair => pair.Second);
        var copy = CloneNode(template.Node, SubstituteTypes(map));
        copy.Text = MangledName(name, args);
        methodCopyNames[copy.Text] = $"{name}<{string.Join(", ", args.Select(DisplayName))}>";
        copy.Children.RemoveAll(child => child.Kind == "GenericParams");
        body.Children.Insert(index++, copy);
      }
      index--;
    }
  }

  // s.name(args), s.name<T>(args), S.name<T>(args) on a struct with the generic method name: the name of the copy (requested as for a class),
  // or null when name is not a generic method of the struct
  string? StructGenericMethodName(StructInfo info, string name, List<Node> written, List<Node> args, Scope scope, Node at) {
    if (!genericMethods.TryGetValue((info.Name, name), out var template)) {
      if (written.Count > 0) {
        throw Error($"'{name}' is not a generic method of {DisplayName(info.Name)}", at);
      }
      return null;
    }
    var types = InferTypeArgs(template, written, args, scope, at);
    CheckConstraints(template.Params, types, at, name);
    string mangled = MangledName(name, types);
    if (funs.ContainsKey(info.Name + "." + mangled)) {
      return mangled;
    }
    if (seeds.Methods.Any(seed => seed.Root == info.Name && seed.Name == name && seed.Args.SequenceEqual(types))) {
      throw new CompileException($"internal error: the copy {mangled} of the generic method {name} was not created", at.Line);
    }
    int maxCopies = Limit("copies", 16);
    if (seeds.Methods.Count(seed => seed.Root == info.Name && seed.Name == name) >= maxCopies) {
      throw Error($"the generic method {info.Name}.{name} was used with more than {maxCopies} type lists (the limit is set by --max-copies)", at);
    }
    seeds.Methods.Add((info.Name, name, types));
    throw new NeedInstanceException();
  }

  // Box(5), Pair("a", 2): the type arguments of a generic class from the arguments of its primary constructor (spec 11.3.5), as for a generic
  // function; a literal given to a parameter of a set type takes the best member of the set; a parameter that no argument fixes, or a value
  // parameter: written arguments are needed
  List<string> InferClassTypeArgs(GenericClass template, List<Node> args, Scope scope, Node at) {
    var constructor = template.Node.Children.FirstOrDefault(child => child.Kind == "PrimaryCtor");
    var bound = new Dictionary<string, string>();
    var names = template.Params.Where(p => p.ValueType == null).Select(p => p.Name).ToHashSet();
    if (constructor != null) {
      var asFun = new GenericFun(template.Name, Synthetic("Fun", template.Node, template.Name).Add(constructor), template.Params);
      foreach (var (pattern, argument) in PatternPairs(constructor, args)) {
        if (!MentionsParam(pattern, names)) {
          continue;
        }
        string actual = ExprOf(argument, scope).Type;
        if (actual is "null" or "void" || IsMulti(actual)) {
          continue;
        }
        MatchType(pattern, SetLiteralArgType(asFun, pattern, argument, actual), bound, names, argument);
      }
    }
    var result = new List<string>();
    foreach (var param in template.Params) {
      if (!bound.TryGetValue(param.Name, out var type)) {
        throw Error($"{template.Name}: {param.Name} cannot be inferred from the arguments of its constructor: write the type arguments, {template.Name}<...>(...) (spec 11.3.5)", at);
      }
      result.Add(type);
    }
    CheckConstraints(template.Params, result, at, template.Name);
    return result;
  }

  // Pair(1, 2), Pair(a: "x", b: "y"): the type arguments of a generic struct from the values given to its fields whose type is a type parameter
  // (by position or by name); a parameter that no value fixes, two values that disagree, or a value parameter: written arguments are needed
  List<string> InferStructTypeArgs(GenericClass template, List<Node> args, Scope scope, Node at) {
    var fields = template.Node.Children.Where(child => child.Kind == "Field").ToList();
    var found = new Dictionary<string, string>();
    for (int index = 0; index < args.Count; index++) {
      var arg = args[index];
      Node? field = arg.Kind == "Named" ? fields.FirstOrDefault(f => f.Text == arg.Text) : index < fields.Count ? fields[index] : null;
      var valueNode = arg.Kind == "Named" ? arg.Children[0] : arg;
      if (field?.Children[0] is not { Kind: "Type", Children.Count: 0 } fieldType || template.Params.All(p => p.Name != fieldType.Text)
          || field.Children.Any(child => child.Kind == "Size")) {
        continue;
      }
      string type = ExprOf(valueNode, scope).Type;
      if (found.TryGetValue(fieldType.Text!, out var earlier) && earlier != type) {
        throw Error($"{template.Name}: {fieldType.Text} is {DisplayName(earlier)} and {DisplayName(type)} from the values given: write the type arguments, {template.Name}<...>(...) (spec 11.3.5)", at);
      }
      found[fieldType.Text!] = type;
    }
    var result = new List<string>();
    foreach (var param in template.Params) {
      if (!found.TryGetValue(param.Name, out var type)) {
        throw Error($"{template.Name}: {param.Name} cannot be inferred from the values given: write the type arguments, {template.Name}<...>(...) (spec 11.3.5)", at);
      }
      result.Add(type);
    }
    CheckConstraints(template.Params, result, at, template.Name);
    return result;
  }

  // obj.name(args) or obj.name<T>(args) on a class with the generic method name: the name of the copy for these type arguments. A copy that
  // does not exist yet is recorded and the attempt starts again. null when name is not a generic method of cls
  string? GenericMethodCallName(ClassInfo cls, string name, List<Node> written, List<Node> args, Scope scope, Node at) {
    var template = GenericMethodOf(cls, name);
    if (template == null) {
      if (written.Count > 0) {
        throw Error($"'{name}' is not a generic method of {DisplayName(cls.Name)}", at);
      }
      return null;
    }
    var types = InferTypeArgs(template, written, args, scope, at);
    CheckConstraints(template.Params, types, at, name);
    string mangled = MangledName(name, types);
    if (LookupMethods(cls, mangled).Count > 0) {
      return mangled;
    }
    string root = GenericMethodRoot(cls, name)!;
    if (seeds.Methods.Any(seed => seed.Root == root && seed.Name == name && seed.Args.SequenceEqual(types))) {
      throw new CompileException($"internal error: the copy {mangled} of the generic method {name} was not created", at.Line);
    }
    int maxCopies = Limit("copies", 16);
    if (seeds.Methods.Count(seed => seed.Root == root && seed.Name == name) >= maxCopies) {
      throw Error($"the generic method {root}.{name} was used with more than {maxCopies} type lists (the limit is set by --max-copies)", at);
    }
    seeds.Methods.Add((root, name, types));
    throw new NeedInstanceException();
  }
}
