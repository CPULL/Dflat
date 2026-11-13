using System.Text;
using dfparser;

namespace dfcompiler;

// Structs that hold strings and objects (spec 11.1.5.3), step 3. A struct is a value and owns what it holds: copying it clones its
// strings and objects (= and ! give the same result), and they end with the struct. Every struct variable owns its struct, except a
// plain parameter (a read-only lend) and a & parameter (the caller's struct). A value that goes into an owner is new (a creation, the
// result of a function, ->s, s!) or it is cloned. The C helpers: df_sclone_s_Name (a copy with its own strings and objects) and
// df_sdrop_s_Name (ends them and leaves the fields empty). arr, vec and list of such structs own them (CodeGen.TupleArrays.cs); tuples of them are not built yet (an error).
public sealed partial class CodeGen {
  readonly HashSet<string> structCloneHelpers = new();
  readonly HashSet<string> structDropHelpers = new();
  readonly HashSet<Node> structObjDecls = new();

  // a field that owns something: a string, a string?, an object (a collection too), a usage of a function value (spec 13.3.3)
  bool IsOwningFieldType(string type) => type is "string" or "string?" || IsClass(type) || IsFunType(type);

  bool StructHoldsObjects(string type) =>
    structs.TryGetValue(type, out var info) && info.Fields.Any(field => field.Buffer == null && (IsOwningFieldType(field.Type) || StructHoldsObjects(field.Type)));

  // the C function that copies a struct with its own strings and objects
  string StructCloneHelper(string type) {
    var info = structs[type];
    string name = "df_sclone_" + info.CName;
    if (structCloneHelpers.Add(type)) {
      var body = new StringBuilder();
      foreach (var field in info.Fields.Where(field => field.Buffer == null)) {
        string source = $"v.f_{field.Name}";
        if (StructHoldsObjects(field.Type)) {
          body.Append($"  {source} = {StructCloneHelper(field.Type)}({source});\n");   // the helpers of the structs inside come first
        } else if (IsOwningFieldType(field.Type)) {
          body.Append($"  {source} = {CloneValue(source, field.Type, field.At)};\n");
        }
      }
      structProtos.Append($"static {info.CName} {name}({info.CName} v);\n");
      structCode.Append($"static {info.CName} {name}({info.CName} v) {{\n{body}  return v;\n}}\n\n");
    }
    return name;
  }

  // the C function that ends the strings and objects of a struct and empties its fields
  string StructDropHelper(string type) {
    var info = structs[type];
    string name = "df_sdrop_" + info.CName;
    if (structDropHelpers.Add(type)) {
      var body = new StringBuilder();
      foreach (var field in info.Fields.Where(field => field.Buffer == null)) {
        string slot = $"v->f_{field.Name}";
        if (StructHoldsObjects(field.Type)) {
          body.Append($"  {StructDropHelper(field.Type)}(&{slot});\n");
        } else if (field.Type == "string") {
          body.Append($"  df_str_free({slot});\n  {slot} = NULL;\n");
        } else if (field.Type == "string?") {
          body.Append($"  df_str_free(({slot}).val);\n  {slot} = (df_opt_string){{0}};\n");
        } else if (IsClass(field.Type)) {
          body.Append($"  df_drop((void*){slot});\n  {slot} = NULL;\n");
        } else if (IsFunType(field.Type)) {
          body.Append($"  df_fn_release({slot});\n  {slot} = (df_fn){{0}};\n");
        }
      }
      structProtos.Append($"static void {name}({info.CName}* v);\n");
      structCode.Append($"static void {name}({info.CName}* v) {{\n{body}}}\n\n");
    }
    return name;
  }

  string StructDropCode(string type, string place) => $"{StructDropHelper(type)}(&({place}));";

  // the value an owner takes: new values are taken as they are, anything else is cloned
  string StructOwnedCode(Expr value, string type, Node at) {
    string converted = Convert(value, type, at);
    if (!StructHoldsObjects(type) || value.Fresh) {
      return converted;
    }
    return $"{StructCloneHelper(type)}({converted})";
  }

