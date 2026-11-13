using dfparser;

namespace dfcompiler;

// Accessor blocks (spec 11.1.4): i32 level { R: { field } private W: { field = value } }. Before the classes are registered, each property
// gets two methods, get_<name> and set_<name> (value), from its blocks or from the defaults ({ field } and { field = value }); inside them
// field is a RawField node, the storage itself. Reading the property calls the getter, assigning it (=, op=, ++, --, a multiple
// assignment) calls the setter. What an accessor lets out is a Panic: it has no error part, as the atEnd of a class.
public sealed partial class CodeGen {
  static readonly string[] VisibilityWords = { "public", "internal", "private", "unrestricted" };

  // the C-safe method names of the property name (a field of a group is "pos.x")
  static string GetterName(string property) => "get_" + property.Replace(".", "__");
  static string SetterName(string property) => "set_" + property.Replace(".", "__");

  void LowerAccessors(Node program) {
    GiveReplacedTraitPropertiesAccessors(program);
    foreach (var type in program.Children.Where(statement => statement.Kind is "Class" or "Trait")) {
      var body = type.Children.FirstOrDefault(child => child.Kind == "Block");
      if (body == null) {
        continue;
      }
      for (int i = 0; i < body.Children.Count; i++) {
        var decl = body.Children[i];
        var accessors = decl.Kind == "Decl" ? decl.Children.FirstOrDefault(child => child.Kind == "Accessors") : null;
        if (accessors == null) {
          continue;
        }
        if (type.Kind == "Trait") {
          throw NotSupported("Accessor blocks in traits", accessors);
        }
        var methods = PropertyMethods(decl, accessors, program, type);
        body.Children.InsertRange(i + 1, methods);
        i += methods.Count;
      }
    }
  }

  // the classes and traits a class or a trait lists in its header (its parent class and its traits), from the program
  static IEnumerable<Node> BaseNodes(Node program, Node type) =>
    (type.Children.FirstOrDefault(child => child.Kind == "Bases")?.Children ?? new List<Node>())
      .Select(baseType => program.Children.FirstOrDefault(statement => statement.Kind is "Class" or "Trait" && statement.Text == baseType.Text))
      .Where(found => found != null)!;

  // the declaration of a property in the classes and traits above a type, the nearest first
  static (Node Owner, Node Decl)? InheritedProperty(Node program, Node type, string name) {
    var seen = new HashSet<Node> { type };
    var queue = new Queue<Node>(BaseNodes(program, type));
    while (queue.Count > 0) {
      var current = queue.Dequeue();
      if (!seen.Add(current)) {
        continue;
      }
      var decl = current.Children.FirstOrDefault(child => child.Kind == "Block")?.Children.FirstOrDefault(member => member.Kind == "Decl" && member.Text == name);
      if (decl != null) {
        return (current, decl);
      }
      foreach (var next in BaseNodes(program, current)) {
        queue.Enqueue(next);
      }
    }
    return null;
  }

  // replace public i32 sides { R: ... } on a property of a trait (spec 11.2): the trait property gets default accessors (get_ and set_ on
  // its storage), so the code of the trait, the values of the trait type and the other classes reach it through a call and the class that
  // replaces it answers. A trait property that no class replaces stays a plain field: a direct memory access.
  void GiveReplacedTraitPropertiesAccessors(Node program) {
    var done = new HashSet<Node>();
    foreach (var cls in program.Children.Where(statement => statement.Kind == "Class").ToList()) {
      var body = cls.Children.FirstOrDefault(child => child.Kind == "Block");
      foreach (var decl in body?.Children.Where(member => member.Kind == "Decl" && member.Children.Any(child => child.Kind == "Accessors")).ToList() ?? new List<Node>()) {
        var mods = decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
        if (!mods.Contains("replace") || InheritedProperty(program, cls, decl.Text!) is not var (owner, traitDecl) || owner.Kind != "Trait"
            || traitDecl.Children.Any(child => child.Kind is "Accessors" or "Property") || !done.Add(traitDecl)) {
          continue;
        }
        if (IsCountedDecl(traitDecl) || traitDecl.Children.Any(child => child.Kind == "Weak")) {
          throw NotSupported("Replacing a counted or weak property of a trait", decl);
        }
        var traitBody = owner.Children.First(child => child.Kind == "Block");
        var methods = PropertyMethods(traitDecl, Synthetic("Accessors", traitDecl), program, owner);
        traitBody.Children.InsertRange(traitBody.Children.IndexOf(traitDecl) + 1, methods);
      }
    }
  }

