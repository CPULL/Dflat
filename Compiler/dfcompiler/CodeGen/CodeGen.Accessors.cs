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
        var methods = PropertyMethods(decl, accessors);
        body.Children.InsertRange(i + 1, methods);
        i += methods.Count;
      }
    }
  }

  List<Node> PropertyMethods(Node decl, Node accessors) {
    string name = decl.Text!;
    var mods = decl.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
    if (mods.Contains("static")) {
      throw NotSupported("Static properties with accessor blocks", decl);
    }
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
    if (mods.Contains("readonly")) {
      if (setter != null) {
        throw Error($"'{name}' is readonly: it has no W:", setter);
      }
      canWrite = false;
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
      AddVisibility(fun, getter, visibility);
      fun.Add(Synthetic("Returns", decl).Add(CopyTree(typeNode))).Add(Synthetic("Params", decl)).Add(RawFields(block, name)).Add(Synthetic("AccessorFun", decl));
      methods.Add(fun);
    }
    if (canWrite) {
      var block = setter?.Children.First(child => child.Kind == "Block")
        ?? Synthetic("Block", decl).Add(Synthetic("Assign", decl, "=").Add(Synthetic("Name", decl, "field")).Add(Synthetic("Name", decl, "value")));
      var fun = Synthetic("Fun", decl, SetterName(name));
      AddVisibility(fun, setter, visibility);
      var value = Synthetic("Param", decl, "value").Add(CopyTree(typeNode));
      fun.Add(Synthetic("Params", decl).Add(value)).Add(RawFields(block, name)).Add(Synthetic("AccessorFun", decl));
      methods.Add(fun);
    }
    return methods;
  }

  // the visibility of the accessor (written before R: or W:), else the one of the property
  static void AddVisibility(Node fun, Node? accessor, string visibility) {
    string? own = accessor?.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text;
    string words = own ?? visibility;
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
    return new Expr($"self->{field.CName}", field.Type);
  }

  LValueInfo RawFieldLValue(Node n) {
    var field = currentClass == null ? null : FindField(currentClass, n.Text!);
    if (field == null) {
      throw Error("'field' is used only inside the accessor blocks of a property", n);
    }
    return new LValueInfo($"self->{field.CName}", field.Type, false, null, true, field.IsWeak);
  }

  // an accessor with its own visibility (private W:): said with the name of the property, not of its method
  void CheckAccessorVisible(Expr obj, FieldInfo property, string method, string block, Node at) {
    var accessor = LookupMethods(ObjInfo(obj.Type), method).FirstOrDefault();
    if (accessor is { Visibility: Vis.Private } && !(currentClass != null && (currentClass == property.Owner || IsSubclass(currentClass.Name, property.Owner.Name)))) {
      throw Error($"'{property.Name}' cannot be {(block == "R:" ? "read" : "assigned")} here: its {block} is private", at);
    }
  }

  Expr PropertyGet(Expr obj, FieldInfo property, Scope scope, Node at) {
    if (!property.CanRead) {
      throw Error($"'{property.Name}' cannot be read: it has no R:", at);
    }
    CheckAccessorVisible(obj, property, GetterName(property.Name), "R:", at);
    return ObjectCall(obj, GetterName(property.Name), new List<Node>(), scope, at)
      ?? throw Error($"internal error: no getter for '{property.Name}'", at);
  }

  // x (a property of this class) or o.x: the object and the property; null when the target is not a property
  (Expr Obj, FieldInfo Property)? PropertyTarget(Node target, Scope scope) {
    if (target.Kind == "Name" && scope.Find(target.Text!) == null && currentClass != null && scope.Find("this") != null
        && FindField(currentClass, target.Text!) is { HasAccessors: true } own) {
      return (new Expr("self", currentClass.Name), own);
    }
    var receiver = target.Kind == "Member" && target.Children.Count == 1 ? target.Children[0] : null;
    // a name that is not a value (Shape.count, a static through its class) is never an object with properties
    if (receiver != null && IsSimpleReceiver(receiver) && !(receiver.Kind == "Name" && scope.Find(receiver.Text!) == null && receiver.Text != "this"
        && (currentClass == null || FindField(currentClass, receiver.Text!) == null))) {
      var obj = ExprOf(receiver, scope);
      if (IsClass(obj.Type) && FindField(ObjInfo(obj.Type), target.Text!) is { HasAccessors: true } property) {
        return (obj, property);
      }
    }
    return null;
  }

  // target op value through the setter (op= reads the property through the getter first)
  string PropertyAssign(Node s, Node targetNode, Expr obj, FieldInfo property, Scope scope) {
    string op = s.Text!;
    if (op is "_=" or "??=") {
      throw NotSupported($"{op} on a property with accessor blocks", s);
    }
    Node value = op == "=" ? s.Children[1] : Synthetic("Binary", s, op[..^1]).Add(targetNode).Add(s.Children[1]);
    return PropertySet(obj, property, value, scope, s).Code;
  }

  Expr PropertySet(Expr obj, FieldInfo property, Node value, Scope scope, Node at) {
    if (!property.CanWrite) {
      throw Error($"'{property.Name}' cannot be assigned: it has no W:", at);
    }
    CheckAccessorVisible(obj, property, SetterName(property.Name), "W:", at);
    return ObjectCall(obj, SetterName(property.Name), new List<Node> { value }, scope, at)
      ?? throw Error($"internal error: no setter for '{property.Name}'", at);
  }

  // x++ and the others on a property: the getter, then the setter with the new value
  Expr PropertyIncDec(Node n, Expr obj, FieldInfo property, Scope scope) {
    var targetNode = n.Children[0];
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
