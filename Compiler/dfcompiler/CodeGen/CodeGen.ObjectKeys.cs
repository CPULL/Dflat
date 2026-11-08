using System.Text;
using dfparser;

namespace dfcompiler;

// Objects as keys (spec 6.2, 6.2.3, 11.2.1): a class used as a key of map, set, hmap or hset implements the trait Keyable (a compile error
// otherwise). The collection owns its keys (6.6): a key goes in as a new object or ->x (user code), the copying paths of the library
// (add(other), clone) clone it, a lookup borrows it. The trait Keyable (from the prelude) holds two private fields per object, the number of
// entries that hold the object as a key and the hash stored at the first one. Every use of a key by a collection runs the check: at 0 uses
// nothing is checked; at the step from 0 to 1 the hash is stored; above 0 a different hash raises ModifiedObjectUsedAsKeyException (52).
// The order of a map and a set is the default order of 6.2.2 (the hashes: Comparable<T> is not built yet), so two objects with the same
// hash are the same key; an hmap compares the hashes too (not equals yet).
public sealed partial class CodeGen {
  // set by MapKeyArg: the key of the operation is given to the collection (a new object or ->x)
  bool lastKeyGiven;

  // a class key (not an array, a collection is refused before)
  bool IsObjectKey(string keyType) => IsClass(keyType) && !IsArr(keyType) && !IsCountedType(keyType);

  // the class implements Keyable: it can be a key (spec 6.2)
  void CheckKeyable(string keyType, Node at) {
    if (!traits.ContainsKey("Keyable") || !IsSubtype(keyType, "Keyable")) {
      throw Error($"{DisplayName(keyType)} is not Keyable: a class used as a key implements the trait Keyable (spec 6.2, 11.2.1)", at);
    }
    ObjectKeyHelpers();
  }

  // the C helpers of the object keys, emitted once: the check of a use, the step that adds an entry, the order and the equality
  void ObjectKeyHelpers() {
    if (!arrayHelpers.Add("$objectkeys")) {
      return;
    }
    NeedChunk("hash");
    NeedChunk("map");   // df_bt_kw and the order of the tree keys (RuntimeMap.cs), also for a program with only hmap keys
    hashUsed = true;
    var sb = new StringBuilder();
    sb.Append("static const df_trait df_trait_Keyable;   /* defined with the other trait tables */\n");
    sb.Append("/* the two fields of Keyable in an object: the entries that hold it as a key and the stored hash (spec 11.2.1) */\n");
    sb.Append("static df_uns* df_key_field(void* o, int index) {\n  return (df_uns*)((char*)o + df_impl_of(o, &df_trait_Keyable)->offsets[index]);\n}\n\n");
    sb.Append("/* a use of a key: false when its hash is not the stored one (the object changed while it is a key) */\n");
    sb.Append("static bool df_key_ok(void* o) {\n  if (o == NULL || *df_key_field(o, 0) == 0) {\n    return true;\n  }\n  return df_hash_obj(o) == *df_key_field(o, 1);\n}\n\n");
    sb.Append("/* the object became the key of one more entry */\n");
    sb.Append("static void df_key_added(void* o) {\n  if (o == NULL) {\n    return;\n  }\n  df_uns* uses = df_key_field(o, 0);\n  if (*uses == 0) {\n    *df_key_field(o, 1) = df_hash_obj(o);\n  }\n  *uses += 1;\n}\n\n");
    sb.Append("/* the default order of classes (spec 6.2.2): by hash, the sort key; the same hash is the same key */\n");
    sb.Append("static int df_bt_cmp_same(df_bt_kw a, df_bt_kw b) {\n  (void)a;\n  (void)b;\n  return 0;\n}\n\n");
    sb.Append("static df_uns df_hk_obj(const void* key) {\n  return (df_uns)((const uint64_t*)key)[0];\n}\n\n");
    sb.Append("static bool df_hk_obj_eq(const void* a, const void* b) {\n  return ((const uint64_t*)a)[0] == ((const uint64_t*)b)[0];\n}\n\n");
    arrayCode.Append(sb);
  }

  Node? lastKeyNode;
  bool removingChecked;