  // override public i32 level { R: { field + 1 } }: the property of a parent class, replaced whole (spec 11.1.4). The access is the one of
  // the parent's property; an accessor the override does not write takes its default, not the parent's code. Null: no such property.
  static (bool CanRead, bool CanWrite)? ParentPropertyAccess(Node program, Node type, string name) {
    // the parent classes and the traits, nearest first; a replaced trait property already has its accessors (GiveReplacedTraitPropertiesAccessors)
    if (InheritedProperty(program, type, name) is not var (_, decl)) {
      return null;
    }
    if (decl.Children.FirstOrDefault(child => child.Kind == "Property") is Node done) {
      return (done.Text!.Contains('r'), done.Text!.Contains('w'));
    }
    var mods = decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
    if (decl.Children.FirstOrDefault(child => child.Kind == "Accessors") is Node blocks) {
      bool getter = blocks.Children.Any(child => child.Kind == "Getter");
      bool setter = blocks.Children.Any(child => child.Kind == "Setter");
      bool none = !getter && !setter;
      bool reads = !mods.Contains("writeonly") && (none || getter || mods.Contains("readwrite") || mods.Contains("readonly"));
      bool writes = mods.Contains("readonly") ? setter : none || setter || mods.Contains("readwrite");
      return (reads, writes);
    }
    return null;   // a plain field of a class: read and written directly, nothing to replace
  }

  List<Node> PropertyMethods(Node decl, Node accessors, Node program, Node owner) {
    string name = decl.Text!;
    var mods = decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
    if (mods.Contains("const")) {
      throw Error("a const field has no accessor blocks", decl);
    }
    if (decl.Children.Any(child => child.Kind == "Weak")) {
      throw NotSupported("Weak properties with accessor blocks", decl);
    }
    var getter = accessors.Children.FirstOrDefault(child => child.Kind == "Getter");
    var setter = accessors.Children.FirstOrDefault(child => child.Kind == "Setter");
    bool none = getter == null && setter == null;
    bool canRead = none || getter != null || mods.Contains("readwrite");
    bool canWrite = none || setter != null || mods.Contains("readwrite");
    bool readonlyWriter = false;
    if (mods.Contains("readonly")) {
      // readonly with a W: block: read from outside, written by the class itself (spec 11.1.4)
      readonlyWriter = setter != null;
      canWrite = setter != null;
      canRead = true;
    }
    bool isOverride = mods.Contains("replace");
    if (isOverride) {
      if (decl.Children.Any(child => child.Kind is "Init")) {
        throw Error($"the replacement of '{name}' has no start value: the storage is the parent's", decl);
      }
      var parentAccess = ParentPropertyAccess(program, owner, name)
        ?? throw Error($"'replace' on '{name}': no parent class has a property '{name}' with accessor blocks (spec 11.1.4)", decl);
      if (getter != null && !parentAccess.CanRead || setter != null && !parentAccess.CanWrite) {
        throw Error($"the replacement of '{name}' cannot add {(getter != null && !parentAccess.CanRead ? "an R: block" : "a W: block")}: the parent's property has none", decl);
      }
      (canRead, canWrite) = parentAccess;   // the whole property: the accessors not written take their defaults
    }
    if (mods.Contains("writeonly")) {
      if (getter != null) {
        throw Error($"'{name}' is writeonly: it has no R:", getter);
      }
      canRead = false;
    }
    decl.Children.Remove(accessors);
    decl.Add(Synthetic("Property", decl, (canRead ? "r" : "") + (canWrite ? "w" : "")));

    var typeNode = decl.Children.First(child => TypeNodeKinds.Contains(child.Kind));
    string visibility = string.Join(" ", mods.Where(mod => VisibilityWords.Contains(mod)));
    var methods = new List<Node>();
    if (canRead) {
      var block = getter?.Children.First(child => child.Kind == "Block")
        ?? Synthetic("Block", decl).Add(Synthetic("ExprStmt", decl).Add(Synthetic("Name", decl, "field")));
      var fun = Synthetic("Fun", decl, GetterName(name));
      AddVisibility(fun, getter, visibility, isOverride, mods.Contains("static"));
      var returns = Synthetic("Returns", decl).Add(CopyTree(typeNode));
      if (decl.Children.Any(child => child.Kind == "Counted")) {
        returns.Text = decl.Children.Any(child => child.Kind == "ReadWrite") ? "counted-rw" : "counted";   // R: gives a usage (spec 11.1.4)
      }
      fun.Add(returns).Add(Synthetic("Params", decl)).Add(RawFields(block, name)).Add(Synthetic("AccessorFun", decl));
      methods.Add(fun);
    }
    if (canWrite) {
      var block = setter?.Children.First(child => child.Kind == "Block")
        ?? Synthetic("Block", decl).Add(Synthetic("Assign", decl, "=").Add(Synthetic("Name", decl, "field")).Add(Synthetic("Name", decl, "value")));
      var fun = Synthetic("Fun", decl, SetterName(name));
      AddVisibility(fun, setter, readonlyWriter && setter?.Children.Any(child => child.Kind == "Mods") != true ? "private" : visibility, isOverride, mods.Contains("static"));
      var value = Synthetic("Param", decl, "value").Add(CopyTree(typeNode));
      if (decl.Children.Any(child => child.Kind == "Counted")) {
        value.Add(Synthetic("Counted", decl));   // W: takes a usage (spec 11.1.4)
        if (decl.Children.Any(child => child.Kind == "ReadWrite")) {
          value.Add(Synthetic("ReadWrite", decl));
        }
      }
      fun.Add(Synthetic("Params", decl).Add(value)).Add(RawFields(block, name)).Add(Synthetic("AccessorFun", decl));
      methods.Add(fun);
    }
    return methods;
  }

