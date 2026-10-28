using System.Text;
using dfparser;

namespace dfcompiler;

// Sets set<T> (alias bset, spec 6.8.6) and hset<T> (spec 6.8.7): the B+ tree of map (spec 6.8.4.1, RuntimeMap.cs) and the hash table of hmap
// (spec 6.8.5.1, RuntimeHmap.cs) with values of 0 bytes, so an item is a key of the tree or of the table. The classes set (VecPrelude) and hset
// (HmapPrelude) are written in Dflat and call the tree_ members, which are C emitted here with the helpers of CodeGen.Map.cs and CodeGen.Hmap.cs:
// IsHmap is true for an hset, MapKey is the item type and MapValue is "". Steps 2 and 3 of the sets plan: native and string items, add, has, remove,
// ->s[item], clear, .len, .sizeof, for (sorted for set), printing and literals; the capacity members on hset only.
public sealed partial class CodeGen {
  // a set of either kind
  bool IsSet(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name is "set" or "hset";

  bool IsHset(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "hset";

  // set<T> or hset<T> written as a type: the item types this version builds (the key types of map and hmap, spec 6.2)
  void CheckSetArgs(List<string> args, Node at, bool hashed) {
    string kind = hashed ? "hset" : "set";
    if (args.Count != 1) {
      throw Error($"{kind} takes one item type: {kind}<T>", at);
    }
    if (IsArr(args[0]) || IsVec(args[0]) || IsList(args[0]) || IsMap(args[0]) || IsSet(args[0]) || IsView(args[0])) {
      throw Error($"a collection cannot be an item of a set (spec 6.2): {DisplayName(args[0])}", at);
    }
    if (hashed && !HmapKeyBuilt(args[0])) {
      throw Error($"hset items of type {DisplayName(args[0])} are not built yet (this version: integers, bool, char, enums, reals, date, time, clock, lapse and string)", at);
    }
    if (!hashed && !MapKeyBuilt(args[0])) {
      throw Error($"set items of type {DisplayName(args[0])} are not built yet (this version: integers, bool, char, enums, reals, date, time and string)", at);
    }
  }

  // the collections whose items a set can take or remove (add(collection), s -= other, spec 6.6)
  bool IsItemCollection(string type) => IsArr(type) || IsVec(type) || IsList(type) || IsSet(type);

  // s += x and s -= x (spec 6.6, 6.8.6): s += item is s.add(item) and s += other is s.add(other); s -= item is s.remove(item) and s -= other removes
  // every item of other (a loop of remove, other being a variable or a field). A literal [a, b] on the right is one call per item; s -= s empties s
  Node SetOperator(Node s, Scope scope) {
    Node target = s.Children[0], value = s.Children[1];
    string method = s.Text == "+=" ? "add" : "remove";
    if (value.Kind == "List") {
      var block = Synthetic("Block", s);
      foreach (var item in value.Children) {
        block.Add(Synthetic("ExprStmt", s).Add(MemberCall(s, target, method, item)));
      }
      return block;
    }
    string? otherType = null;
    if (value.Kind is "Name" or "Member") {
      try {
        otherType = LValue(value, scope).Type;
      } catch (CompileException) {
        // not a variable or a field: a value
      }
    }
    string itemType = MapKey(LValue(target, scope).Type);
    if (otherType != null && IsItemCollection(otherType)) {
      string otherItem = IsArr(otherType) ? ArrElem(otherType) : IsVec(otherType) ? VecElem(otherType) : IsList(otherType) ? ListElem(otherType) : MapKey(otherType);
      if (otherItem != itemType) {
        throw Error($"the items of this set are {DisplayName(itemType)}, not {DisplayName(otherItem)}: {s.Text} takes a collection with the same item type (spec 6.6)", value);
      }
    }
    if (s.Text == "-=" && otherType != null && IsItemCollection(otherType)) {
      if (Path(value) is string path && path == Path(target)) {
        return Synthetic("ExprStmt", s).Add(MemberCall(s, target, "clear"));
      }
      string item = "__item" + ++tempCounter;
      var body = Synthetic("Block", s).Add(Synthetic("ExprStmt", s).Add(MemberCall(s, target, "remove", Synthetic("Name", s, item))));
      return Synthetic("For", s).Add(Synthetic("Var", s, item)).Add(Synthetic("In", s).Add(value)).Add(body);
    }
    return Synthetic("ExprStmt", s).Add(MemberCall(s, target, method, value));
  }

  // the type of a set literal without a declared item type: set s = [1, 2], hset s = [1, 2] (spec 6.3)
  string InferSetType(Node literal, Scope scope, Node at, bool hashed) {
    string kind = hashed ? "hset" : "set";
    if (literal.Children.Count == 0) {
      throw Error($"an empty literal needs a declared type: {kind}<T>", literal);
    }
    var items = literal.Children.Select(item => ExprOf(item, scope)).ToList();
    var args = new List<string> { CommonType(items, literal) };
    CheckSetArgs(args, at, hashed);
    return ClassInstanceName(genericClasses[kind], args, at);
  }

  // [a, b, ...] as a set (spec 6.3): an empty set, then the items are added in order. Two equal constant items are a compile error; an item
  // calculated at run time that comes again is added once
  Expr SetLiteral(Node literal, Scope scope, string type) {
    NeedMapChunk(type);
    var seen = new HashSet<string>();
    foreach (var item in literal.Children) {
      if (ConstantKeyText(item) is string text && !seen.Add(text)) {
        throw Error("the same item is written twice in this set literal", item);
      }
    }
    string held = NewTemp();
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {VecNewEmpty(type)}; ");
    foreach (var item in literal.Children) {
      string key = NewTemp();
      string slot = NewTemp();
      sb.Append("{ ").Append(MapKeyArg(item, scope, MapKey(type), key, true)).Append(MapSlot(held, type, key, slot, false)).Append($"(void){slot}; }} ");
    }
    sb.Append($"{held}; }})");
    return new Expr(sb.ToString(), type, true);
  }

  // C function that prints a set as [a, b, ...]: sorted for set, in the order of the table for hset (null for a null set)
  string SetStrFunction(string setType, Node at) {
    string name = "df_set_str_" + System.Text.RegularExpressions.Regex.Replace(setType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      NeedMapChunk(setType);
      string item = MapKey(setType);
      var sb = new StringBuilder();
      sb.Append($"static df_string {name}({CType(setType)} v) {{\n");
      sb.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      sb.Append("  df_string r = df_str_lit(\"[\", 1);\n  df_uns shown = 0;\n");
      sb.Append($"  {EntryLoop("v", setType, "c", " && shown < DF_PRINT_ITEMS", ", shown++")} {{\n");
      sb.Append("    if (shown > 0) {\n      r = df_str_catx(r, df_str_lit(\", \", 2));\n    }\n");
      sb.Append($"    {CType(item)} x = {CursorKey(setType, "c")};\n");
      sb.Append($"    r = {(item == "string" ? "df_str_cat" : "df_str_catx")}(r, {StrOf(new Expr("x", item), at)});\n  }}\n");
      sb.Append($"  return df_str_coll_end(r, {MapTree("v", setType)}->count);\n}}\n\n");
      arrayCode.Append(sb);
    }
    return name;
  }

  // the tree_ members called by the methods of the classes set and hset: the receiver is the object of the method
  Expr SetTreeMember(Expr set, string name, List<Node> args, Scope scope, Node at) {
    NeedMapChunk(set.Type);
    string type = set.Type;
    string itemType = MapKey(type);
    string s = NewTemp();
    string head = $"({{ {CType(type)} {s} = {set.Code}; ";
    string key = NewTemp();
    string tree = MapTree(s, type);
    switch (name) {
      case "tree_len":
        return new Expr($"((df_uns){MapTree(set.Code, type)}->count)", NativeUns);
      case "tree_has":
        return new Expr(head + MapKeyArg(args[0], scope, itemType, key, true) + $"{MapFind(s, type, key)} != NULL; }})", "bool");
      case "tree_add": {
        // the class checked that the item is new and the counter of open iterators; a string item is the set's own copy (MapSlot)
        string slot = NewTemp();
        return new Expr(head + MapKeyArg(args[0], scope, itemType, key, true) + MapSlot(s, type, key, slot, false) + $"(void){slot}; }})", "void");
      }
      case "tree_remove":
        return new Expr(head + MapKeyArg(args[0], scope, itemType, key, true) + $"{MapRemove(s, type, key, "NULL")}; }})", "bool");
      case "tree_take": {
        // the item leaves the set and goes to the caller: a string is the set's own copy, given away instead of freed
        string done = NewTemp();
        string zero = ZeroValue(itemType);
        if (itemType == "string") {
          string gone = NewTemp();
          string arg = head + MapKeyArg(args[0], scope, itemType, key, true);
          if (IsHmap(type)) {
            string words = NewTemp();
            return new Expr(arg + $"{HmapKeySlot(type, key, words)}df_ht_skey {gone} = {{ 0 }}; "
              + $"bool {done} = df_ht_remove({tree}, {words}, {HmapShape(type)}, NULL, &{gone}); {done} ? {gone}.k.s : {zero}; }})", itemType, true);
          }
          return new Expr(arg + $"void* {gone} = NULL; bool {done} = df_bt_remove({tree}, {SortKey(itemType, key)}, {KeyPointer(type, key)}, {MapShape(type)}, NULL, &{gone}); "
            + $"{done} ? (df_string){gone} : {zero}; }})", itemType, true);
        }
        return new Expr(head + MapKeyArg(args[0], scope, itemType, key, true) + $"bool {done} = {MapRemove(s, type, key, "NULL")}; {done} ? {key} : {zero}; }})",
          itemType);
      }
      case "tree_clear":
        return new Expr(head + MapClearCode(s, type) + "})", "void");
      case "tree_capacity":
        return new Expr($"df_ht_capacity({MapTree(set.Code, type)})", NativeUns);
      case "tree_reserve": {
        var wanted = ExprOf(args[0], scope, NativeUns);
        return new Expr(head + $"df_ht_reserve({tree}, (df_uns)({wanted.Code}), {HmapShape(type)}); }})", "void");
      }
      case "tree_shrinkToFit":
        return new Expr(head + $"df_ht_fit({tree}, {HmapShape(type)}); }})", "bool");
      default:
        throw Error($"internal error: no set member {name}", at);
    }
  }
}
