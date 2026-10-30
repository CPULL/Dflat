using System.Text;
using dfparser;

namespace dfcompiler;

// Maps map<K, V> (spec 6.8.4), alias bmap: sorted by key, a B+ tree (spec 6.8.4.1). The class `map` is written in Dflat (CodeGen.Vec.cs, VecPrelude): its
// methods check the rules (the counter of open iterators) and call the tree_ members, which are C emitted here over the runtime of the tree (RuntimeMap.cs).
// The tree sees a key as its sort key (a uint64_t that keeps the order of the key type, SortKey) plus, for a string, a pointer to the key, which the map
// owns (its own copy, shrunk); it sees a value as vsize bytes. The null key is kept in the header of the tree. The field tree is declared u64 in Dflat
// and is a df_bt in the C struct (EmitStruct). c[k] and c[k] = v are C at the call site.
// Steps 1 and 2 of the collections plan: keys and values of the native types and strings (objects come later).
public sealed partial class CodeGen {
  // map or hmap: the two kinds share the class methods and the code at the call sites; the C helpers below choose the runtime (CodeGen.Hmap.cs)
  bool IsMap(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name is "map" or "hmap";

  // += and -= on maps and sets (spec 6.6): the operators call the members, so they are compiled as the statements written out; null when the target
  // is neither a map nor a set
  Node? CollectionOperator(Node s, Scope scope) {
    Node target = s.Children[0];
    if (s.Text is not ("+=" or "-=") || target.Kind is not ("Name" or "Member")) {
      return null;
    }
    // a property is never read here the way a field is written (that would skip its W:): its type is enough
    string type = PropertyTarget(target, scope) is var (_, property) ? property.Type : LValue(target, scope).Type;
    if (IsMap(type) && s.Text == "-=" && MapKeysToRemove(s, scope, type) is Node loop) {
      return loop;
    }
    return IsMap(type) ? Synthetic("ExprStmt", s).Add(MapOperatorCall(s, scope)) : IsSet(type) ? SetOperator(s, scope) : null;
  }

  // m -= s with s a set or an hset of the key type (a variable or a field, spec 6.6): a loop over s that calls m.removeAt(key), so every key of s
  // goes with its value; null when the right side is not a set
  Node? MapKeysToRemove(Node s, Scope scope, string mapType) {
    Node target = s.Children[0], value = s.Children[1];
    if (value.Kind is not ("Name" or "Member")) {
      return null;
    }
    string setType;
    try {
      setType = LValue(value, scope).Type;
    } catch (CompileException) {
      return null;   // not a variable or a field: a key
    }
    if (!IsSet(setType)) {
      return null;
    }
    if (MapKey(setType) != MapKey(mapType)) {
      throw Error($"the keys of this map are {DisplayName(MapKey(mapType))}, not {DisplayName(MapKey(setType))}: m -= s takes a set of keys (spec 6.6)", value);
    }
    string key = "__key" + ++tempCounter;
    var body = Synthetic("Block", s).Add(Synthetic("ExprStmt", s).Add(MemberCall(s, target, "removeAt", Synthetic("Name", s, key))));
    return Synthetic("For", s).Add(Synthetic("Var", s, key)).Add(Synthetic("In", s).Add(value)).Add(body);
  }

  // a call of a member on the target of s: target.name(args)
  static Node MemberCall(Node s, Node target, string name, params Node[] args) {
    var list = Synthetic("Args", s);
    foreach (var arg in args) {
      list.Add(arg);
    }
    return Synthetic("Call", s).Add(Synthetic("Member", s, name).Add(target)).Add(list);
  }

  // m += (k, v), m += other and m -= key on map and hmap: m.add(k, v), m.add(other), m.removeAt(key)
  Node MapOperatorCall(Node s, Scope scope) {
    Node target = s.Children[0], value = s.Children[1];
    var args = Synthetic("Args", s);
    if (s.Text == "-=") {
      args.Add(value);
    } else if (value.Kind == "Tuple") {
      if (value.Children.Count != 2) {
        throw Error("m += (key, value): the tuple has two items, the key and the value", value);
      }
      args.Add(value.Children[0]).Add(value.Children[1]);
    } else if (value.Kind == "Name" && scope.Find(value.Text!) is VarInfo other && IsMap(other.Type) && IsHmap(other.Type) != IsHmap(LValue(target, scope).Type)) {
      throw Error("a map and an hmap cannot be mixed: add(map) takes a map of the same kind (spec 6.6)", value);
    } else if (value.Kind == "Name" && scope.Find(value.Text!) is VarInfo pair && IsTuple(pair.Type)) {
      if (TupleElems(pair.Type).Count != 2) {
        throw Error("m += pair: the tuple has two items, the key and the value", value);
      }
      args.Add(Synthetic("Member", value, "0").Add(value));
      args.Add(Synthetic("Member", value, "1").Add(value));
    } else {
      args.Add(value);
    }
    return Synthetic("Call", s).Add(Synthetic("Member", s, s.Text == "+=" ? "add" : "removeAt").Add(target)).Add(args);
  }

  // the flow of the exceptions (CodeGen.Leaks.cs) runs before the types: it takes m += x and m -= x for the calls they are when the target is
  // declared as a map or a set (a parameter, a local of the function, a field); other targets keep the arithmetic, so a number += does not look
  // like a call to add
  bool FlowTargetIsCollection(Node target, FunSig? fun) {
    static bool CollectionTypeNode(Node? type) => type is { Kind: "Type", Text: "map" or "hmap" or "bmap" or "set" or "bset" or "hset" };
    bool IsKept(string type) => IsMap(type) || IsSet(type);
    if (target.Kind == "Member") {
      return classes.Values.Any(cls => cls.Fields.Any(field => field.Name == target.Text && IsKept(field.Type)));
    }
    if (target.Kind != "Name" || fun == null) {
      return false;
    }
    string name = target.Text!;
    if (fun.Params.FirstOrDefault(p => p.Name == name) is ParamSig param) {
      return IsKept(param.Type);
    }
    bool found = false;
    void Walk(Node node) {
      if (node.Kind == "Decl" && node.Text == name) {
        var type = node.Children.FirstOrDefault();
        var init = node.Children.FirstOrDefault(c => c.Kind == "Init");
        found |= CollectionTypeNode(type) || (type is { Kind: "Type", Text: "var" } && init?.Children[0].Kind == "MapLit");
      }
      foreach (var child in node.Children) {
        Walk(child);
      }
    }
    Walk(fun.Node);
    if (!found && fun.Class != null) {
      found = classes.Values.Any(cls => cls.Fields.Any(field => field.Name == name && IsKept(field.Type)));
    }
    return found;
  }

  string MapKey(string type) => classInstanceArgs[type].Args[0];

  // the value type of a map; "" for a set, which keeps values of 0 bytes (CodeGen.Hset.cs)
  string MapValue(string type) => IsSet(type) ? "" : classInstanceArgs[type].Args[1];

  // the types a map takes in this version: the natives whose sort key is exact, and strings (spec 6.8.4.1)
  bool MapKeyBuilt(string key) {
    string inner = BaseOf(key);
    if (IsNullable(key) && inner is "date" or "time") {
      return false;
    }
    return IsInt(inner) || inner is "bool" or "char" or "r32" or "r64" || IsEnum(inner) || (!IsNullable(key) && inner is "date" or "time" or "string");
  }

  bool MapValueBuilt(string value) {
    string inner = BaseOf(value);
    return IsInt(inner) || inner is "bool" or "char" or "r32" or "r64" or "date" or "time" or "clock" or "lapse" || IsEnum(inner) || value == "string";
  }

  // map<K, V> or hmap<K, V> written as a type: the key and the value types this version builds (spec 6.2)
  void CheckMapArgs(List<string> args, Node at, bool hashed) {
    string kind = hashed ? "hmap" : "map";
    if (args.Count != 2) {
      throw Error($"{kind} takes a key type and a value type: {kind}<K, V>", at);
    }
    if (IsArr(args[0]) || IsVec(args[0]) || IsList(args[0]) || IsMap(args[0]) || IsSet(args[0]) || IsView(args[0])) {
      throw Error($"a collection cannot be a key (spec 6.2): {DisplayName(args[0])}", at);
    }
    if (hashed) {
      if (!HmapKeyBuilt(args[0])) {
        throw Error($"hmap keys of type {DisplayName(args[0])} are not built yet (this version: integers, bool, char, enums, reals, date, time, clock, lapse and string)", at);
      }
      if (!HmapValueBuilt(args[1])) {
        throw Error($"hmap values of type {DisplayName(args[1])} are not built yet (this version: the native types and string)", at);
      }
      return;
    }
    if (!MapKeyBuilt(args[0])) {
      throw Error($"map keys of type {DisplayName(args[0])} are not built yet (this version: integers, bool, char, enums, reals, date, time and string)", at);
    }
    if (!MapValueBuilt(args[1])) {
      throw Error($"map values of type {DisplayName(args[1])} are not built yet (this version: the native types and string)", at);
    }
  }

  string MapTree(string held, string type) => $"(&{held}->{VecField(type, "tree")})";

  string MapValueSize(string type) => MapValue(type) switch {
    "string" => "DF_BT_SVAL",
    "" => "0",
    var value => $"sizeof({CType(value)})"
  };

  // the C type of a value slot: a string value is inline, 64 bytes (spec 6.8.4.1, df_bt_sval in RuntimeMap.cs); a set has none
  string SlotC(string type) => MapValue(type) is "string" or "" ? "uint8_t" : CType(MapValue(type));

  // the value in a slot (a pointer): a string is read in place, a borrow that is valid while the tree does not change
  string ReadSlot(string type, string slot) => MapValue(type) == "string" ? $"df_bt_sval({slot})" : $"(*{slot})";

  // the mark of a string read in place from a map: a variable that keeps it takes a copy (StrAssign)
  const string MapBorrowMark = "/*df_map_borrow*/";

  // a string key keeps a pointer next to its sort key (spec 6.8.4.1)
  bool KeyHasPointer(string type) => MapKey(type) == "string";

  // the shape of the tree, passed to the calls of the runtime: the value size, whether the keys have a pointer, how two key pointers compare
  string MapShape(string type) => $"{MapValueSize(type)}, {(KeyHasPointer(type) ? "true, df_bt_cmp_str" : "false, NULL")}";

  string KeyPointer(string type, string key) => KeyHasPointer(type) ? $"(void*){key}" : "NULL";

  // the C test that a key held in a C variable is null, or null when the key type has no null of its own (a null string is "", spec 7.1)
  string? KeyIsNull(string keyType, string key) {
    if (IsNullable(keyType)) {
      return $"!{key}.has";
    }
    return keyType switch {
      "date" => $"({key} == INT64_MIN)",
      "time" => $"({key} == -1)",
      _ => null
    };
  }

  // the sort key of a key that is not null: a uint64_t in the order of the key type (spec 6.8.4.1)
  string SortKey(string keyType, string key) {
    string inner = BaseOf(keyType);
    string value = IsNullable(keyType) ? $"{key}.val" : key;
    if (enums.TryGetValue(inner, out var info)) {
      inner = info.ValueType;
    }
    if (inner == "string") {
      return $"df_bt_key_str({value})";
    }
    if (inner is "r32" or "r64") {
      return $"df_bt_key_real((double)({value}))";
    }
    if (SignedTypes.Contains(inner) || inner is "date" or "time") {
      return $"((uint64_t)(int64_t)({value}) ^ 0x8000000000000000ULL)";
    }
    return $"((uint64_t)({value}))";
  }

  // the key back from its sort key (exact keys only)
  string FromSortKey(string keyType, string sortKey) {
    string inner = BaseOf(keyType);
    string valueType = enums.TryGetValue(inner, out var info) ? info.ValueType : inner;
    string cType = CType(inner);
    string value = valueType switch {
      "r32" or "r64" => $"(({cType})df_bt_unkey_real({sortKey}))",
      "bool" => $"({sortKey} != 0)",
      _ when SignedTypes.Contains(valueType) || valueType is "date" or "time" => $"(({cType})(int64_t)({sortKey} ^ 0x8000000000000000ULL))",
      _ => $"(({cType})({sortKey}))"
    };
    return IsNullable(keyType) ? $"(({CType(keyType)}){{ true, {value} }})" : value;
  }

  // the null key, as a value of the key type
  string NullKeyValue(string keyType) => IsNullable(keyType) ? $"(({CType(keyType)}){{ false }})" : keyType is "date" or "time" ? NullCode(keyType) : ZeroValue(keyType);

  // the key of a map operation, converted to the key type, in a C variable; a string is only read here (the map keeps its own copy when it adds it)
  string MapKeyArg(Node node, Scope scope, string keyType, string variable, bool ofSet = false) {
    var key = ExprOf(node, scope, keyType);
    if (!Assignable(keyType, key.Type) && key.Type != keyType) {
      throw Error(ofSet ? $"the items of this set are {DisplayName(keyType)}, not {DisplayName(key.Type)}"
        : $"the key of this map is {DisplayName(keyType)}, not {DisplayName(key.Type)}", node);
    }
    string converted = Convert(key, keyType, node);
    if (keyType == "string") {
      converted = StrRead(new Expr(converted, "string", FreshCode(key, converted) == "true" && !IsStaticStr(converted)));
    }
    return $"{CType(keyType)} {variable} = {converted}; ";
  }

  // a value for a map whose values are valueType
  Expr CheckedMapValue(Node node, Scope scope, string valueType) {
    var value = ExprOf(node, scope, valueType);
    if (value.Type != valueType && !Assignable(valueType, value.Type)) {
      throw Error($"the values of this map are {DisplayName(valueType)}, not {DisplayName(value.Type)}", node);
    }
    return value;
  }

  // a value given to a member that only compares it (has, remove, keyOf): a string is only read
  string MapValueRead(Node node, Scope scope, string valueType) {
    var value = CheckedMapValue(node, scope, valueType);
    string converted = Convert(value, valueType, node);
    return valueType == "string" ? StrRead(new Expr(converted, "string", FreshCode(value, converted) == "true" && !IsStaticStr(converted))) : converted;
  }

  // a value that goes into the map: a string is its own copy, in its smallest form (spec 6.6)
  string MapValueItem(Node node, Scope scope, string valueType) {
    var value = CheckedMapValue(node, scope, valueType);
    return StrItemAs(value, valueType, Convert(value, valueType, node));
  }

  // stores an item (already the map's own) in a value slot: the string it replaces is freed
  static string StoreValue(string valueType, string slot, string item) =>
    valueType == "string" ? $"df_bt_sval_put({slot}, {item}); " : $"*{slot} = {item}; ";

  // the C expression of the value slot of a key held in key (a pointer to the value, or NULL when the key is missing)
  string MapFind(string held, string type, string key) {
    string tree = MapTree(held, type);
    if (IsHmap(type)) {
      string words = NewTemp();
      return $"({{ {HmapKeySlot(type, key, words)}df_ht_find({tree}, {words}, {HmapShape(type)}); }})";
    }
    string find = $"df_bt_find({tree}, {SortKey(MapKey(type), key)}, {KeyPointer(type, key)}, {MapShape(type)})";
    return KeyIsNull(MapKey(type), key) is string isNull ? $"({isNull} ? df_bt_find_null({tree}) : {find})" : find;
  }

  // the statements that give slot (a declared pointer variable) the value slot of key, added (zeroed) when the key is missing: then the map takes its own
  // copy of a string key, and the counter of open iterators is checked first when check is true (spec 6.4)
  string MapSlot(string held, string type, string key, string slot, bool check) {
    string valueC = SlotC(type);
    string tree = MapTree(held, type);
    string added = NewTemp();
    string owned = NewTemp();
    if (IsHmap(type)) {
      string words = NewTemp();
      // a new string key: the hmap keeps its own copy, with the hash already computed
      string ownedWords = KeyHasPointer(type) ? NewTemp() : words;
      string keyCopy = KeyHasPointer(type) ? $"df_string {owned} = df_str_dup({key}); {HmapStringKeySlot($"{words}[0].h", owned, ownedWords)}" : "";
      return $"{HmapKeySlot(type, key, words)}{valueC}* {slot} = ({valueC}*)df_ht_find({tree}, {words}, {HmapShape(type)}); if ({slot} == NULL) {{ "
        + $"{(check ? LockCheck(held, type) : "")}bool {added}; {keyCopy}{slot} = ({valueC}*)df_ht_put({tree}, {ownedWords}, {HmapShape(type)}, &{added}); }} ";
    }
    string sb = $"{valueC}* {slot} = ({valueC}*){MapFind(held, type, key)}; if ({slot} == NULL) {{ {(check ? LockCheck(held, type) : "")}bool {added}; ";
    if (KeyIsNull(MapKey(type), key) is string isNull) {
      sb += $"if ({isNull}) {{ {slot} = ({valueC}*)df_bt_put_null({tree}, {MapValueSize(type)}, &{added}); }} else ";
    }
    string copy = KeyHasPointer(type) ? $"df_string {owned} = df_str_dup({key}); " : "";
    string ownedKey = KeyHasPointer(type) ? owned : key;
    sb += $"{{ {copy}{slot} = ({valueC}*)df_bt_put({tree}, {SortKey(MapKey(type), ownedKey)}, {KeyPointer(type, ownedKey)}, {MapShape(type)}, &{added}); }} }} ";
    return sb;
  }

  // removes key: its value goes to outPointer (or NULL: then a string value is freed); the key the map owned is freed; a C bool
  string MapRemove(string held, string type, string key, string outPointer) {
    string tree = MapTree(held, type);
    string valueType = MapValue(type);
    string gone = NewTemp();
    string goneKey = NewTemp();
    string done = NewTemp();
    bool dropValue = outPointer == "NULL" && valueType == "string";
    string target = dropValue ? gone : outPointer;
    if (IsHmap(type)) {
      // the key slot of the entry comes out too: a string key the hmap owned is freed
      string words = NewTemp();
      string head = $"({{ {HmapKeySlot(type, key, words)}{(dropValue ? $"uint8_t {gone}[DF_BT_SVAL] = {{ 0 }}; " : "")}"
        + (KeyHasPointer(type) ? $"df_ht_skey {goneKey} = {{ 0 }}; " : "");
      string body = $"bool {done} = df_ht_remove({tree}, {words}, {HmapShape(type)}, {target}, {(KeyHasPointer(type) ? "&" + goneKey : "NULL")}); "
        + (KeyHasPointer(type) ? $"df_str_free({goneKey}.k.s); " : "") + (dropValue ? $"df_bt_sval_clear({gone}); " : "");
      return head + body + $"{done}; }})";
    }
    string sb = $"({{ {(dropValue ? $"uint8_t {gone}[DF_BT_SVAL] = {{ 0 }}; " : "")}void* {goneKey} = NULL; bool {done} = ";
    string remove = $"df_bt_remove({tree}, {SortKey(MapKey(type), key)}, {KeyPointer(type, key)}, {MapShape(type)}, {target}, &{goneKey})";
    sb += KeyIsNull(MapKey(type), key) is string isNull ? $"({isNull} ? df_bt_remove_null({tree}, {MapValueSize(type)}, {target}) : {remove}); " : $"{remove}; ";
    if (KeyHasPointer(type)) {
      sb += $"df_str_free((df_string){goneKey}); ";
    }
    if (dropValue) {
      sb += $"df_bt_sval_clear({gone}); ";
    }
    return sb + $"{done}; }})";
  }

  // the key under a cursor (for a string key: the map's own string)
  string CursorKey(string type, string cursor) => IsHmap(type)
    ? HmapKeyFromSlot(type, $"df_ht_cur_key(&{cursor}, {HmapShape(type)})")
    : KeyHasPointer(type)
    ? $"((df_string)df_bt_cur_ptr(&{cursor}))"
    : $"({cursor}.on_null ? {NullKeyValue(MapKey(type))} : {FromSortKey(MapKey(type), $"df_bt_cur_key(&{cursor})")})";

  string CursorValue(string held, string type, string cursor) => IsHmap(type)
    ? $"(({SlotC(type)}*)df_ht_cur_value(&{cursor}, {HmapShape(type)}))"
    : $"(({SlotC(type)}*)df_bt_cur_value({MapTree(held, type)}, &{cursor}, {MapValueSize(type)}, {(KeyHasPointer(type) ? "true" : "false")}))";

  // the header of a C loop over the entries (the key order of a map, the order of the table of an hmap) with the cursor cursor; more is added to the
  // condition, step to the step
  string EntryLoop(string held, string type, string cursor, string more = "", string step = "") {
    string tree = MapTree(held, type);
    return IsHmap(type)
      ? $"for (df_ht_cur {cursor} = df_ht_begin({tree}); df_ht_valid(&{cursor}){more}; df_ht_step({tree}, &{cursor}){step})"
      : $"for (df_bt_cur {cursor} = df_bt_begin({tree}); df_bt_valid(&{cursor}){more}; df_bt_step(&{cursor}){step})";
  }

  // a walk over the entries: body gets the C of the value slot (a pointer) and of the cursor
  string ForEachEntry(string held, string type, Func<string, string, string> body) {
    string cursor = NewTemp();
    string slot = NewTemp();
    string valueType = SlotC(type);
    return $"{EntryLoop(held, type, cursor)} {{ {valueType}* {slot} = {CursorValue(held, type, cursor)}; {body(slot, cursor)}}} ";
  }

  // frees what an entry owns: its key string and its value string
  string DropEntry(string type, string slot, string cursor) =>
    (!KeyHasPointer(type) ? ""
      : IsHmap(type) ? $"df_str_free({CursorKey(type, cursor)}); "
      : $"if (!{cursor}.on_null) {{ df_str_free((df_string)df_bt_cur_ptr(&{cursor})); }} ")
    + (MapValue(type) == "string" ? $"df_bt_sval_clear({slot}); " : "");

  bool MapOwnsStrings(string type) => KeyHasPointer(type) || MapValue(type) == "string";

  // all the entries go: what they own is freed, then the nodes
  string MapClearCode(string held, string type) {
    string drop = MapOwnsStrings(type) ? ForEachEntry(held, type, (slot, cursor) => DropEntry(type, slot, cursor)) : "";
    if (IsHmap(type)) {
      return $"{drop}df_ht_clear({MapTree(held, type)}); ";
    }
    return $"{drop}df_bt_clear({MapTree(held, type)}, {(KeyHasPointer(type) ? "true" : "false")}); ";
  }

  // m[k]: the value, or the zero (null) value when the key is missing; no exception (spec 6.6). A string value is the map's own (a borrow)
  Expr MapIndex(Expr obj, Node keyNode, Scope scope) {
    NeedMapChunk(obj.Type);
    string type = obj.Type;
    string held = NewTemp();
    string key = NewTemp();
    string slot = NewTemp();
    string valueType = SlotC(type);
    string mark = MapValue(type) == "string" ? MapBorrowMark : "";
    string code = $"({{ {CType(type)} {held} = {NullChecked(obj.Code, type)}; {MapKeyArg(keyNode, scope, MapKey(type), key)}"
      + $"{valueType}* {slot} = ({valueType}*){MapFind(held, type, key)}; {slot} != NULL ? {ReadSlot(type, slot)} : {ZeroValue(MapValue(type))}; }}){mark}";
    return new Expr(code, MapValue(type));
  }

  // m[k] = v replaces the value or adds the key (spec 6.6); adding a key changes the size, which is not allowed while the map is iterated (spec 6.4).
  // m[k] += v on a missing key starts from the zero value
  string MapIndexAssign(Node s, Scope scope, Expr obj, Node target) {
    NeedMapChunk(obj.Type);
    string type = obj.Type;
    string valueType = MapValue(type);
    string op = s.Text!;
    if (op is "_=" or "??=") {
      throw Error($"'{op}' on a map entry is not supported yet", s);
    }
    string held = NewTemp();
    string key = NewTemp();
    string slot = NewTemp();
    string head = $"({{ {CType(type)} {held} = {NullChecked(obj.Code, type)}; {MapKeyArg(target.Children[1], scope, MapKey(type), key)}";
    if (op == "=") {
      // the value first: it may read the map
      string item = NewTemp();
      return head + $"{CType(valueType)} {item} = {MapValueItem(s.Children[1], scope, valueType)}; " + MapSlot(held, type, key, slot, true)
        + StoreValue(valueType, slot, item) + "})";
    }
    string combined = Convert(BinaryOp(op[..^1], new Expr(ReadSlot(type, slot), valueType), s.Children[1], scope, s), valueType, s);
    return head + MapSlot(held, type, key, slot, true) + StoreValue(valueType, slot, valueType == "string" ? $"df_str_own({combined}, true)" : combined) + "})";
  }

  // an empty map from []
  Expr MapEmptyLiteral(Node literal, string type) {
    if (literal.Children.Count > 0) {
      throw Error("a map literal has key: value entries: [k: v, ...] (spec 6.3)", literal);
    }
    return new Expr(VecNewEmpty(type), type, true);
  }

  // the type of a map literal without a declared type: var m = [1: "one"], map m = [...] (spec 6.3)
  string InferMapType(Node literal, Scope scope, Node at, bool hashed = false) {
    if (!vecPrelude) {
      throw NotSupported("Collections", at);
    }
    var keys = literal.Children.Select(entry => ExprOf(entry.Children[0], scope)).ToList();
    var values = literal.Children.Select(entry => ExprOf(entry.Children[1], scope)).ToList();
    var args = new List<string> { CommonType(keys, literal), CommonType(values, literal) };
    CheckMapArgs(args, at, hashed);
    return ClassInstanceName(genericClasses[hashed ? "hmap" : "map"], args, at);
  }

  // the value of a constant key written in a literal (a literal or an enum entry, a minus before a number), as a text that is the same for the same
  // key; null when the key is calculated at run time
  string? ConstantKeyText(Node key) {
    switch (key.Kind) {
      case "Int":
        return IntLiteralValue(key) is System.Numerics.BigInteger value ? "n" + value : null;
      case "Real":
        return double.TryParse(key.Text!.Replace("_", "").TrimEnd('f', 'F'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real)
          ? "n" + (real == 0 ? "0" : real.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) : null;
      case "String":
        return "s" + key.Text;
      case "Char":
      case "Bool":
        return key.Kind + key.Text;
      case "Null":
        return "null";
      case "Paren":
        return ConstantKeyText(key.Children[0]);
      case "Neg" when ConstantKeyText(key.Children[0]) is string inner && inner.StartsWith('n'):
        return inner == "n0" ? inner : inner.StartsWith("n-") ? "n" + inner[2..] : "n-" + inner[1..];
      case "Member" when Path(key) is string path && path.Split('.') is { Length: 2 } parts && IsEnum(parts[0]):
        return "e" + path;
      default:
        return null;
    }
  }

  // [k: v, ...] (spec 6.3): an empty map, then the entries are added in order. Keys are expressions; two equal constant keys are a compile error,
  // a key calculated at run time that comes again keeps the later value
  Expr MapLiteral(Node literal, Scope scope, string type) {
    NeedMapChunk(type);
    var seen = new Dictionary<string, Node>();
    foreach (var entry in literal.Children) {
      if (ConstantKeyText(entry.Children[0]) is string text && !seen.TryAdd(text, entry)) {
        throw Error("the same key is written twice in this map literal", entry);
      }
    }
    string keyType = MapKey(type);
    string valueType = MapValue(type);
    string held = NewTemp();
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {VecNewEmpty(type)}; ");
    foreach (var entry in literal.Children) {
      string key = NewTemp();
      string item = NewTemp();
      string slot = NewTemp();
      sb.Append("{ ").Append(MapKeyArg(entry.Children[0], scope, keyType, key))
        .Append($"{CType(valueType)} {item} = {MapValueItem(entry.Children[1], scope, valueType)}; ")
        .Append(MapSlot(held, type, key, slot, false)).Append(StoreValue(valueType, slot, item)).Append("} ");
    }
    sb.Append($"{held}; }})");
    return new Expr(sb.ToString(), type, true);
  }

  // C function that prints a map as [k: v, ...] in key order (null for a null map)
  string MapStrFunction(string mapType, Node at) {
    string name = "df_map_str_" + System.Text.RegularExpressions.Regex.Replace(mapType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      NeedMapChunk(mapType);
      string key = MapKey(mapType);
      string value = MapValue(mapType);
      string Cat(string type) => type == "string" ? "df_str_cat" : "df_str_catx";
      var sb = new StringBuilder();
      sb.Append($"static df_string {name}({CType(mapType)} v) {{\n");
      sb.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      sb.Append("  df_string r = df_str_lit(\"[\", 1);\n  df_uns shown = 0;\n");
      sb.Append($"  {EntryLoop("v", mapType, "c", " && shown < DF_PRINT_ITEMS", ", shown++")} {{\n");
      sb.Append("    if (shown > 0) {\n      r = df_str_catx(r, df_str_lit(\", \", 2));\n    }\n");
      sb.Append($"    {CType(key)} k = {CursorKey(mapType, "c")};\n");
      sb.Append($"    {CType(value)} x = {ReadSlot(mapType, CursorValue("v", mapType, "c"))};\n");
      sb.Append($"    r = {Cat(key)}(r, {StrOf(new Expr("k", key), at)});\n");
      sb.Append("    r = df_str_catx(r, df_str_lit(\": \", 2));\n");
      sb.Append($"    r = {Cat(value)}(r, {StrOf(new Expr("x", value), at)});\n  }}\n");
      sb.Append($"  return df_str_coll_end(r, {MapTree("v", mapType)}->count);\n}}\n\n");
      arrayCode.Append(sb);
    }
    return name;
  }

  // .sizeof of a map: the header plus the nodes of the tree and the slot of the null key, without what the keys and values own (spec 6.6)
  Expr MapSizeof(Expr obj) {
    NeedMapChunk(obj.Type);
    string held = NewTemp();
    string header = $"sizeof(struct {ObjInfo(obj.Type).StructName})";
    string bytes = IsHmap(obj.Type) ? $"df_ht_bytes({MapTree(held, obj.Type)}, {HmapShape(obj.Type)})"
      : $"df_bt_bytes({MapTree(held, obj.Type)}, {MapValueSize(obj.Type)}, {(KeyHasPointer(obj.Type) ? "true" : "false")})";
    return new Expr($"({{ {CType(obj.Type)} {held} = {NullChecked(obj.Code, obj.Type)}; (df_uns)({header} + {bytes}); }})", NativeUns);
  }

  // the end of a map: what its entries own is freed, then its nodes
  void EmitMapDropGlue(StringBuilder sb, ClassInfo cls) {
    NeedMapChunk(cls.Name);
    sb.Append("  ").Append(MapClearCode("self", cls.Name)).Append('\n');
  }

  // the members of a map that are not plain calls of the class
  Expr? MapMember(Expr map, string name, List<Node> args, Scope scope, Node at) {
    if (name.StartsWith("tree_")) {
      // the C of the methods of the class (VecPrelude): only its own code calls them
      return at.Line >= LibraryLine ? MapTreeMember(map, name, args, scope, at) : null;
    }
    return null;
  }

  // the tree_ members called by the methods of the class: the receiver is the object of the method
  Expr MapTreeMember(Expr map, string name, List<Node> args, Scope scope, Node at) {
    NeedMapChunk(map.Type);
    string type = map.Type;
    string keyType = MapKey(type);
    string valueType = MapValue(type);
    string valueC = CType(valueType);
    string slotC = SlotC(type);
    string m = NewTemp();
    string head = $"({{ {CType(type)} {m} = {map.Code}; ";
    string key = NewTemp();
    string slot = NewTemp();

    string Same(string slotPointer, string value) => EqualsCode(new Expr(ReadSlot(type, slotPointer), valueType), new Expr(value, valueType), at);

    switch (name) {
      case "tree_len":
        return new Expr($"((df_uns){MapTree(map.Code, type)}->count)", NativeUns);
      case "tree_hasKey":
        return new Expr(head + MapKeyArg(args[0], scope, keyType, key) + $"{MapFind(m, type, key)} != NULL; }})", "bool");
      case "tree_get":
        return new Expr(head + MapKeyArg(args[0], scope, keyType, key)
          + $"{slotC}* {slot} = ({slotC}*){MapFind(m, type, key)}; {slot} != NULL ? {ReadSlot(type, slot)} : {ZeroValue(valueType)}; }})", valueType);
      case "tree_set": {
        // the class checked the counter of open iterators when the key is new
        string value = NewTemp();
        return new Expr(head + MapKeyArg(args[0], scope, keyType, key) + $"{valueC} {value} = {MapValueItem(args[1], scope, valueType)}; "
          + MapSlot(m, type, key, slot, false) + StoreValue(valueType, slot, value) + "})", "void");
      }
      case "tree_take": {
        // the value leaves with its entry and goes to the caller; the key ends
        string taken = NewTemp();
        if (valueType == "string") {
          string buffer = NewTemp();
          return new Expr(head + MapKeyArg(args[0], scope, keyType, key) + $"uint8_t {buffer}[DF_BT_SVAL] = {{ 0 }}; (void){MapRemove(m, type, key, buffer)}; "
            + $"df_string {taken} = df_bt_sval_take({buffer}); {taken}; }})", valueType, true);
        }
        return new Expr(head + MapKeyArg(args[0], scope, keyType, key) + $"{valueC} {taken} = {ZeroValue(valueType)}; (void){MapRemove(m, type, key, "&" + taken)}; {taken}; }})",
          valueType);
      }
      case "tree_hasValue": {
        string value = NewTemp();
        string found = NewTemp();
        return new Expr(head + $"{valueC} {value} = {MapValueRead(args[0], scope, valueType)}; bool {found} = false; "
          + ForEachEntry(m, type, (s, c) => $"if ({Same(s, value)}) {{ {found} = true; break; }} ") + $"{found}; }})", "bool");
      }
      case "tree_keyOf": {
        string value = NewTemp();
        string result = NewTemp();
        return new Expr(head + $"{valueC} {value} = {MapValueRead(args[0], scope, valueType)}; {CType(keyType)} {result} = {ZeroValue(keyType)}; "
          + ForEachEntry(m, type, (s, c) => $"if ({Same(s, value)}) {{ {result} = {CursorKey(type, c)}; break; }} ") + $"{result}; }})", keyType);
      }
      case "tree_removeValue": {
        // the first entry in key order that holds the value
        string value = NewTemp();
        string found = NewTemp();
        string hit = NewTemp();
        return new Expr(head + $"{valueC} {value} = {MapValueRead(args[0], scope, valueType)}; bool {found} = false; {CType(keyType)} {hit} = {ZeroValue(keyType)}; "
          + ForEachEntry(m, type, (s, c) => $"if ({Same(s, value)}) {{ {found} = true; {hit} = {CursorKey(type, c)}; break; }} ")
          + $"if ({found}) {{ (void){MapRemove(m, type, hit, "NULL")}; }} {found}; }})", "bool");
      }
      case "tree_removeAllValues": {
        // one pass marks the entries (and frees what they own), then the tree is built again once (spec 6.8.4.1)
        string value = NewTemp();
        string removed = NewTemp();
        string tree = MapTree(m, type);
        string nullSlot = $"(({slotC}*){tree}->null_value)";
        // a string value is copied first: it may be one of the strings that are freed
        string given = valueType == "string" ? $"df_str_dup({MapValueRead(args[0], scope, valueType)})" : MapValueRead(args[0], scope, valueType);
        string release = valueType == "string" ? $"df_str_free({value}); " : "";
        if (IsHmap(type)) {
          // the null key is a normal entry of the table: one pass marks, then the table is built again once (spec 6.8.5.1)
          return new Expr(head + $"{valueC} {value} = {given}; "
            + ForEachEntry(m, type, (s, c) => $"if ({Same(s, value)}) {{ {DropEntry(type, s, c)}df_ht_mark({tree}, &{c}); }} ")
            + $"df_uns {removed} = df_ht_sweep({tree}, {HmapShape(type)}); {release}{removed}; }})", NativeUns);
        }
        return new Expr(head + $"{valueC} {value} = {given}; df_uns {removed} = 0; "
          + $"if ({tree}->has_null && {Same(nullSlot, value)}) {{ {(valueType == "string" ? $"df_bt_sval_clear({nullSlot}); " : "")}"
          + $"df_bt_remove_null({tree}, {MapValueSize(type)}, NULL); {removed}++; }} "
          + ForEachEntry(m, type, (s, c) => $"if (!{c}.on_null && {Same(s, value)}) {{ {DropEntry(type, s, c)}df_bt_mark(&{c}); }} ")
          + $"{removed} += df_bt_sweep({tree}, {MapValueSize(type)}, {(KeyHasPointer(type) ? "true" : "false")}); {release}{removed}; }})", NativeUns);
      }
      case "tree_clear":
        return new Expr(head + MapClearCode(m, type) + "})", "void");
      case "tree_capacity":
        return new Expr($"df_ht_capacity({MapTree(map.Code, type)})", NativeUns);
      case "tree_reserve": {
        var wanted = ExprOf(args[0], scope, NativeUns);
        return new Expr(head + $"df_ht_reserve({MapTree(m, type)}, (df_uns)({wanted.Code}), {HmapShape(type)}); }})", "void");
      }
      case "tree_shrinkToFit":
        return new Expr(head + $"df_ht_fit({MapTree(m, type)}, {HmapShape(type)}); }})", "bool");
      default:
        throw Error($"internal error: no map member {name}", at);
    }
  }

  // ---------------------------------------------------------------- .keys and .values (spec 6.5, 6.6)

  // the views of a map: "mapKeys" or "mapValues" (mapKeys, mapValues in VecPrelude; hmapKeys, hmapValues in HmapPrelude), or null
  string? MapViewKind(string type) =>
    !vecPrelude || !classInstanceArgs.TryGetValue(type, out var instance) ? null
    : instance.Template.Name is "mapKeys" or "hmapKeys" ? "mapKeys"
    : instance.Template.Name is "mapValues" or "hmapValues" ? "mapValues" : null;

  // m.keys or m.values written on a map: (the map, true for the keys), or null. The forms that only read them (a loop, .len, .has, !) work on the map
  // directly, so no view object is made for them
  (Expr Map, bool Keys)? MapViewOf(Node node, Scope scope) {
    if (!vecPrelude || node.Kind != "Member" || node.Text is not ("keys" or "values")) {
      return null;
    }
    Expr map;
    try {
      map = ExprOf(node.Children[0], scope);
    } catch (CompileException) {
      return null;
    }
    return IsMap(map.Type) ? (map, node.Text == "keys") : null;
  }

  // the map of a view held in a variable: a view whose map ended raises UsageOfDestroyedObjectException (spec 6.5)
  Expr MapOfView(Expr view) {
    var cls = ObjInfo(view.Type);
    string held = NewTemp();
    string parent = $"{held}->{cls.ParentField!.CName}";
    string code = $"({{ {CType(view.Type)} {held} = {NullChecked(view.Code, view.Type)}; "
      + $"if ({parent} == NULL) {{ df_raise(&df_desc_UsageOfDestroyedObjectException, {fn!.NameConst}); {RaiseJump} }} {parent}; }})";
    return new Expr(code, cls.ParentField.Type);
  }

  Expr MapLen(Expr map) {
    string held = NewTemp();
    return new Expr($"({{ {CType(map.Type)} {held} = {NullChecked(map.Code, map.Type)}; (df_uns){MapTree(held, map.Type)}->count; }})", NativeUns);
  }

  // m.keys! and m.values!: a vec<Key> or a vec<Value> with a copy of them, in key order (spec 6.6)
  Expr MapViewCopy(Expr map, bool keys, Node at) {
    NeedMapChunk(map.Type);
    string type = map.Type;
    string elem = keys ? MapKey(type) : MapValue(type);
    string vecType = ClassInstanceName(genericClasses["vec"], new List<string> { elem }, at);
    string held = NewTemp();
    string result = NewTemp();
    string count = NewTemp();
    string index = NewTemp();
    string items = $"{result}->{VecField(vecType, "items")}";
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {NullChecked(map.Code, type)}; df_uns {count} = {MapTree(held, type)}->count; {CType(vecType)} {result} = {VecNewEmpty(vecType)}; ");
    sb.Append($"{items} = ({ArrCType(elem)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; df_uns {index} = 0; ");
    sb.Append(ForEachEntry(held, type, (slot, cursor) => {
      string item = keys ? CursorKey(type, cursor) : ReadSlot(type, slot);
      return $"{items}.p[{index}++] = {(elem == "string" ? $"df_str_dup({item})" : item)}; ";
    }));
    sb.Append($"{result}->{VecField(vecType, "count")} = {index}; {result}; }})");
    return new Expr(sb.ToString(), vecType, true);
  }

  // C function that prints a view of a map as [a, b] in the order of the map
  string MapViewStrFunction(string viewType, Node at) {
    string name = "df_mapview_str_" + System.Text.RegularExpressions.Regex.Replace(viewType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      var cls = ObjInfo(viewType);
      string mapType = cls.ParentField!.Type;
      NeedMapChunk(mapType);
      bool keys = MapViewKind(viewType) == "mapKeys";
      string elem = keys ? MapKey(mapType) : MapValue(mapType);
      string map = $"v->{cls.ParentField.CName}";
      var sb = new StringBuilder();
      sb.Append($"static df_string {name}({CType(viewType)} v) {{\n");
      sb.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      sb.Append($"  if ({map} == NULL) {{\n    return df_str_lit(\"(destroyed)\", 11);\n  }}\n");
      sb.Append("  df_string r = df_str_lit(\"[\", 1);\n  df_uns shown = 0;\n");
      sb.Append($"  {EntryLoop(map, mapType, "c", " && shown < DF_PRINT_ITEMS", ", shown++")} {{\n");
      sb.Append("    if (shown > 0) {\n      r = df_str_catx(r, df_str_lit(\", \", 2));\n    }\n");
      string item = keys ? CursorKey(mapType, "c") : ReadSlot(mapType, CursorValue(map, mapType, "c"));
      sb.Append($"    {CType(elem)} x = {item};\n");
      sb.Append($"    r = {(elem == "string" ? "df_str_cat" : "df_str_catx")}(r, {StrOf(new Expr("x", elem), at)});\n  }}\n");
      sb.Append($"  return df_str_coll_end(r, {MapTree(map, mapType)}->count);\n}}\n\n");
      arrayCode.Append(sb);
    }
    return name;
  }

  // a view given only to be printed (print(m.keys)) ends after it: it gives the counter of its map back
  string MapViewStr(Expr view, Node at) {
    string function = MapViewStrFunction(view.Type, at);
    if (!view.Fresh) {
      return $"{function}({view.Code})";
    }
    string held = NewTemp();
    string text = NewTemp();
    return $"({{ {CType(view.Type)} {held} = {view.Code}; df_string {text} = {function}({held}); df_drop((void*){held}); {text}; }})";
  }
}