  // the visibility of the accessor (written before R: or W:), else the one of the property
  static void AddVisibility(Node fun, Node? accessor, string visibility, bool isOverride = false, bool isStatic = false) {
    string? own = accessor?.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text;
    string words = isOverride ? "replace" : own ?? visibility;   // a replacement takes the visibility of the parent's accessor
    if (isStatic) {
      words += " static";   // the accessors of a static property are static functions (spec 11.1.2)
    }
    words = words.Trim();
    if (words.Length > 0) {
      fun.Add(Synthetic("Mods", fun, words));
    }
  }

  // field inside the blocks of the property is its storage
  static Node RawFields(Node node, string property) {
    if (node.Kind == "Name" && node.Text == "field") {
      node.Kind = "RawField";
      node.Text = property;
      return node;
    }
    if (node.Kind is not ("Fun" or "Lambda")) {
      foreach (var child in node.Children) {
        RawFields(child, property);
      }
    }
    return node;
  }

  // ---------------------------------------------------------------- reads and writes

  // the storage of a property, inside its own accessors
  Expr RawFieldExpr(Node n) {
    var field = currentClass == null ? null : FindField(currentClass, n.Text!);
    if (field == null) {
      throw Error("'field' is used only inside the accessor blocks of a property", n);
    }
    if (field.IsStatic) {
      return new Expr(field.CName, field.Type);   // the storage of a static property
    }
    if (field.IsCounted) {
      return CountedFieldRead(new Expr($"self->{field.CName}", field.Type), field, n);   // the object of a counted storage
    }
    if (currentClass!.IsTrait) {
      return new Expr(TraitFieldCode("self", field), field.Type);   // the default accessors of a replaced trait property (spec 11.2)
    }
    return new Expr($"self->{field.CName}", field.Type);
  }

