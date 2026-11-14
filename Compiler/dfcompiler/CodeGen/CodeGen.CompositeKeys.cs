using System.Text;
using dfparser;

namespace dfcompiler;

// Tuples and structs as keys of map, set, hmap, hset (spec 6.2, 6.2.2, 6.8.4.1, 6.8.5.1). Built: tuples whose components are exact natives
// (numbers, bool, char, enums, bitfields), strings or objects of Keyable classes (cloned into the key's copy), and structs whose fields are exact natives, strings or objects of Keyable classes (no
// buffer). A struct with strings or objects owns them (11.1.5.3): the collection's copy is a clone (df_sclone_), dropped with the entry
// (df_sdrop_). An object field is ordered by its hash (the default order of 6.2.2) and an hmap compares it with the equals of its class when
// the class replaces it.
//  - map / set: the layout with a pointer: the sort key is the one of the first component, the key word points to the map's own copy of the
//    key (on the heap, its strings copied); a tie is compared item by item (df_tk_cmp_<T>).
//  - hmap / hset: a slot of two words, the full hash and a pointer to the key (the hmap's own copy once added); equal when the hashes match and
//    the components are equal (df_tk_eq_<T>).
// A key read from the collection (.keys, a for, a cursor) is the copy of the collection: its strings are lent while the entry exists.
public sealed partial class CodeGen {
  readonly HashSet<string> compositeKeyHelpers = new();

  // the components of a tuple or a struct key: their C names and types
  // a string1k key alone is a key of one component with no name (spec 4.5): Part reads it from the key itself
  // a string? or string1k? key is its value then its flag (spec 7.1): null is the null slot of a map / set, and in an hmap / hset it hashes as
  // "null".hash and differs from "" by the flag
  // a tuple or a struct inside the key is flattened into its own components, with the C path to each (e0.e1, f_corner.f_x): the order, the
  // copy, the end and the equality work on these leaves, left to right, as on the components of a flat key (spec 6.2.2)
  List<(string CName, string Type)> KeyComponents(string type) => type == "string1k" ? new() { ("", "string1k") }
    : type is "string?" or "string1k?" ? new() { ("val", BaseOf(type)), ("has", "bool") }
    : (IsTuple(type)
      ? TupleElems(type).Select((elem, index) => ($"e{index}", elem.Type))
      : structs[type].Fields.Select(field => ($"f_{field.Name}", field.Type)))
      .SelectMany(part => IsNestedKeyPart(part.Item2)
        ? KeyComponents(part.Item2).Select(inner => ($"{part.Item1}.{inner.CName}", inner.Type))
        : new[] { (part.Item1, part.Item2) }).ToList();

  // a tuple or a struct used as a component of a tuple or a struct key (spec 6.2)
  bool IsNestedKeyPart(string type) => (IsTuple(type) || IsStruct(type)) && IsCompositeKey(type);

  // a component of a composite key read through a pointer (ptr->name) or from a value (value.name); the unnamed one of a string1k key is the key
  static string Part(string pointer, string cname) => cname == "" ? $"(*{pointer})" : $"{pointer}->{cname}";
  static string ValuePart(string value, string cname) => cname == "" ? value : $"{value}.{cname}";

  bool IsExactKeyPart(string type) {
    string plain = enums.TryGetValue(type, out var info) ? info.ValueType : type;
    return IsInt(plain) || plain is "bool" or "char" or "r32" or "r64";
  }

  // an object field of a struct key: a class (Keyable is checked by CompositeKeyHelpers, the error names the trait)
  bool IsObjectKeyPart(string type) => IsObjectKey(type) && !IsCollectionType(type);

  // a tuple or a struct this version takes as a key
  bool IsCompositeKey(string type) =>
    type is "string1k" or "string?" or "string1k?"
    || IsTuple(type) && TupleElems(type).All(elem => IsKeyLeafOrNested(elem.Type))
    || IsStruct(type) && structs[type].Fields.Count > 0
      && structs[type].Fields.All(field => field.Buffer == null && IsKeyLeafOrNested(field.Type));

