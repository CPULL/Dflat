using System.Text;
using dfparser;

namespace dfcompiler;

// A struct of the program (spec 11.1.5): its fields in the order they are written
sealed class StructInfo {
  public StructInfo(string name, Node node) {
    Name = name;
    Node = node;
  }

  public string Name { get; }
  public Node Node { get; }
  public string CName => "s_" + Name;
  public List<StructField> Fields { get; } = new();
  public string? Layout { get; set; }   // fixed or compact (spec 11.1.5.6); null: the compiler's layout
  public bool Emitted { get; set; }    // its C typedef is written
  public bool Emitting { get; set; }   // its C typedef is being written (a struct that holds itself)
}

// Buffer: the N of an arr<T>[N] field, whose items are stored inline (spec 11.1.5.4); null for the other fields
sealed record StructField(string Name, string Type, Node? Init, Node At, long? Buffer = null);

// Structs (spec 11.1.5), step 1: declaration, creation by name or by position, zero and start values, fields read and written,
// locals, parameters and results. A struct is a C struct (typedef struct s_Name { ... } s_Name) passed and returned by value;
// its fields are f_<name>. Methods, objects inside, buffers, layout tags, transmute, ==, hash and printing come in later steps.
public sealed partial class CodeGen {
  readonly Dictionary<string, StructInfo> structs = new();

  bool IsStruct(string type) => structs.ContainsKey(type);

  void DeclareStructs(Node program) {
    foreach (var node in program.Children.Where(child => child.Kind == "Struct")) {
      string name = node.Text!;
      if (structs.ContainsKey(name) || IsKnownType(name) || name == "Exception" || IsSetName(name) || genericClasses.ContainsKey(name)) {
        throw Error($"the name '{name}' is already used by a type", node);
      }
      structs[name] = new StructInfo(name, node);
    }
    CheckNestedStructs(program, true);
  }

  // a struct is declared at the top level of a file
  void CheckNestedStructs(Node node, bool topLevel) {
    foreach (var child in node.Children) {
      if (child.Kind == "Struct") {
        if (!topLevel) {
          throw Error("a struct is declared at the top level, not inside a class, a function or a block", child);
        }
        continue;
      }
      CheckNestedStructs(child, false);
    }
  }

  void RegisterStructs() {
    foreach (var info in structs.Values) {
      var node = info.Node;
      info.Layout = node.Children.FirstOrDefault(child => child.Kind == "Layout")?.Text;   // spec 11.1.5.6
      foreach (var member in node.Children) {
        if (member.Kind != "Field") {
          continue;
        }
        if (info.Fields.Any(field => field.Name == member.Text)) {
          throw Error($"the struct '{info.Name}' already has a field '{member.Text}'", member);
        }
        string type = ResolveType(member.Children[0]);
        var init = member.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
        if (member.Children.FirstOrDefault(child => child.Kind == "Size") is Node size) {
          // arr<T>[N]: N items stored inline (spec 11.1.5.4)
          long length = BufferLength(size.Children[0]);
          info.Fields.Add(new StructField(member.Text!, type, init, member, length));
          continue;
        }
        CheckStructFieldType(type, member);
        info.Fields.Add(new StructField(member.Text!, type, init, member));
      }
      if (info.Fields.Count == 0) {
        throw Error($"the struct '{info.Name}' has no fields", node);
      }
    }
    // the items of a buffer are plain values: checked once every struct knows its fields
    foreach (var field in structs.Values.SelectMany(info => info.Fields).Where(field => field.Buffer != null)) {
      string elem = ArrElem(field.Type);
      bool plain = IsNumeric(elem) || IsEnum(elem) || elem is "bool" or "char" or "addr" || IsTimeType(elem) && elem != "lapse"
        || IsStruct(elem) && !StructHoldsObjects(elem);
      if (!plain) {
        throw Error($"the items of a buffer are plain values (numbers, bool, char, enums, bitfields, date, time, clock, structs of plain values), not {elem} (spec 11.1.5.4)", field.At);
      }
      if (field.Init != null && !(field.Init.Kind == "List" && field.Init.Children.Count == field.Buffer)) {
        throw Error($"the start value of the buffer '{field.Name}' is an array literal of {field.Buffer} items", field.Init);
      }
    }
    foreach (var info in structs.Values.Where(info => info.Layout != null)) {
      CheckLayoutFields(info);
    }
    foreach (var info in structs.Values) {
      EmitStructType(info, info.Node);
    }
  }

