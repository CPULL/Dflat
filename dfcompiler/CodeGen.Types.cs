using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// Types: resolution, C names, implicit conversions, bool/string conversion, equality.
public sealed partial class CodeGen {
  // C code that can be read twice without side effects: v_x, t_3.val, v_pair.e0
  static readonly Regex SimpleCode = new(@"^[\p{L}_][\p{L}\p{N}_]*(\.[\p{L}\p{N}_]+)*$", RegexOptions.Compiled);

  // struct typedefs for nullables and tuples, in dependency order
  readonly StringBuilder typeDefs = new();
  readonly HashSet<string> definedTypes = new();
  readonly Dictionary<string, string> tupleCNames = new();

  static bool IsNullable(string type) => type.EndsWith('?');
  static string BaseOf(string type) => IsNullable(type) ? type[..^1] : type;
  static bool IsTuple(string type) => type.StartsWith('(') && type.EndsWith(')');
  static bool IsMulti(string type) => type.StartsWith('*');
  bool IsEnum(string type) => enums.ContainsKey(type);

  static bool IsInt(string t) => SignedTypes.Contains(t) || UnsignedTypes.Contains(t);
  static bool IsNumeric(string t) => IsInt(t) || RealTypes.Contains(t);

  // the type an untyped literal should take next to a value of this type
  static string? NumericContext(string? type) {
    if (type == null) {
      return null;
    }
    string baseType = BaseOf(type);
    return IsNumeric(baseType) ? baseType : null;
  }

  bool IsKnownType(string t) {
    return SignedTypes.Contains(t) || UnsignedTypes.Contains(t) || RealTypes.Contains(t)
      || t is "bool" or "char" or "string" or "byte" || IsEnum(t);
  }

  static string Normalize(string t) => t == "byte" ? "u8" : t;

  static string Family(string t) {
    if (SignedTypes.Contains(t)) {
      return "signed";
    }
    if (UnsignedTypes.Contains(t)) {
      return "unsigned";
    }
    if (RealTypes.Contains(t)) {
      return "real";
    }
    return t;
  }

  static int Bits(string t) {
    return t switch {
      "i8" or "u8" => 8,
      "i16" or "u16" => 16,
      "i32" or "u32" or "r32" => 32,
      "i64" or "u64" or "r64" => 64,
      _ => 0
    };
  }

  // ---------------------------------------------------------------- tuple type strings

  // "(i32 id,string name)" → [("i32", "id"), ("string", "name")]; also accepts "*(...)"
  static List<(string Type, string? Name)> TupleElems(string tupleType) {
    string inner = tupleType.TrimStart('*')[1..^1];
    var parts = new List<string>();
    int depth = 0;
    int start = 0;
    for (int index = 0; index < inner.Length; index++) {
      char current = inner[index];
      if (current == '(') {
        depth++;
      } else if (current == ')') {
        depth--;
      } else if (current == ',' && depth == 0) {
        parts.Add(inner[start..index]);
        start = index + 1;
      }
    }
    parts.Add(inner[start..]);

    var elems = new List<(string Type, string? Name)>();
    foreach (var part in parts) {
      int split = -1;
      depth = 0;
      for (int index = 0; index < part.Length; index++) {
        if (part[index] == '(') {
          depth++;
        } else if (part[index] == ')') {
          depth--;
        } else if (part[index] == ' ' && depth == 0) {
          split = index;
        }
      }
      elems.Add(split < 0 ? (part, null) : (part[..split], part[(split + 1)..]));
    }
    return elems;
  }

  static string MakeTuple(IEnumerable<(string Type, string? Name)> elems) {
    return "(" + string.Join(",", elems.Select(elem => elem.Name == null ? elem.Type : elem.Type + " " + elem.Name)) + ")";
  }