  // a component a tuple or a struct key can have: an exact native, a string kind, a Keyable object, or a tuple or a struct of them
  bool IsKeyLeafOrNested(string type) =>
    IsExactKeyPart(type) || type is "string" or "string1k" or "string?" or "string1k?" || IsObjectKeyPart(type) || IsNestedKeyPart(type);

  // a composite key of map / set (the B-tree) and of hmap / hset
  bool TKeyOf(string type) => !IsHmap(type) && IsCompositeKey(MapKey(type));
  bool HTKeyOf(string type) => IsHmap(type) && IsCompositeKey(MapKey(type));

  // the C helpers of a composite key type, emitted once: the copy the collection keeps, its end, the order, the hash, the equality
  string CompositeKeyHelpers(string keyType, Node? at = null) {
    string name = SanitizeName(keyType);
    if (!compositeKeyHelpers.Add(keyType)) {
      return name;
    }
    NeedChunk("map");   // df_bt_kw and the sort keys (RuntimeMap.cs)
    NeedChunk("hash");
    hashUsed = true;
    string ct = CType(keyType);
    var parts = KeyComponents(keyType);
    if (parts.Any(part => part.Type is "string1k" or "string1k?")) {
      NeedChunk("sort");   // df_cmp_s1k (RuntimeSort.cs)
      S1kSortKeyHelper();
    }
    CheckKeyableParts(keyType, keyType, at);
    bool owning = IsStruct(keyType) && StructHoldsObjects(keyType);
    if (owning) {
      cloneUsed = true;
    }
    var sb = new StringBuilder();
    // the collection's own copy: the strings are copied, a struct is cloned with its strings and objects (11.1.5.3)
    sb.Append($"static {ct}* df_tk_box_{name}({ct} v) {{\n  {ct}* box = ({ct}*)df_alloc(sizeof({ct}));\n");
    if (owning) {
      sb.Append($"  *box = {StructCloneHelper(keyType)}(v);\n");
    } else {
      sb.Append("  *box = v;\n");
      foreach (var (cname, type) in parts.Where(part => part.Type == "string")) {
        sb.Append($"  box->{cname} = df_str_dup(v.{cname});\n");
      }
      foreach (var (cname, type) in parts.Where(part => part.Type == "string?")) {
        sb.Append($"  box->{cname} = df_opt_str_dup(v.{cname});\n");   // a string? part (spec 7.1)
      }
      foreach (var (cname, type) in parts.Where(part => IsObjectKeyPart(part.Type))) {
        cloneUsed = true;
        sb.Append($"  box->{cname} = {CloneValue($"v.{cname}", type, at!)};\n");   // the key is the collection's own: its objects are cloned (spec 3.2)
      }
    }
    sb.Append("  return box;\n}\n\n");
    sb.Append($"static void df_tk_free_{name}(df_bt_kw w) {{\n  {ct}* box = ({ct}*)(uintptr_t)w;\n  if (box == NULL) {{\n    return;\n  }}\n");
    if (owning) {
      sb.Append($"  {StructDropHelper(keyType)}(box);\n");
    } else {
      foreach (var (cname, type) in parts.Where(part => part.Type == "string")) {
        sb.Append($"  df_str_free(box->{cname});\n");
      }
      foreach (var (cname, type) in parts.Where(part => part.Type == "string?")) {
        sb.Append($"  df_str_free(box->{cname}.val);\n");
      }
      foreach (var (cname, type) in parts.Where(part => IsObjectKeyPart(part.Type))) {
        sb.Append($"  df_drop((void*)box->{cname});\n");
      }
    }
    sb.Append("  free(box);\n}\n\n");
    // the order: item by item, left to right (6.2.2)
    sb.Append($"static int df_tk_cmp_{name}(df_bt_kw a, df_bt_kw b) {{\n  const {ct}* x = (const {ct}*)(uintptr_t)a;\n  const {ct}* y = (const {ct}*)(uintptr_t)b;\n  int c;\n");
    foreach (var (cname, type) in parts) {
      if (IsObjectKeyPart(type)) {
        // the default order of classes: the hash (6.2.2)
        sb.Append($"  {{\n    uint64_t p = (uint64_t)df_hash_obj((const void*)x->{cname}), q = (uint64_t)df_hash_obj((const void*)y->{cname});\n    if (p != q) {{\n      return p < q ? -1 : 1;\n    }}\n  }}\n");
      } else if (type == "string") {
        sb.Append($"  c = df_bt_cmp_str((df_bt_kw)(uintptr_t)x->{cname}, (df_bt_kw)(uintptr_t)y->{cname});\n  if (c != 0) {{\n    return c;\n  }}\n");
      } else if (type is "string?" or "string1k?") {
        // null first (6.2.2), then the values
        sb.Append($"  if (x->{cname}.has != y->{cname}.has) {{\n    return x->{cname}.has ? 1 : -1;\n  }}\n  if (x->{cname}.has) {{\n");
        sb.Append(type == "string?"
          ? $"    c = df_bt_cmp_str((df_bt_kw)(uintptr_t)x->{cname}.val, (df_bt_kw)(uintptr_t)y->{cname}.val);\n"
          : $"    c = df_cmp_s1k(&x->{cname}.val, &y->{cname}.val);\n");
        sb.Append("    if (c != 0) {\n      return c;\n    }\n  }\n");
      } else if (type == "string1k") {
        sb.Append($"  c = df_cmp_s1k(&{Part("x", cname)}, &{Part("y", cname)});\n  if (c != 0) {{\n    return c;\n  }}\n");   // by bytes (6.2.2)
      } else {
        sb.Append($"  {{\n    uint64_t p = {SortKey(type, $"x->{cname}")}, q = {SortKey(type, $"y->{cname}")};\n    if (p != q) {{\n      return p < q ? -1 : 1;\n    }}\n  }}\n");
      }
    }
    sb.Append("  return 0;\n}\n\n");
    // the hmap slot: the full hash, then the key; equal when the hashes match and every component is equal
    sb.Append($"static df_uns df_tk_hash_{name}(const void* slot) {{\n  return (df_uns)((const uint64_t*)slot)[0];\n}}\n\n");
    sb.Append($"static bool df_tk_eq_{name}(const void* a, const void* b);   /* written with the hmap shape (CompositeKeyEquality) */\n\n");
    arrayCode.Append(sb);
    return name;
  }

