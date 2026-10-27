using dfparser;

namespace dfcompiler;

// Hash maps hmap<K, V> (spec 6.8.5): the extendible hash table of spec 6.8.5.1 (RuntimeHmap.cs). The class hmap is written in Dflat (HmapPrelude, after
// VecPrelude) with the same methods as map, over the same tree_ members; the C helpers of CodeGen.Map.cs choose the runtime of each kind (df_bt or
// df_ht). The table sees a key as a key slot (one or two uint64_t, made here) with the hash function of the hmap type, and a value as vsize bytes.
// Key slots of the native keys: the value as an unsigned of its width (a real by its normalized bits); a nullable key with spare bits keeps null as
// the null constant, which has a bit above the width of the value; a full-width nullable (64-bit integers and reals) takes two uint64_t: the null
// constant or 0, then the value (spec 6.8.5.1). The key slot is 8 bytes also with a 32-bit uns (CHOICE: the same code on every target).
// Step 1 of the hmap plan: native keys and values (strings and objects come later).
public sealed partial class CodeGen {
  public const string HmapPrelude = """

// hmap<K, V> (spec 6.8.5): a hash map, the extendible hash table of spec 6.8.5.1. The same methods as map; the tree_ members are C emitted by the
// compiler over the hash table (CodeGen.Map.cs, CodeGen.Hmap.cs)
public class hmap<K, V> : MapCollection<K, V> {
  u64 tree              // the table: a df_ht in the C (CodeGen.Hmap.cs)

  override fun uns get_len() { this.tree_len() }

  override fun bool has(V value) {
    return this.tree_hasValue(value)
  }

  public fun bool hasKey(K key) {
    return this.tree_hasKey(key)
  }

  // adds the pair, or replaces the value of an existing key: false when it replaced (spec 6.6)
  public fun bool add(K key, V value) {
    if this.tree_hasKey(key) {
      this.tree_set(key, value)
      return false
    }
    checkUnlocked()
    this.tree_set(key, value)
    return true
  }

  public fun bool tryGet(K key, (&)V result) {
    if !this.tree_hasKey(key) {
      V none
      result = none
      return false
    }
    result = this.tree_get(key)
    return true
  }

  public fun bool tryGet(K key, (&)V result, V otherwise) {
    if !this.tree_hasKey(key) {
      result = otherwise
      return false
    }
    result = this.tree_get(key)
    return true
  }

  // the value of key leaves the map with its entry (->m[k], spec 6.6); a missing key gives the zero value
  public fun V takeAt(K key) {
    checkUnlocked()
    return this.tree_take(key)
  }

  public fun removeAt(K key) {
    V gone = takeAt(key)
  }

  // the first entry in the order of the table that holds the value
  public fun bool remove(V value) {
    checkUnlocked()
    return this.tree_removeValue(value)
  }

  public fun uns removeAll(V value) {
    checkUnlocked()
    return this.tree_removeAllValues(value)
  }

  public fun K keyOf(V value) {
    return this.tree_keyOf(value)
  }

  // all the entries go (spec 6.6): the size changes, so not while the map is iterated
  override fun clear() {
    checkUnlocked()
    this.tree_clear()
  }

  // .capacity (spec 6.8.5.1): the entries held without growing the directory
  public fun uns get_capacity() {
    return this.tree_capacity()
  }

  // room for wanted entries in all: the directory and its buckets are made now, and stay until a shrink (spec 6.6, 6.8.5.1)
  public fun reserve(uns wanted) {
    checkUnlocked()
    this.tree_reserve(wanted)
  }

  // every merge that fits, then the smallest directory: false when nothing changed (spec 6.6, 6.8.5.1)
  public fun bool shrinkToFit() {
    checkUnlocked()
    return this.tree_shrinkToFit()
  }

  // .keys and .values (spec 6.6): views that count as open while they exist (spec 6.4)
  public fun hmapKeys<K, V> get_keys() {
    acquireIterator()
    hmapKeys<K, V> view = hmapKeys<K, V>(this)
    return ->view
  }

  public fun hmapValues<K, V> get_values() {
    acquireIterator()
    hmapValues<K, V> view = hmapValues<K, V>(this)
    return ->view
  }

  // what the views ask their map (they hold a read-only reference to it)
  asparent fun uns viewLen() {
    return this.tree_len()
  }

  asparent fun bool viewHasKey(K key) {
    return this.tree_hasKey(key)
  }

  asparent fun bool viewHasValue(V value) {
    return this.tree_hasValue(value)
  }
}

// the keys and the values of an hmap (spec 6.5, 6.6): the same as mapKeys and mapValues (VecPrelude), in the order of the table
public class hmapKeys<K, V>(&hmap<K, V> collection as parent) {
  public fun uns get_len() {
    return collection.viewLen()
  }

  public fun bool has(K key) {
    return collection.viewHasKey(key)
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}

public class hmapValues<K, V>(&hmap<K, V> collection as parent) {
  public fun uns get_len() {
    return collection.viewLen()
  }

  public fun bool has(V value) {
    return collection.viewHasValue(value)
  }
} atEnd {
  if collection != null {
    collection.releaseIterator()
  }
}
""";