  static int TupleFieldIndex(string tupleType, string field, Node at) {
    var elems = TupleElems(tupleType);
    int index = int.TryParse(field, out int position) ? position : elems.FindIndex(elem => elem.Name == field);
    if (index < 0 || index >= elems.Count) {
      throw Error($"tuple {tupleType} has no field '{field}'", at);
    }
    return index;
  }

  // ---------------------------------------------------------------- C types

  static string PrimitiveCType(string t) {
    return t switch {
      "i8" => "int8_t",
      "i16" => "int16_t",
      "i32" => "int32_t",
      "i64" => "int64_t",
      "u8" => "uint8_t",
      "u16" => "uint16_t",
      "u32" => "uint32_t",
      "u64" => "uint64_t",
      "r32" => "float",
      "r64" => "double",
      "bool" => "bool",
      "char" => "uint32_t",
      "string" => "df_string",
      "void" => "void",
      _ => throw new InvalidOperationException(t)
    };
  }

  string CType(string type) {
    if (type == "Exception") {
      return "df_exception*";
    }
    if (IsNullable(type)) {
      return OptCType(BaseOf(type));
    }
    if (IsMulti(type)) {
      return TupleCType(type[1..]);
    }
    if (IsTuple(type)) {
      return TupleCType(type);
    }
    if (enums.TryGetValue(type, out var info)) {
      return PrimitiveCType(info.ValueType);
    }
    return PrimitiveCType(type);
  }

  // a C identifier fragment for the type
  string Mangle(string type) {
    if (IsTuple(type)) {
      return TupleCType(type)["df_".Length..];
    }
    if (IsEnum(type)) {
      return "e_" + type;
    }
    return type;
  }

  string OptCType(string baseType) {
    string baseC = CType(baseType);
    string name = "df_opt_" + Mangle(baseType);
    if (definedTypes.Add(name)) {
      typeDefs.Append($"typedef struct {{\n  bool has;\n  {baseC} val;\n}} {name};\n\n");
    }
    return name;
  }

  // tuples with the same element types share one struct, whatever the field names
  string TupleCType(string tupleType) {
    var cTypes = TupleElems(tupleType).Select(elem => CType(elem.Type)).ToList();
    string key = string.Join(",", cTypes);
    if (!tupleCNames.TryGetValue(key, out var name)) {
      name = "df_tup" + tupleCNames.Count;
      tupleCNames[key] = name;
      var fields = new StringBuilder();
      for (int index = 0; index < cTypes.Count; index++) {
        fields.Append($"  {cTypes[index]} e{index};\n");
      }
      typeDefs.Append($"typedef struct {{\n{fields}}} {name};\n\n");
    }
    return name;
  }

  static string IntConstCode(BigInteger value, string type) {
    if (type == "i64" && value == IntRange("i64").Item1) {
      return "(-9223372036854775807LL - 1)";
    }
    return SignedTypes.Contains(type)
      ? $"(({PrimitiveCType(type)}){value}LL)"
      : $"(({PrimitiveCType(type)}){value}ULL)";
  }

  // value of a declared variable without initializer
  string ZeroValue(string type) {
    if (type == "string") {
      return "df_str_lit(\"\", 0)";
    }
    if (IsNullable(type)) {
      return $"(({CType(type)}){{0}})";
    }
    if (IsTuple(type) || IsMulti(type)) {
      var zeros = TupleElems(type).Select(elem => ZeroValue(elem.Type));
      return $"(({CType(type)}){{ {string.Join(", ", zeros)} }})";
    }
    if (enums.TryGetValue(type, out var info)) {
      return IntConstCode(info.Entries[0].Value, info.ValueType);
    }
    return type == "bool" ? "false" : "0";
  }

  // ---------------------------------------------------------------- type nodes