  // every object held by the key, also inside a nested tuple or struct, is of a Keyable class (spec 6.2, 11.2.1)
  void CheckKeyableParts(string keyType, string partType, Node? at) {
    if (IsStruct(partType)) {
      foreach (var field in structs[partType].Fields) {
        if (IsObjectKeyPart(field.Type)) {
          if (!traits.ContainsKey("Keyable") || !IsSubtype(field.Type, "Keyable")) {
            throw Error($"{DisplayName(field.Type)} is not Keyable: the field '{field.Name}' of {DisplayName(partType)}, {(partType == keyType ? "a key" : $"in the key {DisplayName(keyType)}")}, holds it; a class used in a key implements the trait Keyable (spec 6.2, 11.2.1)", field.At);
          }
          CheckKeyable(field.Type, field.At);   // the helpers of the object keys
        } else if (IsNestedKeyPart(field.Type)) {
          CheckKeyableParts(keyType, field.Type, field.At);
        }
      }
    } else if (IsTuple(partType)) {
      foreach (var (index, elem) in TupleElems(partType).Select((elem, index) => (index, elem))) {
        if (IsObjectKeyPart(elem.Type)) {
          if (!traits.ContainsKey("Keyable") || !IsSubtype(elem.Type, "Keyable")) {
            throw Error($"{DisplayName(elem.Type)} is not Keyable: the item {index} of {DisplayName(partType)}, {(partType == keyType ? "a key" : $"in the key {DisplayName(keyType)}")}, holds it; a class used in a key implements the trait Keyable (spec 6.2, 11.2.1)", at!);
          }
          CheckKeyable(elem.Type, at!);
        } else if (IsNestedKeyPart(elem.Type)) {
          CheckKeyableParts(keyType, elem.Type, at);
        }
      }
    }
  }