  // the N of arr<T>[N]: an integer literal, or a top-level const with an integer literal (spec 11.1.5.4)
  long BufferLength(Node size) {
    var value = IntLiteralValue(size);
    if (value == null && size.Kind == "Name") {
      var constDecl = ProgramNode!.Children.FirstOrDefault(statement => statement.Kind == "Decl" && statement.Text == size.Text
        && statement.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("const") == true);
      if (constDecl?.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is Node constValue) {
        value = IntLiteralValue(constValue);
      }
    }
    if (value == null) {
      throw Error("the size of a buffer is known at compile time: an integer literal or a const (spec 11.1.5.4)", size);
    }
    if (value <= 0 || value > int.MaxValue) {
      throw Error($"the size of a buffer goes from 1 to {int.MaxValue}", size);
    }
    return (long)value;
  }

  void CheckStructFieldType(string type, Node at) {
    if (IsArr(type)) {
      throw Error("a struct cannot have an array of undefined size: an arr field needs a size known at compile time, arr<T>[N] (spec 11.1.5.4)", at);
    }
    if (IsStruct(type) || IsEnum(type) || IsNumeric(type) || type is "bool" or "char" or "addr" or "string1k" || IsTimeType(type)) {
      return;
    }
    if (IsNullable(type) && !IsStruct(BaseOf(type)) && BaseOf(type) != "string" && !IsTuple(BaseOf(type))) {
      return;   // i32?, date?: a value with its null flag
    }
    if (type is "string" or "string?") {
      return;
    }
    if (IsFunType(type)) {
      return;   // a usage of a function value that does not borrow; a copy of the struct copies it with ! (spec 13.3.3)
    }
    if (IsClass(type)) {
      // an object the struct owns: copying the struct clones it (spec 11.1.5.3)
      if (NotClonableReason(ObjInfo(type)) is string reason) {
        throw Error($"a struct holds only objects that can be cloned: {DisplayName(type)} cannot be cloned, {reason} (spec 11.1.5.3)", at);
      }
      cloneUsed = true;
      return;
    }
    throw NotSupported($"{type} inside a struct", at);
  }

  // the C typedef, after the structs it holds
  void EmitStructType(StructInfo info, Node at) {
    if (info.Emitted) {
      return;
    }
    if (info.Emitting) {
      throw Error($"the struct '{info.Name}' holds itself: a struct is stored inline and cannot contain itself", at);
    }
    info.Emitting = true;
    foreach (var field in info.Fields) {
      string inner = field.Buffer != null ? ArrElem(field.Type) : field.Type;
      if (IsStruct(inner)) {
        EmitStructType(structs[inner], field.At);
      }
    }
    var fields = new StringBuilder();
    foreach (var field in info.Fields) {
      // CType first: it may write the typedef of a nullable; a buffer is a C array (spec 11.1.5.4); a bool of a <fixed> struct is 4 bytes
      string cType = info.Layout == "fixed" && field.Type == "bool" ? "int32_t" : CType(field.Buffer != null ? ArrElem(field.Type) : field.Type);
      fields.Append(field.Buffer is long length
        ? $"  {cType} f_{field.Name}[{length}];\n"
        : $"  {cType} f_{field.Name};\n");
    }
    string typedef = $"typedef struct {info.CName} {{\n{fields}}} {info.CName};\n";
    // <compact>: no alignment, every field at its own size (spec 11.1.5.6)
    typeDefs.Append(info.Layout == "compact" ? $"#pragma pack(push, 1)\n{typedef}#pragma pack(pop)\n\n" : typedef + "\n");
    info.Emitting = false;
    info.Emitted = true;
  }

