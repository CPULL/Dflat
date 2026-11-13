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

  // the string literals of the program (CString): C text -> name of its static object
  readonly Dictionary<string, string> literalNames = new();
  readonly StringBuilder literalCode = new();

  static bool IsNullable(string type) => type.EndsWith('?');
  static string BaseOf(string type) => IsNullable(type) ? type[..^1] : type;
  static bool IsTuple(string type) => type.StartsWith('(') && type.EndsWith(')');
  static bool IsMulti(string type) => type.StartsWith('*');
  bool IsEnum(string type) => enums.ContainsKey(type);
  bool IsBitfield(string type) => enums.TryGetValue(type, out var info) && info.Bitfield;

  // fs.DeleteOptions with alias Root.IO as fs, Root.IO.DeleteOptions, String.StringEncoding: the library enum's own name
  string StripLibraryQualifier(string name) {
    int dot = name.IndexOf('.');
    if (dot < 0) {
      return name;
    }
    string first = name[..dot];
    string full = (nameAliases.TryGetValue(first, out var target) ? target : first) + name[dot..];
    foreach (string library in new[] { "Root.IO.", "Root.ExecProcess." }) {
      if (full.StartsWith(library) && full.IndexOf('.', library.Length) < 0) {
        return full[library.Length..];   // also Root.ExecProcess.RunResult
      }
    }
    if (full is "Root.Console.Color" or "Console.Color") {
      return "ConsoleColor";   // Console.Color (stdlib 2)
    }
    return full == "String.StringEncoding" ? "StringEncoding" : name;
  }

  static bool IsTimeType(string t) => t is "date" or "time" or "clock" or "lapse";

  // the null of date, time, clock and lapse (spec 3.2, 3.3)
  static string NullCode(string t) => t switch {
    "date" => "INT64_MIN",
    "time" => "(-1)",
    _ => $"(({PrimitiveCType(t)})0)"
  };

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
      || t is "bool" or "char" or "string" or "string1k" or "byte" or "uns" or "int" or "date" or "time" or "clock" or "lapse" or "Exception" or "addr" || IsEnum(t) || IsClass(t) || IsStruct(t);
  }

  // the native integer types (spec 2.1): int and uns, and the unsuffixed integer literals; the size of the cpu or of --native-int, set
  // for each compilation (i64 / u64 on x64, i32 / u32 on x86)
  public static string NativeInt { get; set; } = "i64";
  public static string NativeUns { get; set; } = "u64";

  // true in a release build: an index outside a collection is a panic (spec 6.1)
  public static bool ReleaseBuild { get; set; }

  static string Normalize(string t) => t switch { "byte" => "u8", "uns" => NativeUns, "int" => NativeInt, _ => t };

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
      "i128" or "u128" => 128,
      "i256" or "u256" => 256,
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
      "string1k" => "df_s1k",      // spec 4.5
      "date" => "int64_t",
      "time" => "int32_t",
      "clock" => "uint64_t",
      "lapse" => "int64_t",
      "void" => "void",
      "addr" => "void*",            // an opaque address (spec 2.3): stored, passed, compared, never dereferenced
      _ => throw new InvalidOperationException(t)
    };
  }

  string CType(string type) {
    if (IsSetValue(type)) {
      return SetValueCType();   // spec 11.3.6.1
    }
    if (IsValueLend(type)) {
      return CType(type[1..]) + "*";   // a &T result of a value: the address of the place (spec 13.1.1)
    }
    if (IsFunType(type)) {
      return "df_fn";   // a function value { code, ctx } (spec 13.3.5)
    }
    if (IsCountedType(type) || IsWeakType(type)) {
      return "df_rc*";   // @Dog, _Dog: a counted or weak parameter or result, the counting block (spec 12.7)
    }
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
    if (IsArr(type)) {
      return ArrCType(ArrElem(type));
    }
    if (structs.TryGetValue(type, out var structInfo)) {
      return structInfo.CName;   // a struct of the program (spec 11.1.5)
    }
    if (IsTraitType(type)) {
      return "void*";
    }
    if (IsClass(type)) {
      return "c_" + type + "*";
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
    if (baseType == "string") {
      return "df_opt_string";   // defined by the runtime (string?, spec 7.1)
    }
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
    if (IsSetValue(type)) {
      return $"(({SetValueCType()}){{ {{ 0, 0 }}, 0u }})";   // the generic null of a value never assigned (spec 11.3.6.1)
    }
    if (IsValueLend(type)) {
      return "NULL";
    }
    if (IsFunType(type)) {
      return "((df_fn){0})";   // a function value starts as null (spec 13.3.2)
    }
    if (IsCountedType(type) || IsWeakType(type)) {
      return "NULL";
    }
    if (type == "string") {
      return "df_str_lit(\"\", 0)";
    }
    if (type == "string1k") {
      return "((df_s1k){0})";   // length 0, the text "" (spec 4.5)
    }
    if (IsNullable(type)) {
      return $"(({CType(type)}){{0}})";
    }
    if (IsArr(type)) {
      return $"(({CType(type)}){{0}})";
    }
    if (structs.TryGetValue(type, out var structInfo)) {
      return StructZero(structInfo);   // its start values, or zero (spec 11.1.5.1)
    }
    if (IsClass(type) || type == "addr") {
      return "NULL";
    }
    if (IsTuple(type) || IsMulti(type)) {
      var zeros = TupleElems(type).Select(elem => ZeroValue(elem.Type));
      return $"(({CType(type)}){{ {string.Join(", ", zeros)} }})";
    }
    if (enums.TryGetValue(type, out var info)) {
      return IntConstCode(info.Entries[0].Value, info.ValueType);
    }
    if (IsTimeType(type)) {
      return NullCode(type);   // date, clock and lapse start as null
    }
    return type == "bool" ? "false" : "0";
  }

  // ---------------------------------------------------------------- type nodes

  // inside the type arguments of a generic type, an array or a tuple: string? is not supported there yet
  int nestedTypeDepth = 0;

  string ResolveType(Node n) {
    bool nests = n.Kind == "TupleType" || n.Kind == "Type" && n.Children.Count > 0;
    if (nests) {
      nestedTypeDepth++;
    }
    try {
      string resolved = ResolveTypeCore(n);
      if (nests && structs.Count > 0 && !LibraryCode && n.Line < LibraryLine) {
        CheckStructItems(resolved, n);   // spec 11.1.5.3, not built yet
      }
      return resolved;
    } finally {
      if (nests) {
        nestedTypeDepth--;
      }
    }
  }

  string ResolveTypeCore(Node n) {
    switch (n.Kind) {
      case "Nullable": {
        string inner = ResolveType(n.Children[0]);
        if (inner == "string") {
          return "string?";   // null is not "" (spec 7.1)
        }
        if (IsTimeType(inner)) {
          throw Error($"{inner} is already nullable; '{inner}?' is not allowed", n);
        }
        if (IsArr(inner)) {
          throw Error("collections are already nullable; 'arr<T>?' is not allowed", n);
        }
        if (IsClass(inner)) {
          throw Error($"objects are already nullable; '{inner}?' is not allowed", n);
        }
        if (IsStruct(inner)) {
          throw Error($"a struct is never nullable; '{inner}?' is not allowed (spec 11.1.5)", n);
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
      case "ResolvedType":
        return n.Text!;   // a type parameter replaced by its type argument (generics)
      case "FunType":
        return ResolveFunType(n);   // fun(i32), string(char, char) (spec 13.3.2)
      case "Type": {
        string t = StripLibraryQualifier(n.Text!);
        if (t == "View" && vecPrelude) {
          return ViewTypeOf(n);   // View<vec<i32>>: the view class of that collection (spec 6.5)
        }
        if (t == "T" && n.Children.Count == 0 && currentGenericT != null) {
          return currentGenericT;   // inside a copy of equals(T other)
        }
        if (t is "map" or "bmap" && vecPrelude) {
          // map<K, V> (spec 6.8.4), alias bmap: the copy of the generic class map
          var mapClass = genericClasses["map"];
          var mapArgs = TypeArgsOf(n.Children, mapClass.Params);
          CheckMapArgs(mapArgs, n, false);
          return ClassInstanceName(mapClass, mapArgs, n);
        }
        if (t == "hmap" && vecPrelude) {
          // hmap<K, V> (spec 6.8.5): the copy of the generic class hmap
          var hmapClass = genericClasses["hmap"];
          var hmapArgs = TypeArgsOf(n.Children, hmapClass.Params);
          CheckMapArgs(hmapArgs, n, true);
          return ClassInstanceName(hmapClass, hmapArgs, n);
        }
        if (t is "set" or "bset" or "hset" && vecPrelude) {
          // set<T> (spec 6.8.6), alias bset, and hset<T> (spec 6.8.7): the copy of the generic class set or hset
          bool hashed = t == "hset";
          var setClass = genericClasses[hashed ? "hset" : "set"];
          var setArgs = TypeArgsOf(n.Children, setClass.Params);
          CheckSetArgs(setArgs, n, hashed);
          return ClassInstanceName(setClass, setArgs, n);
        }
        if (TypeFeatures.TryGetValue(t, out var feature)) {
          throw NotSupported(feature, n);
        }
        if (t == "arr") {
          if (n.Children.Count != 1) {
            throw Error("arr needs one element type: arr<T>", n);
          }
          string element = ResolveType(n.Children[0]);
          if (IsMulti(element)) {
            throw Error("an array cannot hold multiple values", n);
          }
          return "arr<" + element + ">";
        }
        if (genericClasses.TryGetValue(t, out var genericClass)) {
          if (genericClass.HasPlaceholders) {
            throw Error($"'{t}' has placeholders: it is not the type of a value, use it as a constraint, as in <Source : {t}>", n);
          }
          if (n.Children.Count == 0) {
            throw Error($"'{t}' is generic: write its type arguments, {t}<...>", n);
          }
          return ClassInstanceName(genericClass, TypeArgsOf(n.Children, genericClass.Params), n);
        }
        if (IsSetName(t)) {
          return t;   // a set as the type of a value: a closed union (spec 11.3.6.1)
        }
        if (n.Children.Count > 0) {
          throw Error($"'{t}' is not generic", n);
        }
        if (typeAliases.TryGetValue(t, out var aliased)) {
          return aliased;
        }
        if (!IsKnownType(t)) {
          throw Error($"unknown type '{t}'", n);
        }
        return Normalize(t);
      }
      case "Specific": {
        // specific T: one type known at compile time; handled as the trait itself for now (calls find the implementation at run time)
        string inner = ResolveType(n.Children[0]);
        if (!IsTraitType(inner)) {
          throw Error($"'specific' applies to a trait, not to {inner}", n);
        }
        return inner;
      }
      default:
        if (NodeFeatures.TryGetValue(n.Kind, out var nodeFeature)) {
          throw NotSupported(nodeFeature, n);
        }
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
    if (to == "string1k" && from == "string" || to == "string" && from == "string1k") {
      return true;   // string1k converts to and from string (spec 4.5)
    }
    if ((IsTuple(to) || IsMulti(to)) && IsStruct(from)) {
      return StructToTupleAssignable(from, to);   // a struct converts to a tuple (spec 11.1.5.8)
    }
    if (IsMulti(to) || IsMulti(from)) {
      return false;
    }
    if (from == "null") {
      return IsNullable(to) || to is "string" or "string1k" || IsTimeType(to) || IsArr(to) || IsClass(to) || to == "Exception" || IsFunType(to);
    }
    if (to == "Exception" && IsExceptionClass(from)) {
      return true;
    }
    if (IsClass(to) || IsClass(from)) {
      return IsClass(to) && IsClass(from) && IsSubtype(from, to);   // up the hierarchy only
    }
    if (IsNullable(to)) {
      string toBase = BaseOf(to);
      return Assignable(toBase, IsNullable(from) ? BaseOf(from) : from);
    }
    if (to is "string" or "string1k" && from is "string?" or "string1k?") {
      return true;   // a null string? or string1k? is "" (spec 7.1, 4.5)
    }
    if (IsNullable(from)) {
      return false;
    }
    if (to == "lapse" && IsNumeric(from)) {
      return true;     // a number is a lapse in milliseconds (spec 3.4)
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
    // unsigned to a wider signed type keeps the value
    if (Family(from) == "unsigned" && Family(to) == "signed" && Bits(from) < Bits(to)) {
      return true;
    }
    return Family(to) == Family(from) && IsNumeric(to) && Bits(from) <= Bits(to);
  }

  string Convert(Expr e, string to, Node at) {
    if (e.Type == to) {
      return e.Code;
    }
    if (to == "string1k" && e.Type == "string") {
      // cut to 1021 bytes at a whole character, with the silent StringTruncatedException (spec 4.5)
      return SilentCall($"df_s1k_from_str({StrRead(e)})", "string1k", "StringTruncatedException", at);
    }
    if (to == "string" && e.Type == "string1k") {
      return $"df_s1k_to_str({e.Code})";
    }
    if (IsSetValue(e.Type) && !LibraryCode) {
      throw Error($"a value of the set {DisplayName(e.Type)} is read with if k is T x {{ }} or k is ? {{ }} (spec 11.3.6.1)", at);
    }
    if (IsSetValue(to) && e.Type == "null") {
      var nullableMembers = SetValueMembers(to, at).Where(IsNullableMember).ToList();
      if (nullableMembers.Count == 1) {
        return WrapSetValue(new Expr(Convert(e, nullableMembers[0], at), nullableMembers[0]), to, at).Code;   // the null of its nullable member
      }
      throw Error(nullableMembers.Count == 0
        ? $"the set {DisplayName(to)} has no nullable member: its values are never null (spec 11.3.6.1)"
        : $"the set {DisplayName(to)} has more than one nullable member ({string.Join(", ", nullableMembers.Select(DisplayName))}): write which one, as in ({DisplayName(nullableMembers[0])})null (spec 11.3.6.1)", at);
    }
    if (IsSetValue(to)) {
      return WrapSetValue(e, to, at).Code;   // Nums k = count (spec 11.3.6.1)
    }
    if (IsStruct(e.Type) && (IsTuple(to) || IsMulti(to))) {
      return StructToTupleCode(e, to, at);   // a struct converts to a tuple (spec 11.1.5.8)
    }
    if (IsWeakType(e.Type) || IsWeakType(to)) {
      throw Error("a weak reference is used through enforce w { } else { } (spec 12.7)", at);
    }
    if (IsCountedType(e.Type) || IsCountedType(to)) {
      throw IsCountedType(e.Type)
        ? Error($"this call gives a counted {CountedBase(e.Type)}: keep it in a counted variable (@{CountedBase(e.Type)} x = ...) to use it (spec 12.7)", at)
        : Error($"a counted {CountedBase(to)} is expected here (spec 12.7)", at);
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
      if (to == "string1k") {
        return ZeroValue(to);   // a null string1k is "" (spec 4.5)
      }
      if (IsTimeType(to)) {
        return NullCode(to);
      }
      if (IsArr(to) || IsClass(to) || to == "addr") {
        return ZeroValue(to);
      }
      if (to == "Exception") {
        return "NULL";
      }
      if (IsFunType(to)) {
        return ZeroValue(to);
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
    if (e.Type == "string?" && to == "string") {
      return $"({e.Code}).val";   // a null string? is "" (spec 7.1)
    }
    if (e.Type == "string1k?" && to is "string1k" or "string") {
      // a null string1k? is "" (spec 4.5)
      return WithTemp(e, temp => Convert(new Expr($"({temp}.has ? {temp}.val : {ZeroValue("string1k")})", "string1k"), to, at));
    }
    if (e.Type == "string?" && to == "string1k") {
      return WithTemp(e, temp => Convert(new Expr($"{temp}.val", "string"), to, at));   // null gives "" (spec 4.5)
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
    if (to == "lapse" && IsNumeric(e.Type)) {
      // milliseconds: an integer gives a date lapse, a real a clock lapse (spec 3.4)
      return RealTypes.Contains(e.Type) ? $"df_lapse_from_ms_real((double)({e.Code}))" : $"df_lapse_from_ms((int64_t)({e.Code}))";
    }
    if (!Assignable(to, e.Type)) {
      throw Error($"cannot convert {e.Type} to {to} implicitly; use a cast", at);
    }
    return $"(({CType(to)})({e.Code}))";
  }

  // Reads the value once: simple code is used as it is, anything else goes into a temporary
  // (GNU statement expression, supported by clang).
  string WithTemp(Expr e, Func<string, string> build) {
    e = OptStrRead(e);   // a new string? that is only read ends with the statement
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
    e = StrReadExpr(e);
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
      return $"(df_str_len({e.Code}) != 0)";
    }
    if (e.Type == "date") {
      return $"(({e.Code}) != INT64_MIN)";
    }
    if (e.Type == "time") {
      return $"(({e.Code}) != -1)";
    }
    if (IsArr(e.Type)) {
      throw Error("collections cannot be converted to bool", at);
    }
    if (IsClass(e.Type)) {
      throw Error("objects cannot be converted to bool: compare with null", at);
    }
    if (e.Type == "lapse") {
      return $"df_lapse_bool({e.Code})";
    }
    return $"(({e.Code}) != 0)";
  }

  // the text of an item of a struct or a collection: a function value prints as its type, or null (spec 13.3.2), as a field of a class does
  string ItemStrOf(Expr e, Node at) => IsFunType(e.Type)
    ? WithTemp(e, held => $"({held}.code == NULL ? df_str_lit(\"null\", 4) : {CString(CodePoints(FunDisplay(e.Type)))})")
    : StrOf(e, at);

  string StrOf(Expr e, Node at) {
    if (e.Type == "string1k") {
      return $"df_s1k_to_str({e.Code})";   // spec 4.5
    }
    if (IsSetValue(e.Type)) {
      return SetValueStr(e, at);
    }
    if (IsStruct(e.Type)) {
      return $"{StructPrinter(structs[e.Type])}({TempStruct(e).Code})";   // Vec3 { x: 1, y: 2, z: 3 } (spec 11.1.5.9)
    }
    if (e.Type == "string") {
      return e.Code;
    }
    if (e.Fresh && IsClass(e.Type)) {
      e = TempObject(e);   // log(v.clone()): the new collection or object ends with the statement
    } else if (e.Fresh && IsArr(e.Type) && !LibraryCode) {
      // log(a.filter(...)): the new array ends once its text is made
      string held = NewTemp();
      string text = NewTemp();
      return $"({{ {CType(e.Type)} {held} = {e.Code}; df_string {text} = {StrOf(new Expr(held, e.Type), at)}; {ArrDropHelper(e.Type)}({held}); {text}; }})";
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
    if (IsFunType(e.Type)) {
      if (LibraryCode || currentFun == null) {
        return WithTemp(e, held => $"({held}.code == NULL ? df_str_lit(\"null\", 4) : {CString(CodePoints(FunDisplay(e.Type)))})");   // an item of a collection
      }
      throw Error("a function value has no text", at);
    }
    if (e.Type == "string?") {
      // always a new string, as for the other types: the text is copied (a null prints as null)
      return WithTemp(e, temp => $"({temp}.has ? df_str_dup({temp}.val) : df_str_lit(\"null\", 4))");
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
            code = $"df_str_catx({code}, df_str_lit(\", \", 2))";
          }
          string join = elems[index].Type == "string" ? "df_str_cat" : "df_str_catx";
          code = $"{join}({code}, {StrOf(new Expr($"{temp}.e{index}", elems[index].Type), at)})";
        }
        return $"df_str_catx({code}, df_str_lit(\")\", 1))";
      });
    }
    if (IsEnum(e.Type)) {
      return $"df_enum_str_{e.Type}({e.Code})";
    }
    if (IsArr(e.Type)) {
      return $"{ArrStrFunction(ArrElem(e.Type), at)}({e.Code})";
    }
    if (IsVec(e.Type)) {
      return $"{VecStrFunction(e.Type, at)}({e.Code})";   // [a, b, c], like an array
    }
    if (IsMap(e.Type)) {
      return $"{MapStrFunction(e.Type, at)}({e.Code})";
    }
    if (IsSet(e.Type)) {
      return $"{SetStrFunction(e.Type, at)}({e.Code})";
    }
    if (MapViewKind(e.Type) != null) {
      return MapViewStr(e, at);
    }
    if (IsList(e.Type)) {
      return $"{ListStrFunction(e.Type, at)}({e.Code})";
    }
    if (IsView(e.Type)) {
      return $"{ViewStrFunction(e.Type, at)}({e.Code})";   // the items of the view, as [a, b]
    }
    if (IsClass(e.Type)) {
      return $"df_str_obj({e.Code})";
    }
    if (e.Type == "Exception") {
      throw Error("Exception cannot be converted to a string yet; use its message", at);
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
      "date" => $"df_str_from_date({e.Code})",
      "time" => Need("date", $"df_str_from_time({e.Code})"),
      "clock" => $"df_str_from_clock({e.Code})",
      "lapse" => $"df_str_from_lapse({e.Code})",
      "addr" => $"df_str_from_addr({e.Code})",
      _ => throw Error($"{e.Type} cannot be converted to a string yet", at)
    };
  }

  // ---------------------------------------------------------------- equality

  // C bool expression for l == r. Null equals only null.
  string EqualsCode(Expr l, Expr r, Node at) {
    if (IsSetValue(l.Type) || IsSetValue(r.Type)) {
      return SetValueEquals(l, r, at);   // spec 11.3.6.1
    }
    if (IsStruct(l.Type) || IsStruct(r.Type)) {
      return StructEquals(l, r, at);   // field by field (spec 11.1.5.9)
    }
    if (l.Type == "null" && r.Type == "null") {
      return "true";
    }
    if (l.Type == "null") {
      (l, r) = (r, l);
    }
    if (IsTimeType(l.Type) || IsTimeType(r.Type)) {
      if (r.Type == "null") {
        return $"({l.Code} == {NullCode(l.Type)})";
      }
      if (l.Type != r.Type) {
        throw Error($"cannot compare {l.Type} and {r.Type}", at);
      }
      return $"({l.Code} == {r.Code})";
    }
    if (IsClass(l.Type)) {
      return ObjectEquals(l, r, at);   // equals, the operator ==, or identity (11.1.3)
    }
    if (IsFunType(l.Type) && IsFunType(r.Type) && (LibraryCode || currentFun == null)) {
      return $"({{ df_fn a_ = {l.Code}; df_fn b_ = {r.Code}; a_.code == b_.code && a_.ctx == b_.ctx; }})";   // has, indexOf of the library
    }
    if (IsFunType(l.Type) || IsFunType(r.Type)) {
      // a function value is compared with null only (spec 13.3.2)
      var value = IsFunType(l.Type) ? l : r;
      if ((IsFunType(l.Type) ? r : l).Type != "null") {
        throw Error("a function value is compared only with null", at);
      }
      return $"(({value.Code}).code == NULL)";
    }
    if (l.Type == "addr" || r.Type == "addr") {
      // addr (spec 2.3): compared with another addr or with null (address 0)
      if (!(l.Type == "addr" && r.Type is "addr" or "null")) {
        throw Error($"cannot compare {l.Type} and {r.Type}", at);
      }
      return r.Type == "null" ? $"(({l.Code}) == NULL)" : $"(({l.Code}) == ({r.Code}))";
    }
    if (IsArr(l.Type) && (IsArr(r.Type) || r.Type == "null")) {
      // collections compare by identity (spec 6.1)
      return r.Type == "null" ? WithTemp(l, temp => $"({temp}.p == NULL)")
        : WithTemp(l, leftTemp => WithTemp(r, rightTemp => $"({leftTemp}.p == {rightTemp}.p)"));
    }
    if (r.Type == "null") {
      if (l.Type == "Exception") {
        return $"(({l.Code}) == NULL)";
      }
      if (IsNullable(l.Type)) {
        return WithTemp(l, temp => $"(!{temp}.has)");
      }
      if (l.Type == "string") {
        return WithTemp(l, temp => $"(df_str_len({temp}) == 0)");
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
    if (l.Type == "string1k" && r.Type == "string1k") {
      return $"df_s1k_eq({l.Code}, {r.Code})";   // the values of two string1k? (spec 4.5)
    }
    if (l.Type == "string1k" && r.Type == "string" || l.Type == "string" && r.Type == "string1k") {
      // the value of a string1k? with a string: on the string it converts to (spec 4.5)
      string ToStr(Expr side) => side.Type == "string1k" ? StrRead(new Expr($"df_s1k_to_str({side.Code})", "string", true)) : side.Code;
      return $"df_str_eq({ToStr(l)}, {ToStr(r)})";
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