  LValueInfo RawFieldLValue(Node n) {
    var field = currentClass == null ? null : FindField(currentClass, n.Text!);
    if (field == null) {
      throw Error("'field' is used only inside the accessor blocks of a property", n);
    }
    string place = field.IsStatic ? field.CName : currentClass!.IsTrait ? TraitFieldCode("self", field) : $"self->{field.CName}";   // a static storage, a replaced trait property (spec 11.2)
    return new LValueInfo(place, field.Type, false, null, true, field.IsWeak);
  }

  // an accessor with its own visibility (private W:): said with the name of the property, not of its method
  void CheckAccessorVisible(Expr obj, FieldInfo property, string method, string block, Node at) {
    var accessor = LookupMethods(ObjInfo(obj.Type), method).FirstOrDefault();
    if (accessor is { Visibility: Vis.Private } && !(currentClass != null && (currentClass == property.Owner || IsSubclass(currentClass.Name, property.Owner.Name)))) {
      throw Error($"'{property.Name}' cannot be {(block == "R:" ? "read" : "assigned")} here: its {block} is private", at);
    }
  }

  // set by a counted receiver (@Dog b = k.boss, an @T argument): the next counted property read gives its usage instead of a temporary one
  bool wantCountedUsage;

  Expr PropertyGet(Expr obj, FieldInfo property, Scope scope, Node at) {
    if (!property.CanRead) {
      throw Error($"'{property.Name}' cannot be read: it has no R:", at);
    }
    CheckAccessorVisible(obj, property, GetterName(property.Name), "R:", at);
    bool wantUsage = wantCountedUsage;
    wantCountedUsage = false;
    var got = (obj.Code == "" ? MethodCall(ObjInfo(obj.Type), GetterName(property.Name), null, false, new List<Node>(), scope, at)
        : ObjectCall(obj, GetterName(property.Name), new List<Node>(), scope, at))
      ?? throw Error($"internal error: no getter for '{property.Name}'", at);
    if (!property.IsCounted || wantUsage || !IsCountedType(got.Type)) {
      return got;
    }
    // k.boss.name: the usage R: gives lives for the statement (spec 11.1.4)
    string usage = NewTemp();
    string inner = CountedBase(got.Type);
    string read = IsCountedValueType(inner) ? CellRead(usage, inner) : $"DF_RC_OBJ({CType(inner)}, {usage})";
    return new Expr($"({{ df_rc* {usage} = {got.Code}; df_tmpf({usage}, df_rc_release_tmp); {read}; }})", inner);
  }

  // make().name, make().bark(): the usage a call gives is kept for the statement and released at its end; the value is what it holds
  Expr CountedResultUse(Expr counted) {
    string usage = NewTemp();
    string inner = CountedBase(counted.Type);
    string read = IsCountedValueType(inner) ? CellRead(usage, inner) : $"DF_RC_OBJ({CType(inner)}, {usage})";
    return new Expr($"({{ df_rc* {usage} = {counted.Code}; df_tmpf({usage}, df_rc_release_tmp); {read}; }})", inner);
  }

  // x (a property of this class) or o.x: the object and the property; null when the target is not a property
  (Expr Obj, FieldInfo Property)? PropertyTarget(Node target, Scope scope) {
    // a static property: count inside its class, C.count outside (the class is the receiver, its code is empty)
    if (target.Kind == "Name" && scope.Find(target.Text!) == null && currentClass != null && FindField(currentClass, target.Text!) is { HasAccessors: true, IsStatic: true } ownStatic) {
      return (new Expr("", ownStatic.Owner.Name), ownStatic);
    }
    if (target.Kind == "Member" && target.Children.Count == 1 && target.Children[0] is { Kind: "Name" } owner && scope.Find(owner.Text!) == null
        && classes.TryGetValue(owner.Text!, out var ownerClass) && FindField(ownerClass, target.Text!) is { HasAccessors: true, IsStatic: true } staticProperty) {
      return (new Expr("", ownerClass.Name), staticProperty);
    }
    if (target.Kind == "Name" && scope.Find(target.Text!) == null && currentClass != null && scope.Find("this") != null
        && FindField(currentClass, target.Text!) is { HasAccessors: true } own) {
      return (new Expr("self", currentClass.Name), own);
    }
    var receiver = target.Kind == "Member" && target.Children.Count == 1 ? target.Children[0] : null;
    // a name that is not a value (Shape.count, a static through its class) is never an object with properties
    // any other receiver (blocks[i].pos.x) is a property too, so the setter is never skipped; it is evaluated once per use
    if (receiver != null && !(receiver.Kind == "Name" && scope.Find(receiver.Text!) == null && receiver.Text != "this" && !globals.ContainsKey(receiver.Text!)
        && (currentClass == null || FindField(currentClass, receiver.Text!) == null))) {
      var obj = ExprOf(receiver, scope);
      if (IsClass(obj.Type) && FindField(ObjInfo(obj.Type), target.Text!) is { HasAccessors: true } property) {
        return (obj, property);
      }
    }
    return null;
  }