  // the value of a field nobody gave: its start value, or zero (spec 11.1.5.1)
  string StructFieldDefault(StructField field) {
    if (field.Buffer != null) {
      return field.Init == null ? "{0}" : BufferInitializer(field, field.Init, new Scope(null));
    }
    if (field.Init == null) {
      return ZeroValue(field.Type);
    }
    var value = ExprOf(field.Init, new Scope(null), field.Type);
    return StructFieldValue(field, value, field.Init);
  }

  // the value a field keeps: a string or an object of its own (spec 11.1.5.3)
  string StructFieldValue(StructField field, Expr value, Node at, Scope? scope = null) {
    string converted = Convert(value, field.Type, at);
    if (IsFunType(field.Type)) {
      if (scope != null) {
        CheckNotBorrowingFunction(field.Type, at, scope, $"the field '{field.Name}'");   // the struct keeps a usage (spec 13.3.4)
      }
      return FnOwned(value with { Code = converted });
    }
    if (field.Type == "string") {
      return StrOwned(value, converted);
    }
    if (field.Type == "string?") {
      return OptStrItem(value, converted);
    }
    if (IsClass(field.Type)) {
      if (!value.Fresh && value.Type != "null") {
        throw Error($"the field '{field.Name}' owns its object: give it a new object or move one in with ->x", at);
      }
      return converted;
    }
    if (StructHoldsObjects(field.Type)) {
      return StructOwnedCode(value, field.Type, at);
    }
    return converted;
  }

  // a struct nobody gave a value: every field at its start value or zero
  string StructZero(StructInfo info) {
    var parts = info.Fields.Select(field => $".f_{field.Name} = {StructFieldDefault(field)}");
    return $"(({info.CName}){{ {string.Join(", ", parts)} }})";
  }

  // Vec3(x: 2.0): the fields given by name, the others at their start value or zero; Vec3(1.0, 2.0, 3.0): all the fields in order
  // (spec 11.1.5.1). The values are computed in the order they are written.
  Expr StructCreate(StructInfo info, List<Node> args, Scope scope, Node at) {
    var given = StructGiven(info, args, at);
    if (given.Count == 0) {
      return new Expr(StructZero(info), info.Name, true);
    }
    return StructCreateCode(info, given, scope);
  }

  // the fields a creation gives, in the order they are written
  List<(StructField Field, Node Value)> StructGiven(StructInfo info, List<Node> args, Node at) {
    var named = args.Where(arg => arg.Kind == "Named").ToList();
    var positional = args.Where(arg => arg.Kind != "Named").ToList();
    if (named.Count > 0 && positional.Count > 0) {
      throw Error($"a struct is created by name or by position, not both: {info.Name}({info.Fields[0].Name}: value, ...) or all its fields in order", at);
    }
    if (positional.Count > 0 && positional.Count != info.Fields.Count) {
      throw Error($"a struct created by position needs all its {info.Fields.Count} fields, in order ({string.Join(", ", info.Fields.Select(field => field.Name))}); "
        + $"give only some of them by name: {info.Name}({info.Fields[0].Name}: value) (spec 11.1.5.1)", at);
    }
    var given = new List<(StructField Field, Node Value)>();
    for (int index = 0; index < positional.Count; index++) {
      given.Add((info.Fields[index], positional[index]));
    }
    foreach (var arg in named) {
      var field = info.Fields.FirstOrDefault(candidate => candidate.Name == arg.Text)
        ?? throw Error($"the struct '{info.Name}' has no field '{arg.Text}'", arg);
      if (given.Any(pair => pair.Field == field)) {
        throw Error($"the field '{arg.Text}' is given twice", arg);
      }
      given.Add((field, arg.Children[0]));
    }
    return given;
  }