  // a declaration of a struct that holds objects: it owns them and they end with the scope. The type is written, or it is var with a
  // creation (Vec3(...)) or a call of functions of the program that return such a struct
  bool IsStructObjDecl(Node decl) {
    if (LibraryCode) {
      // the collections of the library: an item taken out of them (T gone = ->items[i], T gone = takeAt(i)) ends with the local
      var taken = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
      return taken is { Kind: "Gift" } or { Kind: "Call", Children: [{ Text: "takeAt" }, ..] }
        && decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is Node takenNode
        && TryResolveType(takenNode) is string takenType && StructHoldsObjects(takenType);
    }
    if (decl.Children.Any(child => child.Kind == "Mods")) {
      return false;
    }
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode is { Kind: "Type", Text: "var" }) {
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
      if (init is { Kind: "Call" } call && call.Children[0] is { Kind: "Name" } called && structs.ContainsKey(called.Text!)) {
        return StructHoldsObjects(called.Text!);
      }
      return init is { Kind: "Call" } && CalleesOf(init) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine && StructHoldsObjects(f.Ret));
    }
    return typeNode is { Kind: "Type" } && TryResolveType(typeNode) is string type && StructHoldsObjects(type);
  }

  static Node StructDropNode(string name, Node at) =>
    Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("StructDrop", at, name)));

  // t = value where t owns its struct: the old strings and objects end after the new value is made
  bool IsStructOwner(LValueInfo target) =>
    !LibraryCode && StructHoldsObjects(target.Type)
    && (target.IsField || target.IsStatic || target.Variable is { OwnsStruct: true } || target.Variable is { IsParam: true, ReadOnlyStruct: false } && target.Variable.CName.StartsWith("(*"));

  string StructAssign(LValueInfo target, Node value, Scope scope, Node at) {
    var assigned = ExprOf(value, scope, target.Type);
    if (!Assignable(target.Type, assigned.Type)) {
      throw Error($"cannot assign {assigned.Type} to {target.Type}", at);
    }
    string code = StructOwnedCode(assigned, target.Type, at);
    MarkAssigned(target.Variable);
    string old = NewTemp();
    return $"({{ {CType(target.Type)} {old} = {target.Code}; {target.Code} = {code}; {StructDropCode(target.Type, old)} }})";
  }

  // ->s: the struct leaves its owner, which is left with empty fields
  Expr StructGift(Node operand, Expr value, Scope scope, Node at) {
    var place = LValue(operand, scope);
    if (place.IsConst || place.Variable is { ReadOnlyStruct: true }) {
      throw Error($"'{operand.Text}' does not own its struct: it cannot be moved with ->", at);
    }
    if (place.Variable != null && !place.Variable.OwnsStruct && !place.Variable.CName.StartsWith("(*")) {
      throw Error($"'{operand.Text}' does not own its struct: it cannot be moved with ->", at);
    }
    MarkAssigned(place.Variable);
    string taken = NewTemp();
    string code = $"({{ {CType(value.Type)} {taken} = {place.Code}; {place.Code} = ({CType(value.Type)}){{0}}; {taken}; }})";
    return new Expr(code, value.Type, true);
  }

  // a new struct that nothing keeps (a creation or a result read, printed, compared or lent): it ends with the statement
  Expr TempStruct(Expr e) {
    if (LibraryCode || !e.Fresh || !StructHoldsObjects(e.Type)) {
      return e;
    }
    return new Expr(TempBoxCode(e.Type, e.Code, StructDropCode(e.Type, "*t")), e.Type);
  }

  // a struct result nobody keeps (a statement f()): its strings and objects end at once
  string? DroppedStructCode(Expr e) {
    if (LibraryCode || !e.Fresh || !StructHoldsObjects(e.Type)) {
      return null;
    }
    string held = NewTemp();
    return $"({{ {CType(e.Type)} {held} = {e.Code}; {StructDropCode(e.Type, held)} }})";
  }

  // collections and tuples of structs that hold objects would copy them without ending them: not built yet
  void CheckStructItems(string type, Node at) {
    string? elem = IsArr(type) ? ArrElem(type) : IsVec(type) ? VecElem(type) : IsList(type) ? ListElem(type) : null;
    var parts = new List<string>();
    if (elem != null) {
      if (StructHoldsObjects(elem)) {
        CheckStructItems(elem, at);   // arr, vec and list own their struct items as they own tuples (CodeGen.TupleArrays.cs)
        return;
      }
      parts.Add(elem);
    } else if (IsMap(type) || IsSet(type)) {
      if (!IsCompositeKey(MapKey(type))) {
        parts.Add(MapKey(type));   // a struct key keeps a clone and ends it (CodeGen.CompositeKeys.cs)
      }
      if (IsMap(type) && !StructHoldsObjects(MapValue(type))) {
        parts.Add(MapValue(type));   // a struct value is owned by the map (CodeGen.Map.cs)
      }
    } else if (IsTuple(type) || IsMulti(type)) {
      // a tuple owns its struct parts as it owns objects (CodeGen.TupleArrays.cs); the parts inside them are checked
      parts.AddRange(TupleElems(type).Where(item => !StructHoldsObjects(item.Type)).Select(item => item.Type));
      foreach (var item in TupleElems(type).Where(item => StructHoldsObjects(item.Type))) {
        CheckStructItems(item.Type, at);
      }
    }
    foreach (var part in parts) {
      if (StructHoldsObjects(part)) {
        throw NotSupported($"{(IsTuple(type) || IsMulti(type) ? "Tuples" : "Collections")} of structs that hold strings or objects", at);
      }
      CheckStructItems(part, at);
    }
  }

  // the argument of a struct parameter that holds objects: ->S p takes a new struct (->s, s!, a creation, a result), S p lends it
  string StructArg(ParamSig param, Node arg, Scope scope) {
    var value = ExprOf(arg, scope, param.Type);
    if (param.IsGift) {
      if (!value.Fresh) {
        throw Error($"'{param.Name}' takes ownership of the struct: write ->{arg.Text ?? "s"} to move it, or {arg.Text ?? "s"}! to give a copy", arg);
      }
      return Convert(value, param.Type, arg);
    }
    if (arg.Kind == "Gift") {
      throw Error($"'{param.Name}' only borrows the struct: remove the ->", arg);
    }
    return Convert(TempStruct(value), param.Type, arg);   // a new struct lent to the call ends with the statement
  }

  // Vec2.dot(a, b): a static function of a struct (spec 11.1.5.2)
  Expr StructStaticCall(StructInfo info, string name, List<Node> args, Scope scope, Node at) {
    if (name == "offset" && !funs.ContainsKey(info.Name + ".offset")) {
      return StructOffset(info, args, at);   // Vec2.offset(y) (spec 11.1.5.6)
    }
    if (!funs.TryGetValue(info.Name + "." + name, out var all)) {
      throw Error($"the struct '{info.Name}' has no function '{name}'", at);
    }
    var statics = all.Where(fun => fun.IsStatic).ToList();
    if (statics.Count == 0) {
      throw Error($"'{name}' is a method of {info.Name}: call it on a struct, s.{name}(...)", at);
    }
    var f = ResolveOverload(statics, args, scope, at);
    if (f.Visibility == Vis.Private && currentStruct != info) {
      throw Error($"'{name}' is a private function of {info.Name}: only the code of {info.Name} calls it", at);
    }
    return UserCall(f, args, scope, at);
  }
}