  string ResolveType(Node n) {
    switch (n.Kind) {
      case "Nullable": {
        string inner = ResolveType(n.Children[0]);
        if (inner == "string") {
          throw Error("string is already nullable; 'string?' is not allowed", n);
        }
        if (IsNullable(inner)) {
          throw Error($"'{inner}' is already nullable", n);
        }
        return inner + "?";
      }
      case "TupleType": {
        var elems = new List<(string Type, string? Name)>();
        foreach (var field in n.Children) {
          if (field.Text != null && elems.Any(elem => elem.Name == field.Text)) {
            throw Error($"duplicate tuple field '{field.Text}'", field);
          }
          elems.Add((ResolveType(field.Children[0]), field.Text));
        }
        return MakeTuple(elems);
      }
      case "Type": {
        if (n.Children.Count > 0) {
          throw Error($"type '{DescribeType(n)}' is not supported yet", n);
        }
        string t = n.Text!;
        if (typeAliases.TryGetValue(t, out var aliased)) {
          return aliased;
        }
        if (!IsKnownType(t)) {
          throw Error($"type '{t}' is not supported yet", n);
        }
        return Normalize(t);
      }
      default:
        throw Error($"type '{DescribeType(n)}' is not supported yet", n);
    }
  }

  static string DescribeType(Node n) {
    return n.Kind == "Type" ? n.Text + (n.Children.Count > 0 ? "<...>" : "") : n.Kind;
  }

  // ---------------------------------------------------------------- conversions

  // implicit conversion: same type, same-family widening, value → nullable, null → nullable
  bool Assignable(string to, string from) {
    if (to == from) {
      return true;
    }
    if (IsMulti(to) || IsMulti(from)) {
      return false;
    }
    if (from == "null") {
      return IsNullable(to) || to == "string";
    }
    if (IsNullable(to)) {
      string toBase = BaseOf(to);
      return Assignable(toBase, IsNullable(from) ? BaseOf(from) : from);
    }
    if (IsNullable(from)) {
      return false;
    }
    if (IsTuple(to) && IsTuple(from)) {
      var toElems = TupleElems(to);
      var fromElems = TupleElems(from);
      return toElems.Count == fromElems.Count
        && toElems.Zip(fromElems).All(pair => Assignable(pair.First.Type, pair.Second.Type));
    }
    if (IsEnum(to) || IsEnum(from)) {
      return false;
    }
    return Family(to) == Family(from) && IsNumeric(to) && Bits(from) <= Bits(to);
  }

  string Convert(Expr e, string to, Node at) {
    if (e.Type == to) {
      return e.Code;
    }
    if (IsMulti(e.Type)) {
      throw Error($"this call returns {TupleElems(e.Type).Count} values; assign them to as many targets", at);
    }
    if (e.Type == "void") {
      throw Error("this expression has no value", at);
    }
    if (to == "bool") {
      return BoolOf(e, at);
    }
    if (e.Type == "null") {
      if (IsNullable(to)) {
        return $"(({CType(to)}){{0}})";
      }
      if (to == "string") {
        return "df_str_lit(\"\", 0)";
      }
      throw Error($"{to} is not nullable; declare it as {to}?", at);
    }
    if (IsNullable(to)) {
      string toBase = BaseOf(to);
      if (!IsNullable(e.Type)) {
        return $"(({CType(to)}){{ true, {Convert(e, toBase, at)} }})";
      }
      string fromBase = BaseOf(e.Type);
      if (!Assignable(toBase, fromBase)) {
        throw Error($"cannot convert {e.Type} to {to} implicitly; use a cast", at);
      }
      if (CType(e.Type) == CType(to)) {
        return e.Code;
      }
      return WithTemp(e, temp =>
        $"(({CType(to)}){{ {temp}.has, {Convert(new Expr(temp + ".val", fromBase), toBase, at)} }})");
    }
    if (IsNullable(e.Type)) {
      throw Error($"this {e.Type} value may be null; unwrap it with '??' or 'enforce'", at);
    }
    if (IsTuple(to) && IsTuple(e.Type)) {
      var toElems = TupleElems(to);
      var fromElems = TupleElems(e.Type);
      if (!Assignable(to, e.Type)) {
        throw Error($"cannot convert {e.Type} to {to}", at);
      }
      if (toElems.Zip(fromElems).All(pair => pair.First.Type == pair.Second.Type)) {
        return e.Code; // same C struct, only the field names differ
      }
      return WithTemp(e, temp => {
        var fields = toElems.Select((elem, index) =>
          Convert(new Expr($"{temp}.e{index}", fromElems[index].Type), elem.Type, at));
        return $"(({CType(to)}){{ {string.Join(", ", fields)} }})";
      });
    }
    if (!Assignable(to, e.Type)) {
      throw Error($"cannot convert {e.Type} to {to} implicitly; use a cast", at);
    }
    return $"(({CType(to)})({e.Code}))";
  }