  // the equality of two hmap slots of a composite key, emitted once with the shape of the hmap (the equals of the classes are known by then):
  // the same hash, the same order, then the equals of the object fields whose class replaces it. The body goes with the lambda helpers, after
  // the tables of equals
  string CompositeKeyEquality(string keyType) {
    string name = CompositeKeyHelpers(keyType);
    if (!compositeKeyHelpers.Add("$eq " + keyType)) {
      return name;
    }
    string ct = CType(keyType);
    var withEquals = KeyComponents(keyType).Where(part => IsObjectKeyPart(part.Type) && FindEqualsClass(classes[part.Type]) != null).ToList();
    var eq = new StringBuilder($"static bool df_tk_eq_{name}(const void* a, const void* b) {{\n  const uint64_t* x = (const uint64_t*)a;\n  const uint64_t* y = (const uint64_t*)b;\n"
      + $"  if (x[0] != y[0] || df_tk_cmp_{name}((df_bt_kw)x[1], (df_bt_kw)y[1]) != 0) {{\n    return false;\n  }}\n");
    if (withEquals.Count > 0) {
      eq.Append($"  const {ct}* l = (const {ct}*)(uintptr_t)x[1];\n  const {ct}* r = (const {ct}*)(uintptr_t)y[1];\n");
      foreach (var (cname, type) in withEquals) {
        eq.Append($"  if (!{ObjectEqualsHelper(type)}(l->{cname}, r->{cname})) {{\n    return false;\n  }}\n");
      }
    }
    eq.Append("  return true;\n}\n\n");
    lambdaHelpers.Append(eq);
    return name;
  }

  // the C function that compares two objects of a class that replaces equals (a failure of equals counts as different), in lambdaHelpers
  string ObjectEqualsHelper(string type) {
    string name = "df_obj_eq_" + SanitizeName(type);
    if (arrayHelpers.Add(name)) {
      string ct = CType(type);
      string pair = ResultPair("bool");
      string table = EqualsTable(type);
      arrayCode.Append($"static bool {name}({ct} l, {ct} r);\n");
      lambdaHelpers.Append($"static bool {name}({ct} l, {ct} r) {{\n  if (l == r) {{\n    return true;\n  }}\n  if (l == NULL || r == NULL) {{\n    return false;\n  }}\n"
        + $"  {pair} p = (({pair}(*)(void*, {ct}))({table}[l->cls->id - 256]))((void*)l, r);\n"
        + "  if (p.err) {\n    df_exc_release(p.err);\n    return false;\n  }\n  return p.value;\n}\n\n");
    }
    return name;
  }

  // the sort key of a string1k: its first 8 bytes, as for a string (the bytes after the ending 0 are not read)
  void S1kSortKeyHelper() {
    if (arrayHelpers.Add("df_bt_key_s1k")) {
      arrayCode.Append("static uint64_t df_bt_key_s1k(const df_s1k* s) {\n  uint64_t key = 0;\n  bool ended = false;\n  for (unsigned i = 0; i < 8; i++) {\n"
        + "    ended = ended || s->b[i] == 0;\n    key = (key << 8) | (ended ? 0 : s->b[i]);\n  }\n  return key;\n}\n\n");
    }
  }

  // the sort key of a composite key held in key: the one of its first component (6.8.4.1)
  string CompositeSortKey(string keyType, string key) {
    var (cname, type) = KeyComponents(keyType)[0];
    if (type == "string1k") {
      return $"df_bt_key_s1k(&({ValuePart($"({key})", cname)}))";
    }
    if (type is "string?" or "string1k?") {
      // null first: its sort key is 0, and a tie is decided by df_tk_cmp_ (the flag, then the value)
      string part = ValuePart($"({key})", cname);
      return type == "string?" ? $"({part}.has ? df_bt_key_str({part}.val) : (uint64_t)0)" : $"({part}.has ? df_bt_key_s1k(&{part}.val) : (uint64_t)0)";
    }
    return type == "string" ? $"df_bt_key_str(({key}).{cname})" : IsObjectKeyPart(type) ? $"((uint64_t)df_hash_obj((const void*)({key}).{cname}))" : SortKey(type, $"({key}).{cname}");
  }
}
