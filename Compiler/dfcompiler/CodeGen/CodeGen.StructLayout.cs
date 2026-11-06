using dfparser;

namespace dfcompiler;

// Layout tags and C interop (spec 11.1.5.6), step 5. struct<fixed> is a C struct with the normal alignment, its bool fields are 4 bytes
// (int32_t, read as != 0); struct<compact> has no alignment (#pragma pack(1)), every type at its own size, bool 1 byte. Both hold only
// plain values: numbers, bool, char, addr, enums, bitfields, date, time, clock, buffers of those and structs with the same tag.
// .sizeof (sizeof, padding included) and .offset(field) (offsetof) work on every struct, on a value or on the name of the struct.
// An extern function takes such a struct by value (a copy) or by address: *S name (read-only, the call writes *v) or &*S name
// (writable, the call writes &*v); it can also return one.
public sealed partial class CodeGen {
  void CheckLayoutFields(StructInfo info) {
    foreach (var field in info.Fields) {
      string type = field.Buffer != null ? ArrElem(field.Type) : field.Type;
      if (IsStruct(type)) {
        if (structs[type].Layout != info.Layout) {
          throw Error($"a struct<{info.Layout}> holds only structs with the same tag: {type} is {(structs[type].Layout is string other ? $"struct<{other}>" : "a struct without a tag")} (spec 11.1.5.6)", field.At);
        }
        continue;
      }
      bool plain = IsNumeric(type) || IsEnum(type) || type is "bool" or "char" or "addr" or "date" or "time" or "clock";
      if (!plain) {
        throw Error($"a struct<{info.Layout}> holds only plain values (numbers, bool, char, addr, enums, bitfields, date, time, clock, buffers and structs with the same tag), not {type} (spec 11.1.5.6)", field.At);
      }
      if (field.Buffer != null && type == "bool" && info.Layout == "fixed") {
        throw NotSupported("a buffer of bool in a struct<fixed> (4-byte items)", field.At);
      }
    }
  }

  // s.sizeof, Vec2.sizeof: the size in bytes, padding included
  Expr StructSizeof(string type) => new Expr($"((df_uns)sizeof({structs[type].CName}))", NativeUns);

  // s.offset(x), Vec2.offset(x): the byte offset of a field, padding included
  Expr StructOffset(StructInfo info, List<Node> args, Node at) {
    if (args.Count != 1 || args[0].Kind != "Name") {
      throw Error($"offset takes the name of a field: {info.Name}.offset({info.Fields[0].Name}) (spec 11.1.5.6)", at);
    }
    var field = StructFieldOf(info.Name, args[0].Text!, args[0]);
    return new Expr($"((df_uns)offsetof({info.CName}, f_{field.Name}))", NativeUns);
  }

  // f(*v) for *S name, f(&*v) for &*S name: the address of the struct (spec 11.1.5.6, 13.1)
  string StructPointerArg(ParamSig param, Node arg, Scope scope) {
    string marker = param.Pointer == 1 ? "*" : "&*";
    string kind = param.Pointer == 1 ? "AddressOf" : "WritablePointerOf";
    if (arg.Kind != kind) {
      throw Error($"'{param.Name}' takes the address of the struct: write {marker}{(arg.Kind is "AddressOf" or "WritablePointerOf" ? arg.Children[0].Text : arg.Text) ?? "s"} (spec 11.1.5.6)", arg);
    }
    var place = arg.Children[0];
    if (place.Kind is not ("Name" or "Member" or "Index")) {
      throw Error($"{marker} needs a variable, a field or an item", arg);
    }
    var value = ExprOf(place, scope);
    if (value.Type != param.Type) {
      throw Error($"'{param.Name}' is {Article(param.Type)} {param.Type}, not {value.Type}", arg);
    }
    if (param.Pointer == 2 && ReadOnlyPlace(place, scope).Kind != ReadOnlyKind.None) {
      throw Error($"'{place.Text}' is read-only: &* gives a writable address", arg);
    }
    var target = place.Kind == "Index" && StructItemLValue(place, scope) is LValueInfo item ? item : LValue(place, scope);
    if (param.Pointer == 2 && target.IsConst) {
      throw Error($"'{place.Text}' is const: &* gives a writable address", arg);
    }
    return $"&({target.Code})";
  }
}