  Expr StructCreateCode(StructInfo info, List<(StructField Field, Node Value)> given, Scope scope) {
    // the given values first, in the order they are written; then the struct, with the start values of the others
    var code = new StringBuilder("({ ");
    var made = new Dictionary<StructField, string>();
    var copies = new StringBuilder();
    string result = NewTemp();
    foreach (var (field, value) in given) {
      if (field.Buffer != null) {
        if (value.Kind == "List") {
          made[field] = BufferInitializer(field, value, scope);
        } else {
          copies.Append($"memmove({result}.f_{field.Name}, {BufferSource(field, value, scope)}, sizeof({result}.f_{field.Name})); ");
          made[field] = "{0}";
        }
        continue;
      }
      var expr = ExprOf(value, scope, field.Type);
      string temp = NewTemp();
      code.Append($"{CType(field.Type)} {temp} = {StructFieldValue(field, expr, value, scope)}; ");
      made[field] = temp;
    }
    var parts = info.Fields.Select(field => $".f_{field.Name} = {(made.TryGetValue(field, out var temp) ? temp : StructFieldDefault(field))}");
    code.Append($"{info.CName} {result} = {{ {string.Join(", ", parts)} }}; {copies}{result}; }})");
    return new Expr(code.ToString(), info.Name, true);
  }

  StructField StructFieldOf(string type, string name, Node at) {
    var info = structs[type];
    return info.Fields.FirstOrDefault(field => field.Name == name)
      ?? throw Error($"the struct '{info.Name}' has no field '{name}'", at);
  }

  // s.x
  Expr StructMember(Expr obj, Node n) {
    if (n.Text == "sizeof" && !structs[obj.Type].Fields.Any(field => field.Name == "sizeof")) {
      return StructSizeof(obj.Type);   // spec 11.1.5.6
    }
    if (n.Text == "hash" && !structs[obj.Type].Fields.Any(field => field.Name == "hash")) {
      return HashOf(TempStruct(obj), n);   // spec 11.1.5.9
    }
    if (n.Text == "offset") {
      throw Error("offset takes the name of a field: s.offset(x) (spec 11.1.5.6)", n);
    }
    var field = StructFieldOf(obj.Type, n.Text!, n);
    return new Expr(StructFieldRead($"({obj.Code})", field), field.Type);
  }

  // the value of a field; a buffer is read as an arr on the items inside the struct (spec 11.1.5.4)
  string StructFieldRead(string structCode, StructField field) {
    if (field.Type == "bool" && field.Buffer == null && structs.Values.Any(info => info.Layout == "fixed" && info.Fields.Contains(field))) {
      return $"({structCode}.f_{field.Name} != 0)";   // a 4-byte bool of a <fixed> struct (spec 11.1.5.6)
    }
    if (field.Buffer is long length) {
      string elem = ArrElem(field.Type);
      return $"(({ArrCType(elem)}){{ ({CType(elem)}*){structCode}.f_{field.Name}, {length} }})";
    }
    return $"{structCode}.f_{field.Name}";
  }

  // s.x = v: a plain parameter is a read-only lend, its fields are not written (spec 13.1, 11.1.5.2); an unrestricted method only reads this
  LValueInfo StructLValue(LValueInfo owner, Node target, Scope scope) {
    CheckStructWritable(target.Children[0], target.Text!, scope, target);
    var field = StructFieldOf(owner.Type, target.Text!, target);
    if (field.Buffer != null) {
      throw Error($"the buffer '{field.Name}' is changed item by item ({target.Text}[i] = v) or assigned as a whole from an array literal of {field.Buffer} items (spec 11.1.5.4)", target);
    }
    // a field that owns a string, an object or a struct that holds them: its old value ends when it is replaced (spec 11.1.5.3)
    bool owning = IsOwningFieldType(field.Type) || StructHoldsObjects(field.Type);
    return new LValueInfo($"{owner.Code}.f_{field.Name}", field.Type, owner.IsConst, null, owning);
  }
}

public sealed partial class CodeGen {
  // a field (or a field of a struct it holds) has a start value: zero is not enough for a new item
  bool StructHasStartValues(string type) =>
    structs[type].Fields.Any(field => field.Init != null || IsStruct(field.Type) && StructHasStartValues(field.Type));

  // a new array of structs: every item at its start values
  string StructArrayFill(string arrayCode, string arrayType, string elem) {
    string array = NewTemp();
    string index = NewTemp();
    return $"({{ {CType(arrayType)} {array} = {arrayCode}; for (df_uns {index} = 0; {index} < {array}.len; {index}++) {{ {array}.p[{index}] = {StructZero(structs[elem])}; }} {array}; }})";
  }