  // Reads the value once: simple code is used as it is, anything else goes into a temporary
  // (GNU statement expression, supported by clang).
  string WithTemp(Expr e, Func<string, string> build) {
    if (SimpleCode.IsMatch(e.Code)) {
      return build(e.Code);
    }
    string temp = NewTemp();
    return $"({{ {CType(e.Type)} {temp} = {e.Code}; {build(temp)}; }})";
  }

  // common type of several values (switch expression arms)
  string CommonType(List<Expr> values, Node at) {
    string? result = null;
    bool sawNull = false;
    foreach (var value in values) {
      if (value.Type == "null") {
        sawNull = true;
        continue;
      }
      if (value.Type == "void" || IsMulti(value.Type)) {
        throw Error("every switch arm needs a single value", at);
      }
      if (result == null || Assignable(result, value.Type)) {
        result ??= value.Type;
        continue;
      }
      if (!Assignable(value.Type, result)) {
        throw Error($"the values have different types: {result} and {value.Type}", at);
      }
      result = value.Type;
    }
    if (result == null) {
      throw Error("cannot infer a type from null alone", at);
    }
    if (sawNull && !IsNullable(result) && result != "string") {
      result += "?";
    }
    return result;
  }

  string BoolOf(Expr e, Node at) {
    if (e.Type == "bool") {
      return e.Code;
    }
    if (e.Type == "null") {
      return "false";
    }
    if (e.Type == "void") {
      throw Error("this expression has no value", at);
    }
    if (IsTuple(e.Type) || IsMulti(e.Type)) {
      throw Error("tuples cannot be converted to bool", at);
    }
    if (IsNullable(e.Type)) {
      return WithTemp(e, temp => $"({temp}.has && {BoolOf(new Expr(temp + ".val", BaseOf(e.Type)), at)})");
    }
    if (RealTypes.Contains(e.Type)) {
      return $"df_rbool({e.Code})";
    }
    if (e.Type == "string") {
      return $"(({e.Code}).len != 0)";
    }
    return $"(({e.Code}) != 0)";
  }

  string StrOf(Expr e, Node at) {
    if (e.Type == "string") {
      return e.Code;
    }
    if (e.Type == "null") {
      return "df_str_lit(\"\", 0)"; // a null string is empty
    }
    if (e.Type == "void") {
      throw Error("this expression has no value", at);
    }
    if (IsMulti(e.Type)) {
      throw Error($"this call returns {TupleElems(e.Type).Count} values; assign them to as many targets", at);
    }
    if (IsNullable(e.Type)) {
      return WithTemp(e, temp =>
        $"({temp}.has ? {StrOf(new Expr(temp + ".val", BaseOf(e.Type)), at)} : df_str_lit(\"null\", 4))");
    }
    if (IsTuple(e.Type)) {
      return WithTemp(e, temp => {
        var elems = TupleElems(e.Type);
        string code = "df_str_lit(\"(\", 1)";
        for (int index = 0; index < elems.Count; index++) {
          if (index > 0) {
            code = $"df_str_concat({code}, df_str_lit(\", \", 2))";
          }
          code = $"df_str_concat({code}, {StrOf(new Expr($"{temp}.e{index}", elems[index].Type), at)})";
        }
        return $"df_str_concat({code}, df_str_lit(\")\", 1))";
      });
    }
    if (IsEnum(e.Type)) {
      return $"df_enum_str_{e.Type}({e.Code})";
    }
    if (e.Type == "Exception") {
      throw Error("Exception cannot be converted to a string yet; use its Message", at);
    }
    if (SignedTypes.Contains(e.Type)) {
      return $"df_str_from_i64((int64_t)({e.Code}))";
    }
    if (UnsignedTypes.Contains(e.Type)) {
      return $"df_str_from_u64((uint64_t)({e.Code}))";
    }
    return e.Type switch {
      "r64" => $"df_str_from_r64({e.Code})",
      "r32" => $"df_str_from_r32({e.Code})",
      "bool" => $"df_str_from_bool({e.Code})",
      "char" => $"df_str_from_char({e.Code})",
      _ => throw Error($"{e.Type} cannot be converted to a string yet", at)
    };
  }

