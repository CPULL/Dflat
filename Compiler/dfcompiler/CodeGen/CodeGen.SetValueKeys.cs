using System.Text;
using dfparser;

namespace dfcompiler;

// Set values as keys and their hash (spec 11.3.6.1, 6.8.4.1, 6.8.5.1). The tag of a member is the hash of its type name (6.2.1, the
// same as "i32".hash). .hash of a set value is tag $ the hash of the value. Keys: every member exact (numbers, bool, char, enums,
// bitfields; no nullable member yet).
//  - map / set (B-tree): the multi-type exact layout of 6.8.4.1, 16 bytes inline: the tag as the sort key and the sort key of the value
//    in the second word of the key slot (df_bt_kw), compared as numbers on a tie (df_bt_cmp_word); nothing on the heap.
//  - hmap / hset: a key slot of two words, the tag and the value bits (zero-extended, a real normalized), compared as bytes; the hash of
//    the slot is the tag XOR the hash of the value (6.8.5.1).
public sealed partial class CodeGen {
  readonly HashSet<string> setKeyHelpers = new();

  // the members a key of this set can have: exact natives only (no null of a member)
  bool SetValueKeyBuilt(string set) => SetMembers(set).All(member => {
    string plain = enums.TryGetValue(member, out var info) ? info.ValueType : member;
    return !member.EndsWith('?') && (IsInt(plain) || plain is "bool" or "char" or "r32" or "r64");
  });

  // the tag of a member: the hash of the name of its type, a uns constant
  string SetTagHash(string member) => HashConst(QualifiedTypeName(member));

  // the C name part of a set
  static string SetCName(string set) => System.Text.RegularExpressions.Regex.Replace(set, @"[^A-Za-z0-9_]", "_");

  // k.hash: tag $ the hash of the value; the generic null hashes as null
  string SetValueHash(Expr e, Node at) {
    var members = SetValueMembers(e.Type, at);
    string held = NewTemp();
    string hash = NewTemp();
    var sb = new StringBuilder($"({{ {SetValueCType()} {held} = {e.Code}; df_uns {hash} = DF_HASH_NULL; switch ({held}.tag) {{ ");
    for (int index = 0; index < members.Count; index++) {
      string member = members[index];
      sb.Append($"case {index + 1}u: {hash} = df_hash_comb({SetTagHash(member)}, {HashCode(new Expr(UnwrapSetValue(held, member), member), at)}); break; ");
    }
    sb.Append($"default: break; }} {hash}; }})");
    return sb.ToString();
  }

  // the C helpers of the keys of a set, emitted once: the tag as a sort key, the sort key of the value, the comparison, the hmap slot
  string SetKeyHelpers(string set, Node at) {
    string name = SetCName(set);
    if (!setKeyHelpers.Add(set)) {
      return name;
    }
    if (!SetValueKeyBuilt(set)) {
      throw NotSupported($"Keys of the set {DisplayName(set)}: every member must be an exact native (numbers, bool, char, enums, bitfields), with no nullable member", at);
    }
    var members = SetValueMembers(set, at);
    string ct = SetValueCType();
    NeedChunk("map");   // df_bt_kw and the sort keys (RuntimeMap.cs), also for a program with only hset keys
    var sb = new StringBuilder();
    if (setKeyHelpers.Add("$word")) {
      sb.Append("static int df_bt_cmp_word(df_bt_kw a, df_bt_kw b) {\n  return a < b ? -1 : a > b ? 1 : 0;\n}\n\n");
    }
    // the tag as the sort key: the keys are ordered by tag first (6.2.2, 11.3.6.1)
    sb.Append($"static uint64_t df_setv_tag_{name}({ct} v) {{\n  switch (v.tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"    case {index + 1}u: return (uint64_t){SetTagHash(members[index])};\n");
    }
    sb.Append("    default: return 0;\n  }\n}\n\n");
    // the order of two values of the same member
    sb.Append($"static uint64_t df_setv_sk_{name}({ct} v) {{\n  switch (v.tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"    case {index + 1}u: return {SortKey(members[index], UnwrapSetValue("v", members[index]))};\n");
    }
    sb.Append("    default: return 0;\n  }\n}\n\n");
    // the key back from the tag and the sort key of its value (a cursor of map / set)
    sb.Append($"static {ct} df_setv_unkey_{name}(uint64_t tag, df_bt_kw word) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"  if (tag == (uint64_t){SetTagHash(members[index])}) {{\n    return {WrapSetValue(new Expr(FromSortKey(members[index], "word"), members[index]), set, at).Code};\n  }}\n");
    }
    sb.Append($"  return {ZeroValue(set)};\n}}\n\n");
    // hmap: the bits of the value, the hash of a slot, the value back from a slot
    sb.Append($"static uint64_t df_setv_bits_{name}({ct} v) {{\n  switch (v.tag) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"    case {index + 1}u: return {HmapKeyBits(members[index], UnwrapSetValue("v", members[index]))};\n");
    }
    sb.Append("    default: return 0;\n  }\n}\n\n");
    sb.Append($"static df_uns df_hk_setv_{name}(const void* key) {{\n  const uint64_t* w = (const uint64_t*)key;\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"  if (w[0] == (uint64_t){SetTagHash(members[index])}) {{\n    return (df_uns)(w[0] ^ (uint64_t){HmapBitsHash(members[index], "w[1]")});\n  }}\n");
    }
    sb.Append("  return DF_HASH_NULL;\n}\n\n");
    sb.Append($"static {ct} df_setv_slot_{name}(const uint64_t* w) {{\n");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"  if (w[0] == (uint64_t){SetTagHash(members[index])}) {{\n    return {WrapSetValue(new Expr(HmapKeyFromBits(members[index], "w[1]"), members[index]), set, at).Code};\n  }}\n");
    }
    sb.Append($"  return {ZeroValue(set)};\n}}\n\n");
    arrayCode.Append(sb);
    return name;
  }
}