  // ps[2] of an array or a vec of structs, as a place: its fields can be written (ps[2].b = 4)
  LValueInfo? StructItemLValue(Node index, Scope scope) {
    if (index.Children[0].Kind == "GenericRef" || index.Children[1].Kind == "Range") {
      return null;
    }
    var obj = ExprOf(index.Children[0], scope);
    bool isVec = IsVec(obj.Type);
    string? elem = IsArr(obj.Type) ? ArrElem(obj.Type) : isVec ? VecElem(obj.Type) : null;
    if (elem == null || !IsStruct(elem)) {
      return null;
    }
    if (ReadOnlyVariable(index.Children[0], scope) is not null) {
      throw Error($"'{index.Children[0].Text}' is a read-only reference: its items cannot be changed", index);
    }
    CheckBufferWrite(index.Children[0], scope, index);   // an item of a buffer (spec 11.1.5.4)
    var position = ExprOf(index.Children[1], scope, "u64");
    string array = NewTemp();
    string at = NewTemp();
    string slot = isVec ? $"{array}->{VecField(obj.Type, "items")}.p[{at}]" : $"{array}.p[{at}]";
    string length = isVec ? $"{array}->{VecField(obj.Type, "count")}" : $"{array}.len";
    string holder = isVec ? NullChecked(obj.Code, obj.Type) : obj.Code;
    string code = $"(*({{ {CType(obj.Type)} {array} = {holder}; df_uns {at} = {IndexValue(position, index)}; "
      + $"if (!df_idx_ok({length}, {at}, {fn!.NameConst})) {RaiseJump} &{slot}; }}))";
    return new LValueInfo(code, elem, false);
  }
}

public sealed partial class CodeGen {
  // a == b on structs: field by field, each field with its own == (spec 11.1.5.9)
  string StructEquals(Expr l, Expr r, Node at) {
    if (l.Type != r.Type) {
      throw Error($"cannot compare {l.Type} and {r.Type}", at);
    }
    string left = NewTemp();
    string right = NewTemp();
    var info = structs[l.Type];
    var parts = info.Fields.Select(field => field.Buffer is long length
      ? BufferEquals(field, left, right, length, at)
      : EqualsCode(new Expr(StructFieldRead(left, field), field.Type), new Expr(StructFieldRead(right, field), field.Type), at));
    return $"({{ {info.CName} {left} = {TempStruct(l).Code}; {info.CName} {right} = {TempStruct(r).Code}; ({string.Join(" && ", parts)}); }})";
  }
}

public sealed partial class CodeGen {
  readonly HashSet<string> structPrinters = new();
  readonly StringBuilder structCode = new();
  readonly StringBuilder structProtos = new();

  // the C function that prints a struct: its name and its first 3 fields, then ", ..." (spec 11.1.5.9, as for classes)
  string StructPrinter(StructInfo info) {
    string name = "df_str_" + info.CName;
    if (!structPrinters.Add(info.Name)) {
      return name;
    }
    var sb = new StringBuilder();
    sb.Append($"static df_string {name}({info.CName} o) {{\n");
    sb.Append($"  df_string r = {CString(CodePoints(SourceMap.PrintName(info.Name) + " { "))};\n");
    for (int index = 0; index < info.Fields.Count && index < 3; index++) {
      var field = info.Fields[index];
      string label = (index > 0 ? ", " : "") + field.Name + ": ";
      string value = IsClass(field.Type)
        ? $"(o.f_{field.Name} == NULL ? df_str_lit(\"null\", 4) : {CString(CodePoints(DisplayName(field.Type) + "{}"))})"   // as in classes
        : ItemStrOf(new Expr(StructFieldRead("o", field), field.Type), info.Node);   // a struct field writes its own printer first
      sb.Append($"  r = df_str_cat(r, {CString(CodePoints(label))});\n");
      sb.Append($"  r = {(field.Type == "string" ? "df_str_cat" : "df_str_catx")}(r, {value});\n");
    }
    if (info.Fields.Count > 3) {
      sb.Append($"  r = df_str_cat(r, {CString(CodePoints(", ..."))});\n");
    }
    sb.Append($"  r = df_str_cat(r, {CString(CodePoints(" }"))});\n");
    sb.Append("  return r;\n}\n\n");
    structProtos.Append($"static df_string {name}({info.CName} o);\n");
    structCode.Append(sb);
    return name;
  }
}

