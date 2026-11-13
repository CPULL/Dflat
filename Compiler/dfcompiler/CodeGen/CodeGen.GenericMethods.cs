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
      if (ModsOf(member).Contains("static")) {
        throw NotSupported("static generic methods (call a generic function, or a method of an object)", member);
      }
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