  // hmap<K, V>[capacity]: empty, with the directory and the buckets for that many entries (spec 6.3, 6.8.5.1)
  Expr HmapNewWithCapacity(Node generic, Node sizeNode, Scope scope) {
    if (generic.Children.Count != 3) {
      throw Error("hmap needs a key type and a value type: hmap<K, V>[capacity]", generic);
    }
    string type = ResolveType(Synthetic("Type", generic, "hmap").Add(generic.Children[1]).Add(generic.Children[2]));
    var size = ExprOf(sizeNode, scope, "u64");
    string held = NewTemp();
    string code = $"({{ {CType(type)} {held} = {VecNewEmpty(type)}; df_ht_reserve({MapTree(held, type)}, {IndexValue(size, sizeNode)}, {HmapShape(type)}); {held}; }})";
    return new Expr(code, type, true);
  }

  bool IsHmap(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "hmap";

  // the runtime part of a map of this kind
  void NeedMapChunk(string type) => NeedChunk(IsHmap(type) ? "hmap" : "map");

  // the key types an hmap takes in this version: the natives and strings (steps 1 and 2 of the hmap plan)
  bool HmapKeyBuilt(string key) {
    string inner = BaseOf(key);
    if (IsNullable(key) && IsTimeType(inner)) {
      return false;
    }
    return IsInt(inner) || inner is "bool" or "char" or "r32" or "r64" || IsEnum(inner) || (!IsNullable(key) && (IsTimeType(inner) || inner == "string"));
  }

  bool HmapValueBuilt(string value) {
    string inner = BaseOf(value);
    return IsInt(inner) || inner is "bool" or "char" or "r32" or "r64" || IsTimeType(inner) || IsEnum(inner) || value == "string";
  }

  // the C type and the width in bits of the value of a native key (an enum: its value type)
  (string Type, int Bits) HmapKeyValueType(string keyType) {
    string inner = BaseOf(keyType);
    if (enums.TryGetValue(inner, out var info)) {
      inner = info.ValueType;
    }
    int bits = inner switch {
      "bool" => 8,
      "char" or "time" or "r32" => 32,
      "date" or "clock" or "lapse" or "r64" => 64,
      _ => Bits(inner)
    };
    return (inner, bits);
  }

  // a nullable key that has no spare bit in a uint64_t: its slot holds two of them
  bool HmapWideNullKey(string keyType) => IsNullable(keyType) && HmapKeyValueType(keyType).Bits == 64;

  int HmapKeyWords(string type) => HmapWideNullKey(MapKey(type)) ? 2 : 1;

  // the bits of a key value (not null) as a uint64_t: an unsigned of its width, a real by its normalized bits
  string HmapKeyBits(string keyType, string value) {
    var (inner, bits) = HmapKeyValueType(keyType);
    return inner switch {
      "r32" => $"df_ht_bits_r32({value})",
      "r64" => $"df_ht_bits_r64({value})",
      "bool" or "char" => $"((uint64_t)({value}))",
      _ => $"((uint64_t)({PrimitiveCType("u" + bits)})({value}))"
    };
  }

  // the key value back from its bits
  string HmapKeyFromBits(string keyType, string bits) {
    var (inner, width) = HmapKeyValueType(keyType);
    string cType = CType(BaseOf(keyType));
    return inner switch {
      "r32" => $"df_ht_unbits_r32({bits})",
      "r64" => $"df_ht_unbits_r64({bits})",
      "bool" => $"(({bits}) != 0)",
      _ => $"(({cType})({PrimitiveCType("u" + width)})({bits}))"
    };
  }

  // the statements that declare the key slot slot (an array of uint64_t) of the key held in the C variable key
  string HmapKeySlot(string type, string key, string slot) {
    string keyType = MapKey(type);
    if (keyType == "string") {
      return HmapStringKeySlot($"(uint64_t)df_hash_str({key})", key, slot);
    }
    string words = $"uint64_t {slot}[{HmapKeyWords(type)}]";
    if (!IsNullable(keyType)) {
      return $"{words} = {{ {HmapKeyBits(keyType, key)} }}; ";
    }
    if (HmapWideNullKey(keyType)) {
      return $"{words} = {{ {key}.has ? 0 : DF_HT_NULL_TAG, {key}.has ? {HmapKeyBits(keyType, key + ".val")} : 0 }}; ";
    }
    return $"{words} = {{ {key}.has ? {HmapKeyBits(keyType, key + ".val")} : DF_HT_NULL_TAG }}; ";
  }

  // the key slot of a string key (df_ht_skey, RuntimeHmap.cs): its full hash and a pointer to the string (an array of one, like the other slots)
  static string HmapStringKeySlot(string hash, string key, string slot) => $"df_ht_skey {slot}[1] = {{ {{ {hash}, {{ .s = {key} }} }} }}; ";

  // the key held in a key slot (a pointer to its uint64_t); a string key is the hmap's own string (a borrow)
  string HmapKeyFromSlot(string type, string slotPointer) {
    string keyType = MapKey(type);
    if (keyType == "string") {
      return $"(((const df_ht_skey*){slotPointer})->k.s)";
    }
    string words = $"((const uint64_t*){slotPointer})";
    if (!IsNullable(keyType)) {
      return HmapKeyFromBits(keyType, $"{words}[0]");
    }
    string cType = CType(keyType);
    if (HmapWideNullKey(keyType)) {
      return $"({words}[0] != 0 ? (({cType}){{ false }}) : (({cType}){{ true, {HmapKeyFromBits(keyType, $"{words}[1]")} }}))";
    }
    return $"({words}[0] == DF_HT_NULL_TAG ? (({cType}){{ false }}) : (({cType}){{ true, {HmapKeyFromBits(keyType, $"{words}[0]")} }}))";
  }

  // the hash of the value of a key slot word that is not null (spec 6.2.1): the value; its two halves when it is wider than uns
  string HmapBitsHash(string keyType, string bits) {
    var (inner, width) = HmapKeyValueType(keyType);
    string hash = WidenedHash($"({PrimitiveCType("u" + width)})({bits})", width);
    // date, time, clock and lapse have their own null (spec 3.3): its hash is the null constant
    return IsTimeType(inner) ? $"({bits} == {HmapKeyBits(inner, NullCode(inner))} ? DF_HASH_NULL : {hash})" : hash;
  }

  // the name of the shape of an hmap type (df_ht_shape), with the hash function of its key slots, emitted once
  string HmapShape(string type) {
    NeedChunk("hmap");
    string mangled = System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    string shape = "df_hts_" + mangled;
    if (MapValue(type) == "string") {
      NeedChunk("map");   // the 64-byte slot of a string value is the one of map (df_bt_sval, RuntimeMap.cs)
    }
    if (arrayHelpers.Add(shape)) {
      string keyType = MapKey(type);
      if (keyType == "string") {
        arrayCode.Append($"static const df_ht_shape {shape} = {{ sizeof(df_ht_skey), {MapValueSize(type)}, df_ht_skey_hash, df_ht_skey_eq, true }};\n\n");
        return "&" + shape;
      }
      string hash;
      if (!IsNullable(keyType)) {
        hash = HmapBitsHash(keyType, "w[0]");
      } else if (HmapWideNullKey(keyType)) {
        hash = $"(w[0] != 0 ? DF_HASH_NULL : {HmapBitsHash(keyType, "w[1]")})";
      } else {
        hash = $"(w[0] == DF_HT_NULL_TAG ? DF_HASH_NULL : {HmapBitsHash(keyType, "w[0]")})";
      }
      arrayCode.Append($"static df_uns df_hk_{mangled}(const void* key) {{\n  const uint64_t* w = (const uint64_t*)key;\n  return {hash};\n}}\n\n");
      arrayCode.Append($"static const df_ht_shape {shape} = {{ {HmapKeyWords(type) * 8}, {MapValueSize(type)}, df_hk_{mangled}, NULL, false }};\n\n");
    }
    return "&" + shape;
  }
}