enum ReadOnlyKind { None, This, Param, Reference, Constant }

// Methods of structs (spec 11.1.5.2), step 2: a method is a C function whose first parameter is the address of the struct (self);
// this is (*self), the fields are read and written by their bare names. Through a read-only place (a plain parameter, a read-only
// reference, a constant, this in an unrestricted method) only unrestricted methods are called, and an unrestricted method writes no
// field of this.
public sealed partial class CodeGen {
  StructInfo? currentStruct;   // the struct whose method is being compiled

  void RegisterStructMethods() {
    foreach (var info in structs.Values) {
      foreach (var member in info.Node.Children.Where(child => child.Kind == "Fun")) {
        var (visibility, _, isStatic, _, isOverride) = ReadMods(member);
        var mods = member.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ') ?? Array.Empty<string>();
        if (isOverride) {
          throw Error("a struct has no inheritance: 'replace' does not apply (spec 11.1.5)", member);
        }
        if (mods.Contains("asparent") || mods.Contains("extern")) {
          throw Error($"'{(mods.Contains("asparent") ? "asparent" : "extern")}' does not apply to the method of a struct (spec 11.1.5.2)", member);
        }
        if (info.Fields.Any(field => field.Name == member.Text)) {
          throw Error($"the struct '{info.Name}' has a field '{member.Text}': a method cannot have the same name", member);
        }
        var fun = RegisterFun(member, null, null, info)!;
        fun.Struct = info;
        fun.IsStatic = isStatic;   // called with the name of the struct, Vec2.dot(a, b)
        fun.Visibility = visibility;
        fun.IsUnrestricted = mods.Contains("unrestricted");
      }
    }
  }

  // x inside a method of a struct, when no variable has that name: this.x
  Node? StructBareField(Node name, Scope scope) {
    if (currentStruct == null || scope.Find(name.Text!) != null || scope.Find("this") == null
        || !currentStruct.Fields.Any(field => field.Name == name.Text)) {
      return null;
    }
    return Synthetic("Member", name, name.Text).Add(Synthetic("Name", name, "this"));
  }

  // whether a place (s, s.inner, ps[2], this, a bare field) can only be read, and why
  (string Name, ReadOnlyKind Kind) ReadOnlyPlace(Node place, Scope scope) {
    var root = place;
    while (root.Kind is "Member" or "Index" or "Paren" or "Transmute") {
      root = root.Kind == "Transmute" ? root.Children[1] : root.Children[0];
    }
    if (root.Kind != "Name") {
      return ("", ReadOnlyKind.None);
    }
    if (StructBareField(root, scope) != null) {
      root = Synthetic("Name", root, "this");
    }
    var variable = scope.Find(root.Text!);
    if (variable == null) {
      return ("", ReadOnlyKind.None);
    }
    if (variable.ReadOnlyStruct) {
      return (root.Text!, root.Text == "this" ? ReadOnlyKind.This : ReadOnlyKind.Param);
    }
    if (variable.ReadOnlyRef || variable.CapturedReadOnly) {
      return (root.Text!, ReadOnlyKind.Reference);
    }
    if (variable.IsConst && IsStruct(variable.Type)) {
      return (root.Text!, ReadOnlyKind.Constant);
    }
    return ("", ReadOnlyKind.None);
  }

  // the receiver could be a struct: a variable, a field, an item, a new struct or the result of a function that gives one
  bool MayBeStructReceiver(Node receiver, Scope scope) {
    if (Path(receiver) is string path) {
      var first = Synthetic("Name", receiver, path.Split('.')[0]);
      return scope.Find(first.Text!) != null || StructBareField(first, scope) != null || globalConsts.ContainsKey(first.Text!)
        || currentClass != null && FindField(currentClass, first.Text!) != null;
    }
    if (receiver.Kind is "Index" or "Paren" or "Transmute") {
      return true;
    }
    if (receiver.Kind == "Call" && receiver.Children[0] is { Kind: "Name" } called && scope.Find(called.Text!) == null) {
      return structs.ContainsKey(called.Text!)
        || funs.TryGetValue(called.Text!, out var overloads) && overloads.All(fun => IsStruct(fun.Ret));
    }
    return false;
  }