  // ---------------------------------------------------------------- equality

  // C bool expression for l == r. Null equals only null.
  string EqualsCode(Expr l, Expr r, Node at) {
    if (l.Type == "null" && r.Type == "null") {
      return "true";
    }
    if (l.Type == "null") {
      (l, r) = (r, l);
    }
    if (r.Type == "null") {
      if (IsNullable(l.Type)) {
        return WithTemp(l, temp => $"(!{temp}.has)");
      }
      if (l.Type == "string") {
        return WithTemp(l, temp => $"({temp}.len == 0)");
      }
      throw Error($"{l.Type} values are never null", at);
    }
    bool leftNullable = IsNullable(l.Type);
    bool rightNullable = IsNullable(r.Type);
    if (leftNullable || rightNullable) {
      return WithTemp(l, leftTemp => WithTemp(r, rightTemp => {
        var leftValue = leftNullable ? new Expr(leftTemp + ".val", BaseOf(l.Type)) : new Expr(leftTemp, l.Type);
        var rightValue = rightNullable ? new Expr(rightTemp + ".val", BaseOf(r.Type)) : new Expr(rightTemp, r.Type);
        string inner = EqualsCode(leftValue, rightValue, at);
        if (leftNullable && rightNullable) {
          return $"({leftTemp}.has == {rightTemp}.has && (!{leftTemp}.has || {inner}))";
        }
        return $"({(leftNullable ? leftTemp : rightTemp)}.has && {inner})";
      }));
    }
    if (l.Type == "string" && r.Type == "string") {
      return $"df_str_eq({l.Code}, {r.Code})";
    }
    if (IsTuple(l.Type) || IsTuple(r.Type)) {
      if (!IsTuple(l.Type) || !IsTuple(r.Type) || TupleElems(l.Type).Count != TupleElems(r.Type).Count) {
        throw Error($"cannot compare {l.Type} and {r.Type}", at);
      }
      return WithTemp(l, leftTemp => WithTemp(r, rightTemp => {
        var leftElems = TupleElems(l.Type);
        var rightElems = TupleElems(r.Type);
        var parts = leftElems.Select((elem, index) => EqualsCode(
          new Expr($"{leftTemp}.e{index}", elem.Type), new Expr($"{rightTemp}.e{index}", rightElems[index].Type), at));
        return "(" + string.Join(" && ", parts) + ")";
      }));
    }
    if (IsEnum(l.Type) || IsEnum(r.Type)) {
      if (l.Type != r.Type) {
        throw Error($"cannot compare {l.Type} and {r.Type}", at);
      }
      return $"({l.Code} == {r.Code})";
    }
    if (l.Type == r.Type && l.Type is "bool" or "char") {
      return $"({l.Code} == {r.Code})";
    }
    string common = Unify(l.Type, r.Type, "==", at);
    string leftCode = l.Type == common ? l.Code : $"(({CType(common)}){l.Code})";
    string rightCode = r.Type == common ? r.Code : $"(({CType(common)}){r.Code})";
    return $"({leftCode} == {rightCode})";
  }
}
