using System.Text;
using dfparser;

namespace dfcompiler;

// A set value that owns a string, an object or a tuple as a key (spec 11.3.6.1, 6.8.4.1, 6.8.5.1): it goes the way of the composite keys.
// The collection keeps a boxed clone (df_tk_box_); the order is the tag first (its sort key is the hash of the type, df_svk_tag_), then the
// value by the order of its member (strings by bytes, objects by hash, tuples item by item); an hmap compares the hash, then the order.
public sealed partial class CodeGen {
  string SetKeyBoxHelpers(string set, Node? at) {
    string name = SanitizeName(set);
    if (!compositeKeyHelpers.Add(set)) {
      return name;
    }
    var node = at != null && at.Line < LibraryLine ? at : genericSets[set];   // reported in the program, not in the library
    NeedChunk("map");
    NeedChunk("hash");
    hashUsed = true;
    var members = SetValueMembers(set, node);
    string ct = SetValueCType();
    string helpers = SetOwnedHelpers(set, node);
    foreach (string member in members.Where(IsClass)) {
      if (!traits.ContainsKey("Keyable") || !IsSubtype(member, "Keyable")) {
        throw Error($"{DisplayName(member)} is not Keyable: the set {DisplayName(set)} is a key, so its classes implement the trait Keyable (spec 6.2, 11.2.1)", node);
      }
      CheckKeyable(member, node);
    }
    foreach (string member in members.Where(IsTuple)) {
      if (!IsCompositeKey(member)) {
        throw Error($"{DisplayName(member)} cannot be part of a key (spec 6.2): the set {DisplayName(set)} is a key", node);
      }
      CompositeKeyHelpers(member, node);
    }
    var sb = new StringBuilder();
    sb.Append($"static uint64_t df_svk_tag_{name}({ct} v) {{\n  switch (v.tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"    case {index + 1}u: return (uint64_t){SetTagHash(members[index])};\n");
    }
    sb.Append("    default: return 0;\n  }\n}\n\n");
    sb.Append($"static {ct}* df_tk_box_{name}({ct} v) {{\n  {ct}* box = ({ct}*)df_alloc(sizeof({ct}));\n  *box = df_svclone_{helpers}(v);\n  return box;\n}}\n\n");
    sb.Append($"static void df_tk_free_{name}(df_bt_kw w) {{\n  {ct}* box = ({ct}*)(uintptr_t)w;\n  if (box == NULL) {{\n    return;\n  }}\n  df_svdrop_{helpers}(box);\n  free(box);\n}}\n\n");
    sb.Append($"static int df_tk_cmp_{name}(df_bt_kw a, df_bt_kw b) {{\n  const {ct}* x = (const {ct}*)(uintptr_t)a;\n  const {ct}* y = (const {ct}*)(uintptr_t)b;\n");
    sb.Append($"  uint64_t p = df_svk_tag_{name}(*x), q = df_svk_tag_{name}(*y);\n  if (p != q) {{\n    return p < q ? -1 : 1;\n  }}\n");
    sb.Append("  if (x->tag != y->tag) {\n    return x->tag < y->tag ? -1 : 1;\n  }\n  switch (x->tag) {\n");
    for (int index = 0; index < members.Count; index++) {
      string member = members[index];
      string storage = IsTuple(member) ? CType(member) + "*" : CType(member);
      string Read(string side) => $"({{ {storage} m; memcpy(&m, {side}->bits, sizeof m); m; }})";
      string compare = member == "string" ? $"return df_bt_cmp_str((df_bt_kw)(uintptr_t){Read("x")}, (df_bt_kw)(uintptr_t){Read("y")});"
        : IsClass(member) ? $"{{ uint64_t h = (uint64_t)df_hash_obj((const void*){Read("x")}), k = (uint64_t)df_hash_obj((const void*){Read("y")}); return h < k ? -1 : h > k ? 1 : 0; }}"
        : IsTuple(member) ? $"return df_tk_cmp_{CompositeKeyHelpers(member, node)}((df_bt_kw)(uintptr_t){Read("x")}, (df_bt_kw)(uintptr_t){Read("y")});"
        : $"{{ uint64_t h = {SortKey(member, UnwrapSetValue("(*x)", member))}, k = {SortKey(member, UnwrapSetValue("(*y)", member))}; return h < k ? -1 : h > k ? 1 : 0; }}";
      sb.Append($"    case {index + 1}u: {compare}\n");
    }
    sb.Append("    default: return 0;\n  }\n}\n\n");
    sb.Append($"static df_uns df_tk_hash_{name}(const void* slot) {{\n  return (df_uns)((const uint64_t*)slot)[0];\n}}\n\n");
    sb.Append($"static bool df_tk_eq_{name}(const void* a, const void* b) {{\n  const uint64_t* x = (const uint64_t*)a;\n  const uint64_t* y = (const uint64_t*)b;\n");
    sb.Append($"  return x[0] == y[0] && df_tk_cmp_{name}((df_bt_kw)x[1], (df_bt_kw)y[1]) == 0;\n}}\n\n");
    arrayCode.Append(sb);
    return name;
  }
}