  // op= and ++ use the receiver twice (getter, then setter): only a name or a chain of members, which can be read again
  void CheckReceiverReadTwice(Node target, Node at) {
    if (target.Kind == "Member" && target.Children.Count == 1 && !IsSimpleReceiver(target.Children[0])) {
      throw NotSupported("op= and ++ / -- on a property through an expression (put the object in a variable first)", at);
    }
  }

  // target op value through the setter (op= reads the property through the getter first)
  string PropertyAssign(Node s, Node targetNode, Expr obj, FieldInfo property, Scope scope) {
    string op = s.Text!;
    if (op == "_=") {
      throw NotSupported($"{op} on a property with accessor blocks", s);
    }
    if (op == "??=") {
      CheckReceiverReadTwice(targetNode, s);
      var current = PropertyGet(obj, property, scope, s);
      return $"({{ if ({EqualsCode(current, new Expr("0", "null"), s)}) {{ {PropertySet(obj, property, s.Children[1], scope, s).Code}; }} }})";
    }
    if (op != "=") {
      CheckReceiverReadTwice(targetNode, s);
    }
    Node value = op == "=" ? s.Children[1] : Synthetic("Binary", s, op[..^1]).Add(targetNode).Add(s.Children[1]);
    return PropertySet(obj, property, value, scope, s).Code;
  }

  Expr PropertySet(Expr obj, FieldInfo property, Node value, Scope scope, Node at) {
    if (!property.CanWrite) {
      throw Error($"'{property.Name}' cannot be assigned: it has no W:", at);
    }
    if (obj.Code is "self" or "" && currentFun?.LambdaReadOnlyMembers.Contains(property.Name) == true) {
      throw Error($"'{property.Name}' is captured read-only by the lambda: list it as &{property.Name} to change it (spec 13.3.1)", at);
    }
    CheckAccessorVisible(obj, property, SetterName(property.Name), "W:", at);
    return (obj.Code == "" ? MethodCall(ObjInfo(obj.Type), SetterName(property.Name), null, false, new List<Node> { value }, scope, at)
        : ObjectCall(obj, SetterName(property.Name), new List<Node> { value }, scope, at))
      ?? throw Error($"internal error: no setter for '{property.Name}'", at);
  }

  // x++ and the others on a property: the getter, then the setter with the new value
  Expr PropertyIncDec(Node n, Expr obj, FieldInfo property, Scope scope) {
    var targetNode = n.Children[0];
    CheckReceiverReadTwice(targetNode, n);
    if (!IsNumeric(property.Type)) {
      throw Error($"++ and -- need a number, not {property.Type}", n);
    }
    string op = n.Kind is "PostInc" or "PreInc" ? "+" : "-";
    var before = PropertyGet(obj, property, scope, n);
    string set = PropertySet(obj, property, Synthetic("Binary", n, op).Add(targetNode).Add(Synthetic("Int", n, "1")), scope, n).Code;
    string old = NewTemp();
    string result = n.Kind is "PostInc" or "PostDec" ? old : PropertyGet(obj, property, scope, n).Code;
    return new Expr($"({{ {CType(property.Type)} {old} = {before.Code}; (void){old}; {set}; {result}; }})", property.Type);
  }

  // a value already computed in C, given to a function that takes nodes (the setter in a multiple assignment)
  static Node CValue(Expr value, Node at) {
    return Synthetic("CValue", at, value.Code).Add(Synthetic("Type", at, value.Type));
  }
}
