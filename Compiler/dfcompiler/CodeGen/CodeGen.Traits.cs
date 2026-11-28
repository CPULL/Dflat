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
    var body = trait.Node.Children.First(child => child.Kind == "Block");
    foreach (var member in body.Children) {
      switch (member.Kind) {
        case "Decl": {
          var (visibility, _, isStatic, isConst, isOverride) = ReadMods(member);
          if (isStatic || isConst || isOverride) {
            throw Error("a trait property cannot be static, const or replace", member);
          }
          if (ModsOf(member).Any(mod => mod is "asparent" or "unrestricted")) {
            throw Error("'asparent' and 'unrestricted' apply to functions", member);
          }
          if (member.Children.Any(child => child.Kind == "Size")) {
            throw NotSupported("Sized declarations (arr<T>[n] name)", member);
          }
          bool countedField = IsCountedDecl(member);   // @Dog pet, &@i32 total: a user of its object or cell, as a counted field of a class (spec 12.7)
          if (countedField && member.Children.Any(child => child.Kind == "Property")) {
            throw NotSupported("Replacing a counted property of a trait", member);
          }
          var init = member.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
          bool weakField = member.Children.Any(child => child.Kind == "Weak");   // %Dog seen, &%Dog writer: a weak reference, as a weak field of a class (spec 12.7)
          if (init?.Kind == "WeakInit" && !weakField) {
            throw Error($"a weak property is declared with % before its type: %Type {member.Text}", init);
          }
          if (weakField && member.Children.Any(child => child.Kind == "Property")) {
            throw NotSupported("Replacing a counted or weak property of a trait", member);
          }
          if (init is { Kind: "WeakInit", Text: "default" }) {
            init = null;   // the null start of %T name is not written
          }
          var typeNode = member.Children.First(child => TypeNodeKinds.Contains(child.Kind));
          if (typeNode is { Kind: "Type", Text: "var" }) {
            throw Error("'var' cannot be used for a property: write its type", member);
          }
          if (FieldCandidates(trait, member.Text!).Any()) {
            throw Error($"the property '{member.Text}' is already declared in {trait.Name} or its parent trait", member);
          }
          // private (no keyword, spec 1.6.1): only the code of the trait and, read-only, its sub-traits (spec 11.2)
          string fieldType = ResolveType(typeNode);
          if (countedField && !IsClass(fieldType) && !IsTraitType(fieldType) && !IsCountedValueType(fieldType)) {
            throw NotSupported($"Counted values of type {fieldType} (spec 12.7)", member);
          }
          if (weakField && !IsClass(fieldType)) {
            throw Error("only objects have weak references", member);
          }
          var traitField = new FieldInfo(member.Text!, fieldType, trait, member) {
            Visibility = visibility,
            IsCounted = countedField,
            IsWeak = weakField,
            IsWritable = (countedField || weakField) && member.Children.Any(child => child.Kind == "ReadWrite"),
            Init = init?.Children[0],
            IsReadOnly = ModsOf(member).Contains("readonly")   // a simple field: the trait and its classes write it, the others read it (spec 11.2)
          };
          if (trait.Name == "Keyable" && member.Text is "keyUses" or "keyHash") {
            traitField.Hidden = true;   // the counter and the stored hash of Keyable: not in the hash, a clone or the text of the object (spec 11.2.1)
          }
          if (member.Children.FirstOrDefault(child => child.Kind == "Property") is Node replacedProperty) {
            traitField.HasAccessors = true;   // a class replaces it: read and written through get_ and set_ (spec 11.2)
            traitField.CanRead = replacedProperty.Text!.Contains('r');
            traitField.CanWrite = replacedProperty.Text!.Contains('w');
          }
          trait.Fields.Add(traitField);
          break;
        }
        case "Constructor": {
          if (member.Children.FirstOrDefault(child => child.Kind == "Chain") is Node chain) {
            // Named(string n) : Base(n.len): the constructor of the parent trait runs first (spec 11.2)
            if (chain.Children.Count != 1 || trait.Super == null || chain.Children[0].Text != trait.Super.Name && chain.Children[0].Text != TemplateName(trait.Super)) {
              throw Error($"a constructor of the trait {DisplayName(trait.Name)} chains only to its parent trait{(trait.Super != null ? ", " + DisplayName(trait.Super.Name) : " (it has none)")}", chain);
            }
            chain.Children[0].Text = trait.Super.Name;
            trait.ChainsToSuper = true;
          }
          trait.CtorNodes.Add(member);              // a trailing atEnd is a destructor fragment (spec 11.4, RegisterTraitInit)              // overloads, like the constructors of a class (spec 11.1.1, 11.2)
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
    ExpandGenericMethods(trait, body);   // a generic method of a trait: its copies, replaced by every class that implements it (spec 11.3.1)
    var members = body.Children.Where(member => member.Kind == "Fun").ToList();
    var chainLinks = new HashSet<Node>();
    foreach (var member in members.ToList()) {
      if (member.Children.Any(child => child.Kind is "ExecuteBefore" or "ExecuteAfter")) {
        // a sub-trait that redeclares a method of a parent trait with its own block: one more link of the chain (spec 11.2). Its wrapper
        // answers the parent's __impl_m, and the class implements one level deeper: m runs B(parent), B(this), the class, A(this), A(parent)
        while (AncestorHooks(trait, member.Text!)) {
          member.Text = ImplPrefix + member.Text;
          chainLinks.Add(member);
        }
        members.Insert(members.IndexOf(member), SplitExecuteBlocks(trait, member));
      }
    }
    foreach (var member in members) {
      var (visibility, explicitVisibility, isStatic, isConst, isOverride) = ReadMods(member);
      if (isStatic || isConst) {
        throw Error("a trait function cannot be static", member);
      }
      if (isOverride && member.Children.Any(child => child.Kind == "NoBody")) {
        throw Error($"'{member.Text}': a sub-trait replaces a method of its parent trait with a new default body (spec 11.2)", member);
      }
      if (isOverride && explicitVisibility) {
        throw Error("a replacing function takes the visibility of the original: write none on it", member);
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
      if (member.Text == "==") {
        // fun bool ==(Shape o) in a trait: required of its classes, called by the class of the left object (spec 11.2)
        CheckOperatorEquals(f, member);
        if (!IsSubtype(trait.Name, f.Params[0].Type) && f.Params[0].Type != trait.Name) {
          throw Error($"the == of the trait {DisplayName(trait.Name)} takes a {DisplayName(trait.Name)}: fun bool ==({DisplayName(trait.Name)} o) (spec 11.2)", member);
        }
      }
      f.IsAccessor = member.Children.Any(child => child.Kind == "AccessorFun");   // the default accessors of a replaced property (spec 11.2)
      f.Visibility = visibility;   // private (no keyword, spec 1.6.1): only the trait, its sub-traits and the class that implements it
      f.IsAsParent = mods.Contains("asparent");
      f.IsUnrestricted = mods.Contains("unrestricted");
      f.TSlot = trait.TraitMethods.Count;
      trait.TraitMethods.Add(f);
      for (var up = trait.Super; up != null; up = up.Super) {
        if (up.TraitMethods.FirstOrDefault(g => g.Name.EndsWith("." + member.Text)) is FunSig parentMethod) {
          if (chainLinks.Contains(member)) {
            // the link answers the parent's method: the same parameters and result (spec 11.2)
            if (!ParamTypes(parentMethod).SequenceEqual(ParamTypes(f)) || parentMethod.Ret != f.Ret) {
              throw Error($"'{ShownMethodName(member.Text!)}' of {DisplayName(trait.Name)} must have the parameters and the result of '{ShownMethodName(member.Text!)}' of {DisplayName(up.Name)} to add a block to it", member);
            }
            f.IsAbstract = false;
            f.Visibility = parentMethod.Visibility;
            break;
          }
          if (isOverride) {
            // replace fun m() { } in a sub-trait: the new default body of m for the classes of the sub-trait (spec 11.2)
            if (!ParamTypes(parentMethod).SequenceEqual(ParamTypes(f)) || parentMethod.Ret != f.Ret) {
              throw Error($"'{member.Text}' of {DisplayName(trait.Name)} must have the parameters and the result of '{member.Text}' of {DisplayName(up.Name)} to replace it", member);
            }
            f.Visibility = parentMethod.Visibility;
            isOverride = false;   // found
            break;
          }
          if (ParamTypes(parentMethod).SequenceEqual(ParamTypes(f))) {
            throw Error($"'{member.Text}' is a method of the parent trait {DisplayName(up.Name)}: write replace fun to give it a new default body (spec 11.2)", member);
          }
        }
      }
      if (isOverride) {
        throw Error($"'{member.Text}' replaces nothing: no parent trait of {DisplayName(trait.Name)} has it", member);
      }
    }
    RegisterTraitInit(trait);
    RegisterTraitDtor(trait);
  }

  // the atEnd after a trait and the fragments of its constructors (spec 11.4): one function of the trait (__tdtor), run when an object of a
  // class that implements it dies, after the blocks of the class and of its parents (EmitDropGlue)
  void RegisterTraitDtor(ClassInfo trait) {
    var trailing = trait.Node.Children.FirstOrDefault(child => child.Kind == "AtEnd" && child.Text == "trailing");
    if (trailing == null && trait.Fragments.Count == 0) {
      return;
    }
    var block = Synthetic("Block", trait.Node);
    if (trait.Fragments.Count > 0) {
      block.Add(FragmentRunner(trait, trait.Node));
    }
    foreach (var statement in trailing?.Children[0].Children ?? new List<Node>()) {
      block.Add(statement);
    }
    var node = Synthetic("Fun", trait.Node, "__tdtor").Add(Synthetic("Params", trait.Node)).Add(block);
    var f = RegisterFun(node, null, trait)!;
    f.Class = trait;
    f.IsDtor = true;
    f.Visibility = Vis.Private;
    trait.Dtor = f;
  }

  // ---------------------------------------------------------------- executeBefore / executeAfter (spec 11.2)

  const string ImplPrefix = "__impl_";   // a name no program can write (a name cannot start with _)

  // a parent trait (at any level) already has executeBefore / executeAfter on this method name
  static bool AncestorHooks(ClassInfo trait, string name) {
    for (var up = trait.Super; up != null; up = up.Super) {
      if (up.Hooked.Contains(name)) {
        return true;
      }
    }
    return false;
  }

  // the name m.result is written with: a copy of a generic method (same__i32) answers to the name of the generic (same, spec 11.3.1)
  string WrittenMethodName(string name) {
    string shown = ShownMethodName(name);
    return methodCopyNames.TryGetValue(shown, out var display) && display.IndexOf('<') is int open and > 0 ? display[..open] : shown;
  }

  // the method name as the program wrote it (without the __impl_ of the chain)
  static string ShownMethodName(string name) {
    while (name.StartsWith(ImplPrefix)) {
      name = name[ImplPrefix.Length..];
    }
    return name;
  }

  // fun R m(params) executeBefore { B } executeAfter { A } becomes two trait functions:
  //   __impl_m(params): what the class implements (its body is the default body of m, if any); private
  //   m(params) { B; var __result = __impl_m(params); A; return __result }: the default of the trait, the only m there is
  // so B and A run on every call of m, cannot be overridden, and A runs only when the implementation succeeded. The class writes
  // 'replace fun m', which RegisterMethods turns into __impl_m. Returns the node of __impl_m.
  Node SplitExecuteBlocks(ClassInfo trait, Node member) {
    string name = member.Text!;
    if (member.Children.Any(child => child.Kind == "GenericParams")) {
      throw NotSupported("executeBefore / executeAfter on a generic function", member);
    }
    var returns = member.Children.FirstOrDefault(child => child.Kind == "Returns");
    int results = returns?.Children.Count ?? 0;   // several results: each in its own local, read as m.result.0, m.result.1 (spec 11.2)
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
    } else if (results > 1) {
      var targets = Synthetic("Targets", member);
      for (int index = 0; index < results; index++) {
        targets.Add(Synthetic("Decl", member, "__result" + index).Add(Synthetic("Type", member, "var")));
      }
      block.Add(Synthetic("MultiAssign", member).Add(targets).Add(Synthetic("Values", member).Add(call)));
    } else {
      block.Add(Synthetic("Decl", member, "__result").Add(Synthetic("Type", member, "var")).Add(Synthetic("Init", member).Add(call)));
    }
    if (after != null) {
      foreach (var statement in after.Children) {
        block.Add(ResultReads(statement, WrittenMethodName(name), returns != null, results));   // m.result, also in a link of a chain
      }
    }
    if (results > 1) {
      var multiReturn = Synthetic("Return", member);
      for (int index = 0; index < results; index++) {
        multiReturn.Add(Synthetic("Name", member, "__result" + index));
      }
      block.Add(multiReturn);
    } else if (returns != null) {
      block.Add(Synthetic("Return", member).Add(Synthetic("Name", member, "__result")));
    }
    member.Children.RemoveAll(child => child.Kind is "Block" or "NoBody" or "ExecuteBefore" or "ExecuteAfter");
    member.Add(block);
    return implementation;
  }

  // m.result in an executeAfter block: the value the implementation returned, read-only
  Node ResultReads(Node node, string method, bool hasResult, int results = 1) {
    bool IsResult(Node n) => n is { Kind: "Member", Text: "result" } && n.Children[0] is { Kind: "Name" } owner && owner.Text == method;
    if (node.Kind is "Assign" or "PreInc" or "PostInc" && node.Children.Count > 0 && IsResult(node.Children[0])
        || node.Kind == "WritableAddressOf" && IsResult(node.Children[0]) || node.Kind == "Gift" && IsResult(node.Children[0])) {
      throw Error($"{method}.result is read-only", node);
    }
    if (results > 1 && node is { Kind: "Member" } part && part.Children.Count == 1 && IsResult(part.Children[0])) {
      // m.result.1 of a method with several results (spec 11.2)
      return int.TryParse(part.Text, out int index) && index >= 0 && index < results ? Synthetic("Name", node, "__result" + index)
        : throw Error($"{method} has {results} results: read them as {method}.result.0 to {method}.result.{results - 1}", node);
    }
    if (IsResult(node)) {
      return results > 1 ? throw Error($"{method} has {results} results: read each one, {method}.result.0 to {method}.result.{results - 1}", node)
        : hasResult ? Synthetic("Name", node, "__result") : throw Error($"{method} returns nothing: there is no {method}.result", node);
    }
    for (int i = 0; i < node.Children.Count; i++) {
      node.Children[i] = ResultReads(node.Children[i], method, hasResult, results);
    }
    return node;
  }

  // the property initializers and the constructor of a trait run in a function of the trait (__tinit), so they see its private members
  // a constructor with parameters is one more __tinit (overloads); the class names it with : Trait(...) (spec 11.1.1)
  void RegisterTraitInit(ClassInfo trait) {
    var statements = new List<Node>();
    foreach (var field in trait.Fields.Where(field => field.IsCounted && field.Init == null && IsCountedValueType(field.Type) && !IsNullable(field.Type))) {
      // &@i32 total: a counted value property starts with a cell of its own holding the zero value, as a field of a class (spec 12.7)
      string plain = BaseOf(field.Type);
      statements.Add(Assignment(field.Name, Synthetic("CValue", field.At, ZeroValue(plain)).Add(Synthetic("Type", field.At, plain)), field.At, false, false));
    }
    foreach (var field in trait.Fields.Where(field => field.Init != null)) {
      statements.Add(Assignment(field.Name, field.Init!, field.At, false, field.IsWeak));
    }
    if (trait.CtorNodes.Count == 0) {
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
      return;
    }
    if (trait.ChainsToSuper && trait.CtorNodes.Any(member => !member.Children.Any(child => child.Kind == "Chain"))) {
      throw NotSupported($"a trait whose constructors do not all chain to its parent trait ({DisplayName(trait.Name)}: write : {DisplayName(trait.Super!.Name)}(...) on each)", trait.Node);
    }
    foreach (var member in trait.CtorNodes) {
      var block = Synthetic("Block", member);
      if (member.Children.FirstOrDefault(child => child.Kind == "Chain") is Node superCall) {
        var called = superCall.Children[0];
        block.Add(Synthetic("ExprStmt", called).Add(Synthetic("CtorChain", called, called.Text).Add(called.Children[0])));   // the parent trait first
      }
      foreach (var statement in statements) {
        block.Add(CloneNode(statement));
      }
      // } atEnd { }: registered after the property initializers, before the body (spec 11.4)
      foreach (var statement in DestructorFragment(trait, member, member.Children.First(child => child.Kind == "Params"))) {
        block.Add(statement);
      }
      if (member.Children.FirstOrDefault(child => child.Kind == "Block") is Node body) {
        foreach (var statement in body.Children) {
          block.Add(statement);
        }
      }
      var node = Synthetic("Fun", member, "__tinit").Add(member.Children.First(child => child.Kind == "Params")).Add(block);
      FunSig f;
      try {
        f = RegisterFun(node, null, trait)!;
      } catch (CompileException e) {
        throw new CompileException(e.Message.Replace($"function '{trait.Name}{TraitInitSuffix}'", $"a constructor of the trait {trait.Name}"), e.Line);
      }
      f.Class = trait;
      trait.InitFun ??= f;
    }
  }

  const string TraitInitSuffix = ".__tinit";

  static bool DerivesFrom(ClassInfo sub, ClassInfo trait) {
    for (var up = sub.Super; up != null; up = up.Super) {
      if (up == trait) {
        return true;
      }
    }
    return false;
  }

  static int TraitDepth(ClassInfo trait) {
    int depth = 0;
    for (var up = trait.Super; up != null; up = up.Super) {
      depth++;
    }
    return depth;
  }

  // for every class: what answers each method of each trait it implements
  void BuildImpls(ClassInfo cls) {
    CheckCopyClashes(cls);
    foreach (var trait in AllTraits(cls)) {
      var methods = new List<FunSig>();
      foreach (var method in trait.TraitMethods) {
        FunSig? implementation = null;
        // replace fun ... Trait.name(): the implementation written for this trait (or a sub-trait of it) wins (spec 11.2)
        string methodBare = method.Name[(method.Name.IndexOf('.') + 1)..];
        for (var c = cls; c != null && implementation == null; c = c.Parent) {
          foreach (var named in AllTraits(cls).Where(t => t == trait || DerivesFrom(t, trait))) {
            if (implementation == null && funs.TryGetValue(c.Name + "." + named.Name + "." + methodBare, out var own)) {
              implementation = own.FirstOrDefault(g => !g.IsStatic && ParamTypes(g).SequenceEqual(ParamTypes(method)) && g.Ret == method.Ret);
            }
          }
        }
        for (var c = cls; c != null && implementation == null; c = c.Parent) {
          if (funs.TryGetValue(c.Name + "." + method.Name[(method.Name.IndexOf('.') + 1)..], out var candidates)) {
            implementation = candidates.FirstOrDefault(g => !g.IsStatic && ParamTypes(g).SequenceEqual(ParamTypes(method)) && g.Ret == method.Ret);
          }
        }
        if (implementation == null) {
          // a sub-trait of this trait, among the traits of the class, that answers the method: a link of an executeBefore / executeAfter
          // chain (spec 11.2); the most specific one wins
          string bare = method.Name[(method.Name.IndexOf('.') + 1)..];
          foreach (var sub in AllTraits(cls).Where(sub => sub != trait && DerivesFrom(sub, trait)).OrderByDescending(TraitDepth)) {
            if (sub.TraitMethods.FirstOrDefault(g => g.Name == sub.Name + "." + bare && !g.IsAbstract) is FunSig link) {
              implementation = link;
              break;
            }
          }
        }
        if (implementation == null && !method.IsAbstract) {
          implementation = method;                 // the default implementation of the trait
        }
        if (implementation == null) {
          string shown = method.Name[(method.Name.IndexOf('.') + 1)..];
          throw Error($"class '{cls.Name}' must implement {DisplayName(trait.Name)}.{MethodCopyName(ShownMethodName(shown))}", cls.Node);
        }
        methods.Add(implementation);
      }
      cls.Impls.Add(new ImplInfo(trait, methods));
    }
  }

  // two copies of one generic trait that the header writes without an alias (class Both : Source<i32>, Source<string>): fine until
  // they clash, a function with the same parameters and another result, or a public property with the same name; then one needs an
  // alias, so that the class and its users can name it (spec 11.3.2, 16)
  void CheckCopyClashes(ClassInfo cls) {
    var plain = cls.Traits.Where(trait => classInstanceArgs.ContainsKey(trait.Name) && !cls.AliasedTraits.Contains(trait.Name))
      .GroupBy(trait => classInstanceArgs[trait.Name].Template).Where(group => group.Count() > 1);
    foreach (var group in plain) {
      var copies = group.ToList();
      for (int i = 0; i < copies.Count; i++) {
        for (int j = i + 1; j < copies.Count; j++) {
          string? clash = copies[i].TraitMethods.FirstOrDefault(m => copies[j].TraitMethods.Any(o => Bare(o) == Bare(m)
              && ParamTypes(o).SequenceEqual(ParamTypes(m)) && o.Ret != m.Ret)) is FunSig method ? Bare(method)
            : copies[i].Fields.FirstOrDefault(f => !f.IsStatic && f.Visibility != Vis.Private && copies[j].Fields.Any(o => o.Name == f.Name))?.Name;
          if (clash != null) {
            throw Error($"{DisplayName(cls.Name)} implements {DisplayName(copies[i].Name)} and {DisplayName(copies[j].Name)}, and '{clash}' clashes: "
              + $"give one an alias, alias {DisplayName(copies[j].Name)} as ... , and write the alias in the header (spec 11.3.2)", cls.Node);
          }
        }
      }
    }
  }

  static string Bare(FunSig f) => f.Name[(f.Name.LastIndexOf('.') + 1)..];

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
    string ctype = field.IsCounted || field.IsWeak ? "df_rc*" : CType(field.Type);   // a counted property holds its counting block (spec 12.7)
    return $"(*({{ void* {temp} = (void*)({objectCode}); ({ctype}*)((char*){temp} + df_impl_of({temp}, &df_trait_{trait.Name})->offsets[{index}]); }}))";
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
    var codes = ArgCodes(chosen, args, scope, at, out string prefix);
    codes.Insert(0, "(void*)self");
    string call = $"{chosen.CName}({string.Join(", ", codes)})";
    return ArgsFirst(new Expr(UserCallCode(chosen, call), chosen.Ret, IsClass(chosen.Ret) && chosen.ReturnsOwned), prefix);
  }

  // the trait a qualifier names for a class (spec 11.2, 11.3.2): a trait, an alias of a copy of a generic trait (in this file, spec 16),
  // or the name of a generic trait: its only copy, or the one the header writes without an alias; null when it names no trait
  ClassInfo? QualifiedTrait(ClassInfo cls, string written, Node at) {
    if (traits.TryGetValue(written, out var direct)) {
      return direct;
    }
    if (typeAliases.TryGetValue(written, at, out var aliased) && traits.TryGetValue(aliased, out var viaAlias)) {
      return viaAlias;
    }
    if (!genericClasses.TryGetValue(written, out var template) || !template.IsTrait) {
      return null;
    }
    var copies = AllTraits(cls).Where(trait => classInstanceArgs.TryGetValue(trait.Name, out var copy) && copy.Template == template).ToList();
    if (copies.Count <= 1) {
      return copies.FirstOrDefault();
    }
    var plain = copies.Where(trait => !AliasedIn(cls, trait)).ToList();
    return plain.Count == 1 ? plain[0]
      : throw Error($"{DisplayName(cls.Name)} implements {written} more than once: name the copy with an alias of it (alias {DisplayName(copies[^1].Name)} as ...)", at);
  }

  // a copy of a generic trait that the header of the class (or of a parent) writes through an alias
  static bool AliasedIn(ClassInfo cls, ClassInfo trait) {
    for (var c = cls; c != null; c = c.Parent) {
      if (c.AliasedTraits.Contains(trait.Name)) {
        return true;
      }
    }
    return false;
  }

  // x.TraitName: the object seen as that trait
  Expr? TraitView(Expr obj, string traitName, Node at) {
    if (!traits.TryGetValue(traitName, out var trait)) {
      if (!classes.TryGetValue(obj.Type, out var objClass) || QualifiedTrait(objClass, traitName, at) is not ClassInfo found) {
        return null;
      }
      trait = found;   // b.SourceString, b.Source: a copy of a generic trait (spec 11.3.2)
      traitName = found.Name;
    }
    if (!IsSubtype(obj.Type, traitName)) {
      throw Error($"{obj.Type} does not implement the trait {traitName}", at);
    }
    return new Expr(obj.Code, traitName);
  }

  // ---------------------------------------------------------------- construction

  // the traits this class introduces, parent traits first: a call of TraitName.__tinit() each (property initializers, then the constructor body).
  // named: the trait constructors a constructor of the class calls in its chain (: Trait(args)); null for the primary constructor, which
  // can only run the constructors without parameters
  List<Node> TraitInitStatements(ClassInfo cls, Node at, Dictionary<string, Node>? named) {
    var statements = new List<Node>();
    var introduced = IntroducedTraits(cls);
    foreach (var sub in introduced.Where(sub => sub.ChainsToSuper)) {
      if (!introduced.Contains(sub.Super!)) {
        throw Error($"the trait {DisplayName(sub.Name)} initializes {DisplayName(sub.Super!.Name)}, which {DisplayName(cls.Name)} has already from its parent class: the parent trait is initialized once (spec 11.2)", at);
      }
      if (named != null && named.TryGetValue(sub.Super!.Name, out var twice)) {
        throw Error($"the constructor of {DisplayName(sub.Name)} calls the constructor of {DisplayName(sub.Super.Name)}: the parent trait is initialized once, do not call it here (spec 11.2)", twice);
      }
    }
    foreach (var trait in introduced.Where(trait => trait.InitFun != null && !introduced.Any(sub => sub.ChainsToSuper && sub.Super == trait))) {
      if (named != null && named.TryGetValue(trait.Name, out var call)) {
        statements.Add(Synthetic("ExprStmt", call).Add(Synthetic("CtorChain", call, trait.Name).Add(call.Children[0])));
        continue;
      }
      if (!funs[trait.Name + TraitInitSuffix].Any(init => init.Params.Count == 0)) {
        throw Error(named == null
          ? $"the constructor of the trait {trait.Name} takes parameters: {cls.Name} must call it from a constructor in its body, with : {trait.Name}(...)"
          : $"a constructor of {cls.Name} must call the constructor of the trait {trait.Name} (it takes parameters): write : {trait.Name}(...) after its parameters", at);
      }
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