  // the value slot of an object key, added when the key is missing (MapSlot for object keys). The collection keeps the given object (a new one
  // or ->x), or a clone of a borrowed one in the copying paths of the library (add(other), clone); a borrowed key in user code is an error.
  // A key that is already there keeps its entry: the given object ends. A new entry counts one more use of the object as a key (11.2.1)
  string ObjectKeySlot(string held, string type, string key, string slot, bool check) {
    string keyType = MapKey(type);
    if (!LibraryCode && !lastKeyGiven) {
      throw Error("the collection owns its keys: give it a new object, or ->x to move one in (spec 6.6)", lastKeyNode ?? currentFun!.Node);
    }
    string valueC = SlotC(type);
    string tree = MapTree(held, type);
    string added = NewTemp();
    string owned = NewTemp();
    string take;
    if (lastKeyGiven) {
      take = $"void* {owned} = (void*)({key}); ";
    } else {
      cloneUsed = true;
      take = $"void* {owned} = (void*){CloneValue(key, keyType, lastKeyNode ?? currentFun!.Node)}; ";
    }
    string keep = $"if ({added}) {{ df_key_added({owned}); }} else {{ df_drop({owned}); }} ";
    string leftOver = lastKeyGiven ? $"else if ({key} != NULL) {{ df_drop((void*)({key})); }} " : "";
    string lockCheck = check ? LockCheck(held, type) : "";
    if (IsHmap(type)) {
      string words = NewTemp();
      string ownedWords = NewTemp();
      return $"{KeyCheckCode(keyType, key)}{HmapKeySlot(type, key, words)}{valueC}* {slot} = ({valueC}*)df_ht_find({tree}, {words}, {HmapShape(type)}); "
        + $"if ({slot} == NULL) {{ {lockCheck}bool {added}; {take}uint64_t {ownedWords}[2] = {{ {words}[0], (uint64_t)(uintptr_t){owned} }}; "
        + $"{slot} = ({valueC}*)df_ht_put({tree}, {ownedWords}, {HmapShape(type)}, &{added}); {keep}}} {leftOver}";
    }
    return $"{KeyCheckCode(keyType, key)}{valueC}* {slot} = ({valueC}*){MapFindRaw(held, type, key)}; if ({slot} == NULL) {{ {lockCheck}bool {added}; "
      + $"if ({key} == NULL) {{ {slot} = ({valueC}*)df_bt_put_null({tree}, {MapValueSize(type)}, &{added}); }} "
      + $"else {{ {take}{slot} = ({valueC}*)df_bt_put({tree}, {SortKey(keyType, owned)}, (df_bt_kw)(uintptr_t){owned}, {MapShape(type)}, &{added}); {keep}}} }} {leftOver}";
  }

  // the equality of two object key slots of an hmap (spec 6.8.5.1, 6.2): the same hash, then the equals of the class when it replaces it
  // (a failure of equals counts as different); a class without its own equals is compared by the hash (its default equals is not built)
  string ObjectKeyEquality(string keyType) {
    var cls = classes[keyType];
    if (FindEqualsClass(cls) == null) {
      return "df_hk_obj_eq";
    }
    string name = "df_hk_eq_" + SanitizeName(keyType);
    if (arrayHelpers.Add(name)) {
      string ct = CType(keyType);
      string pair = ResultPair("bool");
      string table = EqualsTable(keyType);
      arrayCode.Append($"static bool {name}(const void* a, const void* b);\n");
      lambdaHelpers.Append($"static bool {name}(const void* a, const void* b) {{\n  const uint64_t* x = (const uint64_t*)a;\n  const uint64_t* y = (const uint64_t*)b;\n"
        + $"  if (x[0] != y[0]) {{\n    return false;\n  }}\n  {ct} l = ({ct})(uintptr_t)x[1];\n  {ct} r = ({ct})(uintptr_t)y[1];\n"
        + $"  if (l == r) {{\n    return true;\n  }}\n  if (l == NULL || r == NULL) {{\n    return false;\n  }}\n"
        + $"  {pair} p = (({pair}(*)(void*, {ct}))({table}[l->cls->id - 256]))((void*)l, r);\n"
        + "  if (p.err) {\n    df_exc_release(p.err);\n    return false;\n  }\n  return p.value;\n}\n\n");
    }
    return name;
  }

  // the level 1 warning of 11.2.1: a Keyable class with a field of a collection, an iterator or a view can change its identity without
  // changing its hash
  void KeyableFieldWarnings() {
    if (!traits.ContainsKey("Keyable")) {
      return;
    }
    foreach (var cls in classOrder.Where(cls => cls.Node.Line < LibraryLine && IsSubtype(cls.Name, "Keyable"))) {
      foreach (var field in AllInstanceFields(cls).Where(field => !field.Hidden)) {
        bool iterator = classInstanceArgs.TryGetValue(field.Type, out var instance) && instance.Template.Name.EndsWith("Iterator");
        if (IsCollectionType(field.Type) || iterator) {
          Warn($"{DisplayName(cls.Name)} is Keyable and its field '{field.Name}' is a {DisplayName(field.Type)}: it can change without changing the hash of the object (spec 11.2.1)", field.At, 1);
        }
      }
    }
  }

  // the C that raises ModifiedObjectUsedAsKeyException when the key held in key changed while it is a key
  string KeyCheckCode(string keyType, string key) =>
    IsObjectKey(keyType) ? $"if (!df_key_ok((void*){key})) {{ df_raise(&df_desc_ModifiedObjectUsedAsKeyException, {fn!.NameConst}); {RaiseJump} }} " : "";
}