  // s.len(), this.len(), ps[1].grow(), Vec3(1.0, 2.0, 3.0).len(): null when the receiver is not a struct
  Expr? StructMethodCall(Node callee, List<Node> args, Scope scope, Node at) {
    var receiverNode = callee.Children[0];
    if (!MayBeStructReceiver(receiverNode, scope)) {
      return null;
    }
    var receiver = ExprOf(receiverNode, scope);
    if (!IsStruct(receiver.Type)) {
      return null;
    }
    var info = structs[receiver.Type];
    if (callee.Text == "offset" && !funs.ContainsKey(info.Name + ".offset")) {
      return StructOffset(info, args, at);   // s.offset(x) (spec 11.1.5.6)
    }
    if (!funs.TryGetValue(info.Name + "." + callee.Text, out var all)) {
      if (info.Fields.FirstOrDefault(field => field.Name == callee.Text && IsFunType(field.Type)) is StructField held) {
        return FnValueCall(new Expr($"{receiver.Code}.f_{held.Name}", held.Type), args, scope, at);   // b.onClick(3): a field of a function type (spec 13.3.2)
      }
      throw Error($"the struct '{info.Name}' has no method '{callee.Text}'", at);
    }
    var overloads = all.Where(fun => !fun.IsStatic).ToList();
    if (overloads.Count == 0) {
      throw Error($"'{callee.Text}' is a static function of {info.Name}: call it with the name of the struct, {info.Name}.{callee.Text}(...) (spec 11.1.5.2)", at);
    }
    var f = ResolveOverload(overloads, args, scope, at);
    if (f.Visibility == Vis.Private && currentStruct != info) {
      throw Error($"'{callee.Text}' is a private method of {info.Name}: only the methods of {info.Name} call it", at);
    }
    if (receiverNode.Kind is "Name" or "Member" or "Index" or "Paren" or "Transmute") {
      var (name, kind) = ReadOnlyPlace(receiverNode, scope);
      if (kind != ReadOnlyKind.None && !f.IsUnrestricted) {
        string what = kind switch {
          ReadOnlyKind.This => "an unrestricted method only reads its struct",
          ReadOnlyKind.Param => $"'{name}' is a read-only parameter",
          ReadOnlyKind.Constant => $"'{name}' is a constant",
          _ => $"'{name}' is a read-only reference"
        };
        throw Error($"{what}: only the unrestricted methods of {info.Name} can be called, not '{callee.Text}'{DefinedAt(new[] { f }, callee.Text!)} (spec 1.6.2, 11.1.5.2)", at);
      }
      LValueInfo? place = null;
      try {
        place = receiverNode.Kind == "Index" ? StructItemLValue(receiverNode, scope) : LValue(receiverNode, scope);
      } catch (CompileException) when (f.IsUnrestricted || kind != ReadOnlyKind.None) {
        place = null;   // not a place it can change (a property, a read-only field): an unrestricted method reads a copy
      }
      if (place is LValueInfo found) {
        return UserCall(f, args, scope, at, $"&({found.Code})");
      }
    }
    // a value (a new struct, a result): the method works on a copy
    string temp = NewTemp();
    var call = UserCall(f, args, scope, at, "&" + temp);
    if (receiver.Fresh && StructHoldsObjects(info.Name)) {
      // a new struct that nothing keeps: its strings and objects end after the call (spec 11.1.5.3)
      if (call.Type == "void") {
        return new Expr($"({{ {info.CName} {temp} = {receiver.Code}; {call.Code}; {StructDropCode(info.Name, temp)} }})", "void");
      }
      string result = NewTemp();
      return new Expr($"({{ {info.CName} {temp} = {receiver.Code}; {CType(call.Type)} {result} = {call.Code}; {StructDropCode(info.Name, temp)} {result}; }})", call.Type, call.Fresh);
    }
    return new Expr($"({{ {info.CName} {temp} = {receiver.Code}; {call.Code}; }})", call.Type, call.Fresh);
  }
}
