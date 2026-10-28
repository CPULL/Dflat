using System.Text;
using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// Arrays arr<T> (spec 6.1, 6.7): a pointer and a length. A first subset: literals, arr<T>[size], index read and
// write with a bounds check, .len, for-in, has / indexOf / fill / clear / reverse, text conversion.
// A null array has no pointer; arrays never grow, and are never freed yet.
public sealed partial class CodeGen {
  readonly StringBuilder arrayCode = new();
  readonly HashSet<string> arrayHelpers = new();

  static bool IsArr(string type) => type.StartsWith("arr<") && type.EndsWith('>');

  static string ArrElem(string type) => type[4..^1];

  // arr<string> and arr<u8> are defined by the runtime (program arguments, Split, ToCStr)
  string ArrCType(string elem) {
    string name = "df_arr_" + Regex.Replace(Mangle(elem), @"[^A-Za-z0-9_]", "_");
    string elemC = CType(elem);
    if (definedTypes.Add(name)) {
      typeDefs.Append($"typedef struct {{\n  {elemC}* p;\n  df_uns len;\n}} {name};\n\n");
    }
    return name;
  }

  // C function that prints an array as [a, b, c], or [a, b, c, ...] (37 items) (spec 6.1)
  string ArrStrFunction(string elem, Node at) {
    string name = "df_arr_str_" + Regex.Replace(Mangle(elem), @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      string item = StrOf(new Expr("a.p[i]", elem), at);
      arrayCode.Append($"static df_string {name}({ArrCType(elem)} a) {{\n");
      arrayCode.Append("  df_string r = df_str_lit(\"[\", 1);\n");
      arrayCode.Append("  for (df_uns i = 0; i < a.len && i < DF_PRINT_ITEMS; i++) {\n");
      arrayCode.Append("    if (i > 0) {\n      r = df_str_catx(r, df_str_lit(\", \", 2));\n    }\n");
      arrayCode.Append($"    r = {(elem == "string" ? "df_str_cat" : "df_str_catx")}(r, {item});\n  }}\n");
      arrayCode.Append("  return df_str_coll_end(r, a.len);\n}\n\n");
    }
    return name;
  }

  // a C expression (df_uns) for an index of any integer type; a negative value, or one past the range of uns, becomes huge and fails the bounds check
  string IndexValue(Expr index, Node at) {
    if (!IsInt(index.Type)) {
      throw Error($"an index must be an integer, not {index.Type}", at);
    }
    bool wide = Bits(index.Type) > Bits(NativeUns);
    if (SignedTypes.Contains(index.Type)) {
      return wide ? $"df_idx_arg((int64_t)({index.Code}))" : $"((df_uns)(int64_t)({index.Code}))";
    }
    return wide ? $"df_idx_wide((uint64_t)({index.Code}))" : $"((df_uns)({index.Code}))";
  }

  string RaiseJump => $"{{ df_err = df_pending; df_pending = NULL; df_err_thrown = false; goto {Handler}; }}";

  // arr<T>[size]: elements start as zero
  Expr ArrayNew(Node generic, Node sizeNode, Scope scope) {
    string target = generic.Children[0].Text!;
    if (target == "vec" && vecPrelude) {
      return VecNewWithCapacity(generic, sizeNode, scope);
    }
    if (target is "hmap" or "hset" && vecPrelude) {
      return HmapNewWithCapacity(generic, sizeNode, scope);
    }
    if (target == "list" && vecPrelude) {
      throw Error("a list has no capacity: the form list<T>[n] does not apply (spec 6.3)", generic);
    }
    if (target is "set" or "bset" && vecPrelude) {
      throw Error("a set has no capacity: the form set<T>[n] does not apply (spec 6.3); hset<T>[n] has one", generic);
    }
    if (target != "arr") {
      throw NotSupported("Collections", generic);
    }
    if (generic.Children.Count != 2) {
      throw Error("arr needs one element type: arr<T>[size]", generic);
    }
    string type = ResolveType(Synthetic("Type", generic, "arr").Add(generic.Children[1]));
    string elem = ArrElem(type);
    var size = ExprOf(sizeNode, scope, "u64");
    string count = NewTemp();
    string code = $"({{ df_uns {count} = {IndexValue(size, sizeNode)}; ({CType(type)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; }})";
    return new Expr(code, type);
  }

  // [a, b, c] with a known array type
  Expr ArrayLiteral(Node list, Scope scope, string type) {
    string elem = ArrElem(type);
    string array = NewTemp();
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {array} = {{ ({CType(elem)}*)df_calloc({list.Children.Count}, sizeof({CType(elem)})), {list.Children.Count} }}; ");
    for (int index = 0; index < list.Children.Count; index++) {
      var item = list.Children[index];
      var itemValue = Adopted(ExprOf(item, scope, elem), item);
      if (IsClass(elem) && !itemValue.Fresh && itemValue.Type != "null") {
        throw Error("a collection owns its items: put a new object in the literal, or move one in with ->x", item);
      }
      sb.Append($"{array}.p[{index}] = {StrItemAs(itemValue, elem, Convert(itemValue, elem, item))}; ");
    }
    sb.Append($"{array}; }})");
    return new Expr(sb.ToString(), type);
  }

  // element type of a literal assigned to a bare 'arr'
  string InferArrayType(Node list, Scope scope) {
    if (list.Children.Count == 0) {
      throw Error("an empty literal needs a declared type: arr<T>", list);
    }
    var values = list.Children.Select(item => ExprOf(item, scope)).ToList();
    return "arr<" + CommonType(values, list) + ">";
  }

  // the declared type of a variable; a bare 'arr' takes its element type from the literal
  string ResolveDeclType(Node decl, Scope? scope) {
    var typeNode = decl.Children.First(child => TypeNodeKinds.Contains(child.Kind));
    var init = decl.Children.FirstOrDefault(child => child.Kind is "Init" or "WeakInit");
    if (typeNode.Kind == "Type" && typeNode.Text == "arr" && typeNode.Children.Count == 0
        && init?.Children[0].Kind == "List" && scope != null) {
      return InferArrayType(init.Children[0], scope);
    }
    if (typeNode.Kind == "Type" && typeNode.Text == "vec" && typeNode.Children.Count == 0 && vecPrelude
        && init?.Children[0].Kind == "List" && scope != null) {
      return InferVecType(init.Children[0], scope, decl);
    }
    if (typeNode.Kind == "Type" && typeNode.Text == "list" && typeNode.Children.Count == 0 && vecPrelude
        && init?.Children[0].Kind == "List" && scope != null) {
      return InferListType(init.Children[0], scope, decl);
    }
    if (typeNode is { Kind: "Type", Text: "map" or "bmap" or "hmap", Children.Count: 0 } && vecPrelude && init?.Children[0].Kind == "MapLit" && scope != null) {
      // map ages = ["one": 1], hmap ages = ["one": 1]: the types come from the literal (spec 6.3)
      return InferMapType(init.Children[0], scope, decl, typeNode.Text == "hmap");
    }
    if (typeNode is { Kind: "Type", Text: "set" or "bset" or "hset", Children.Count: 0 } && vecPrelude && init?.Children[0].Kind == "List" && scope != null) {
      // set tags = ["red", "blue"], hset tags = [...]: the item type comes from the literal (spec 6.3)
      return InferSetType(init.Children[0], scope, decl, typeNode.Text == "hset");
    }
    // var x = value: the type of the value
    if (typeNode.Kind == "Type" && typeNode.Text == "var" && typeNode.Children.Count == 0 && !typeAliases.ContainsKey("var")) {
      if (init == null || scope == null) {
        throw Error("'var' needs a value to take its type from", decl);
      }
      string type = ExprOf(init.Children[0], scope).Type;
      if (type is "null" or "void" || IsMulti(type)) {
        throw Error($"cannot infer a variable type from {(type == "null" ? "null" : "this expression")}", decl);
      }
      return type;
    }
    return ResolveType(typeNode);
  }

  // a[i] and text[i]
  Expr IndexExpr(Node n, Scope scope) {
    if (n.Children[0].Kind == "GenericRef") {
      return ArrayNew(n.Children[0], n.Children[1], scope);
    }
    if (n.Children[1].Kind == "Range") {
      return ViewExpr(n, scope);
    }
    var obj = ExprOf(n.Children[0], scope);
    if (IsView(obj.Type)) {
      return ObjectCall(obj, "read_pos", new List<Node> { n.Children[1] }, scope, n) ?? throw Error("no read_pos", n);
    }
    if (IsMap(obj.Type)) {
      return MapIndex(obj, n.Children[1], scope);
    }
    var index = ExprOf(n.Children[1], scope, "u64");
    if (obj.Type == "string") {
      string call = $"df_str_char_at({StrRead(obj)}, {IndexValue(index, n)}, {fn!.NameConst})";
      return new Expr(CheckRaise(Need("text", call), "char"), "char");
    }
    if (IsVec(obj.Type)) {
      return VecIndex(obj, index, n);
    }
    if (IsList(obj.Type)) {
      return ListIndex(obj, index, n);
    }
    if (IsSet(obj.Type)) {
      throw Error("a set has no s[k]: has(item) tells whether an item is there, ->s[item] takes it out (spec 6.6)", n);
    }
    if (!IsArr(obj.Type)) {
      throw NotSupported("Collections", n);
    }
    string elem = ArrElem(obj.Type);
    string code = WithTemp(obj, array => WithTemp(new Expr(IndexValue(index, n), NativeUns), position =>
      CheckRaise($"(df_idx_ok({array}.len, {position}, {fn!.NameConst}) ? {array}.p[{position}] : {ZeroValue(elem)})", elem)));
    return new Expr(code, elem);
  }

  // a[i] = v, a[i] += v ...
  string IndexAssign(Node s, Scope scope) {
    var target = s.Children[0];
    if (ReadOnlyVariable(target.Children[0], scope) is not null) {
      throw Error($"'{target.Children[0].Text}' is a read-only reference (&): its items cannot be assigned", s);
    }
    var obj = ExprOf(target.Children[0], scope);
    if (obj.Type == "string") {
      return StrSetChar(target, s.Children[1], s.Text!, scope, s);
    }
    if (IsView(obj.Type)) {
      throw Error("a view is read-only: its items cannot be assigned (spec 6.5)", s);
    }
    if (IsList(obj.Type) && target.Children[0].Kind != "GenericRef") {
      return ListIndexAssign(s, scope, obj, target);
    }
    if (IsMap(obj.Type)) {
      return MapIndexAssign(s, scope, obj, target);
    }
    if (IsSet(obj.Type)) {
      throw Error("a set has no s[k] = v: add(item) adds an item (spec 6.6)", s);
    }
    bool isVec = IsVec(obj.Type);
    if (!(IsArr(obj.Type) || isVec) || target.Children[0].Kind == "GenericRef") {
      throw NotSupported("Collections", s);
    }
    string elem = isVec ? VecElem(obj.Type) : ArrElem(obj.Type);
    var index = ExprOf(target.Children[1], scope, "u64");
    string array = NewTemp();
    string position = NewTemp();
    string slot = isVec ? $"{array}->{VecField(obj.Type, "items")}.p[{position}]" : $"{array}.p[{position}]";
    string length = isVec ? $"{array}->{VecField(obj.Type, "count")}" : $"{array}.len";
    string holder = isVec ? NullChecked(obj.Code, obj.Type) : obj.Code;
    string op = s.Text!;
    if (op == "_=") {
      throw NotSupported("Ownership", s);
    }
    if (op == "??=") {
      throw Error("'??=' on an array element is not supported yet", s);
    }
    string value;
    if (op == "=") {
      var assigned = Adopted(ExprOf(s.Children[1], scope, elem), s);
      if (IsClass(elem) && !assigned.Fresh && assigned.Type != "null" && s.Line < LibraryLine) {
        throw Error("a collection owns its items: move an existing object in with ->x, or put a new one", s);
      }
      value = StrItemAs(assigned, elem, Convert(assigned, elem, s));
    } else {
      value = Convert(BinaryOp(op[..^1], new Expr(slot, elem), s.Children[1], scope, s), elem, s);
    }
    if (op == "=" && IsClass(elem)) {
      // the collection owns its items: the one that is replaced ends (unless it is the same object)
      string replaced = NewTemp();
      string incoming = NewTemp();
      return $"({{ {CType(obj.Type)} {array} = {holder}; df_uns {position} = {IndexValue(index, target)}; {CType(elem)} {incoming} = {value}; "
        + $"if (!df_idx_ok({length}, {position}, {fn!.NameConst})) {RaiseJump} {CType(elem)} {replaced} = {slot}; {slot} = {incoming}; "
        + $"if ({replaced} != {incoming}) {{ df_drop((void*){replaced}); }} }})";
    }
    // a string item that is replaced ends
    string store = elem == "string" ? $"df_str_put(&{slot}, {value}, true);" : $"{slot} = {value};";
    return $"({{ {CType(obj.Type)} {array} = {holder}; df_uns {position} = {IndexValue(index, target)}; "
      + $"if (!df_idx_ok({length}, {position}, {fn!.NameConst})) {RaiseJump} {store} }})";
  }

  // members of an array
  Expr? ArrayMethod(Expr array, string name, List<Node> args, Scope scope, Node at) {
    string elem = ArrElem(array.Type);
    string Elem(string a, string i) => $"{a}.p[{i}]";

    Expr Value(Node arg) {
      var value = ExprOf(arg, scope, elem);
      return new Expr(Convert(value, elem, arg), elem);
    }

    void Arity(int count) {
      if (args.Count != count) {
        throw Error($"{name} takes {count} argument(s), not {args.Count}", at);
      }
    }

    switch (name) {
      case "has":
      case "indexOf": {
        Arity(1);
        var value = Value(args[0]);
        string found = NewTemp();
        string i = NewTemp();
        string code = WithTemp(array, a => WithTemp(value, v => {
          string same = EqualsCode(new Expr(Elem(a, i), elem), new Expr(v, elem), at);
          return name == "has"
            ? $"({{ bool {found} = false; for (df_uns {i} = 0; {i} < {a}.len; {i}++) {{ if ({same}) {{ {found} = true; break; }} }} {found}; }})"
            : $"({{ df_uns {found} = DF_UNS_MAX; for (df_uns {i} = 0; {i} < {a}.len; {i}++) {{ if ({same}) {{ {found} = {i}; break; }} }} {found}; }})";
        }));
        return new Expr(code, name == "has" ? "bool" : NativeUns);
      }
      case "fill": {
        Arity(1);
        if (IsClass(elem) && args[0].Kind != "Null") {
          throw Error("fill would put one object in every item: a class instance is not allowed, fill(null) is", at);
        }
        if (elem == "string") {
          // every item gets its own copy; the strings the items held end
          var text = ExprOf(args[0], scope, elem);
          string read = StrRead(new Expr(Convert(text, elem, args[0]), "string", text.Fresh));
          string at2 = NewTemp();
          return new Expr(WithTemp(array, a => $"({{ df_string {at2} = {read}; for (df_uns k_{at2} = 0; k_{at2} < {a}.len; k_{at2}++) {{ df_str_put(&{Elem(a, $"k_{at2}")}, df_str_dup({at2}), true); }} }})"), "void");
        }
        var value = Value(args[0]);
        string i = NewTemp();
        string replacedItem = NewTemp();
        string code = WithTemp(array, a => WithTemp(value, v => IsClass(elem)
          // the objects the items held end (fill(null))
          ? $"({{ for (df_uns {i} = 0; {i} < {a}.len; {i}++) {{ {CType(elem)} {replacedItem} = {Elem(a, i)}; {Elem(a, i)} = {v}; if ({replacedItem} != {v}) {{ df_drop((void*){replacedItem}); }} }} }})"
          : $"({{ for (df_uns {i} = 0; {i} < {a}.len; {i}++) {{ {Elem(a, i)} = {v}; }} }})"));
        return new Expr(code, "void");
      }
      case "clear": {
        Arity(0);
        string i = NewTemp();
        string gone = NewTemp();
        // every item gets its default value; the objects the items held end (spec 6.1)
        string reset = IsClass(elem)
          ? $"if ({{0}} != NULL) {{{{ void* {gone} = (void*){{0}}; {{0}} = NULL; df_drop({gone}); }}}}"
          : elem == "string" ? "df_str_free({0}); {0} = NULL;" : $"{{0}} = {ZeroValue(elem)};";
        string code = WithTemp(array, a =>
          $"({{ for (df_uns {i} = 0; {i} < {a}.len; {i}++) {{ {string.Format(reset, Elem(a, i))} }} }})");
        return new Expr(code, "void");
      }
      case "reverse": {
        Arity(0);
        string i = NewTemp();
        string swap = NewTemp();
        string code = WithTemp(array, a =>
          $"({{ for (df_uns {i} = 0; {i} < {a}.len / 2; {i}++) {{ {CType(elem)} {swap} = {Elem(a, i)}; {Elem(a, i)} = {a}.p[{a}.len - 1 - {i}]; {a}.p[{a}.len - 1 - {i}] = {swap}; }} }})");
        return new Expr(code, "void");
      }
      case "sort":
      case "sortUnstable": {
        if (args.Count > 1) {
          throw Error($"{name} takes no argument or a SortDirection", at);
        }
        string? compare = CompareFunction(elem);
        if (compare == null) {
          throw Error($"{name} cannot order {elem} yet (objects and tuples need a comparer)", at);
        }
        string descending = SortDescending(name, args, scope, at);
        NeedChunk("sort");
        string code = WithTemp(array, a =>
          $"df_sort({a}.p, {a}.len, sizeof(*{a}.p), {compare}, {(name == "sort" ? "true" : "false")}, {descending})");
        return new Expr(code, "void");
      }
      case "copyFrom":
        return CopyFrom(array, args, scope, at);
      case "getIterator":
        Arity(0);
        return ArrayIterator(array, at);
      case "swap":
        return ArraySwap(array, args, scope, at);
      case "replace":
        return ArrayReplace(array, args, scope, at);
      default:
        return null;
    }
  }

  // the C of the direction of sort(SortDirection.DESC): true when descending; sort() is ascending
  string SortDescending(string name, List<Node> args, Scope scope, Node at) {
    if (args.Count > 1) {
      throw Error($"{name} takes no argument or a SortDirection", at);
    }
    if (args.Count == 0) {
      return "false";
    }
    if (args[0].Kind is "Lambda" or "Fun" or "Closure") {
      throw NotSupported("Lambdas", args[0]);
    }
    var direction = ExprOf(args[0], scope);
    if (direction.Type != "SortDirection") {
      throw Error($"{name} takes SortDirection.ASC or SortDirection.DESC, not {direction.Type}", args[0]);
    }
    return $"(({direction.Code}) == {enums["SortDirection"].Find("DESC")})";
  }

  // sequence.swap(i, j) exchanges two items, with no change of ownership (spec 6.6); a new item goes in with replace(position, value)
  Expr ArraySwap(Expr array, List<Node> args, Scope scope, Node at) {
    CheckSwapArguments(args, scope, at);
    string elem = ArrElem(array.Type);
    var first = ExprOf(args[0], scope, "u64");
    var other = ExprOf(args[1], scope, "u64");
    string a = NewTemp();
    string i = NewTemp();
    string j = NewTemp();
    string swapped = NewTemp();
    string code = $"({{ {CType(array.Type)} {a} = {array.Code}; df_uns {i} = {IndexValue(first, args[0])}; df_uns {j} = {IndexValue(other, args[1])}; "
      + $"if (!df_idx_ok({a}.len, {i}, {fn!.NameConst})) {RaiseJump} if (!df_idx_ok({a}.len, {j}, {fn!.NameConst})) {RaiseJump} "
      + $"{CType(elem)} {swapped} = {a}.p[{i}]; {a}.p[{i}] = {a}.p[{j}]; {a}.p[{j}] = {swapped}; }})";
    return new Expr(code, "void");
  }

  // swap takes two positions: there is no swap(position, new) (spec 6.6)
  void CheckSwapArguments(List<Node> args, Scope scope, Node at) {
    if (args.Count != 2 || args.Any(arg => arg.Kind == "Named")) {
      throw Error("swap takes two positions: swap(i, j)", at);
    }
    if (args[1].Kind == "Gift" || !IsInt(ExprOf(args[1], scope).Type)) {
      throw Error("swap exchanges two items: swap(i, j); to put a new item at a position use replace(position, value)", args[1]);
    }
  }

  // Item old = sequence.replace(position, value): the new item goes in, the old one is given back and its ownership moves to the caller (spec 6.6)
  Expr ArrayReplace(Expr array, List<Node> args, Scope scope, Node at) {
    if (args.Count != 2 || args.Any(arg => arg.Kind == "Named")) {
      throw Error("replace takes a position and the new item: replace(position, value)", at);
    }
    string elem = ArrElem(array.Type);
    var first = ExprOf(args[0], scope, "u64");
    var replacement = Adopted(ExprOf(args[1], scope, elem), args[1]);
    if (IsClass(elem) && !replacement.Fresh && replacement.Type != "null") {
      throw Error("a collection owns its items: move an existing object in with ->x, or put a new one", args[1]);
    }
    string a = NewTemp();
    string i = NewTemp();
    string incoming = NewTemp();
    string old = NewTemp();
    string code = $"({{ {CType(array.Type)} {a} = {array.Code}; df_uns {i} = {IndexValue(first, args[0])}; {CType(elem)} {incoming} = {StrItemAs(replacement, elem, Convert(replacement, elem, args[1]))}; "
      + $"if (!df_idx_ok({a}.len, {i}, {fn!.NameConst})) {RaiseJump} {CType(elem)} {old} = {a}.p[{i}]; {a}.p[{i}] = {incoming}; {old}; }})";
    return new Expr(code, elem, IsClass(elem) || elem == "string");
  }

  // arr<T>[n] name = [] / = [items] / = arr<T>[n]: the size on the declaration (spec 6.8.1); vec<T>[n] name (= []): the capacity on the declaration,
  // an empty vec (spec 6.3). The declaration is rewritten before the compilation
  void RewriteSizedArrays(Node node) {
    foreach (var child in node.Children) {
      RewriteSizedArrays(child);
    }
    if (node.Kind != "Decl") {
      return;
    }
    var size = node.Children.FirstOrDefault(child => child.Kind == "Size");
    var typeNode = node.Children.FirstOrDefault(child => child.Kind == "Type");
    if (size != null && typeNode is { Text: "set" or "bset" } && vecPrelude) {
      throw Error("a set has no capacity: the form set<T>[n] name does not apply (spec 6.3); hset<T>[n] has one", node);
    }
    if (size == null || typeNode is not ({ Text: "arr" or "vec" or "hset", Children.Count: 1 } or { Text: "hmap", Children.Count: 2 })) {
      return;   // the others (hset, list, ...) are not built (EmitDecl says so)
    }
    var init = node.Children.FirstOrDefault(child => child.Kind == "Init");
    var sizeExpr = size.Children[0];
    long? declared = IntLiteralValue(sizeExpr) is System.Numerics.BigInteger value ? (long)value : null;
    Node NewArray() {
      var generic = Synthetic("GenericRef", size).Add(Synthetic("Name", size, typeNode.Text!));
      foreach (var argument in typeNode.Children) {
        generic.Add(argument);
      }
      return Synthetic("Index", size).Add(generic).Add(sizeExpr);
    }
    node.Children.Remove(size);
    if (typeNode.Text is "vec" or "hmap" or "hset") {
      // hmap<K, V>[n] name, hset<T>[n] name (spec 6.3): an empty hmap or hset with room for n entries
      if (init != null && !(init.Children[0].Kind == "List" && init.Children[0].Children.Count == 0)) {
        throw Error($"{typeNode.Text}<{(typeNode.Text == "hmap" ? "K, V" : "T")}>[n] name is an empty {typeNode.Text} with capacity n: it takes nothing or []", node);
      }
      if (init != null) {
        node.Children.Remove(init);
      }
      node.Add(Synthetic("Init", size).Add(NewArray()));
      return;
    }
    if (init == null || (init.Children[0].Kind == "List" && init.Children[0].Children.Count == 0)) {
      node.Children.Remove(init!);
      node.Add(Synthetic("Init", size).Add(NewArray()));
      return;
    }
    var written = init.Children[0];
    if (written.Kind == "List") {
      if (declared != null && declared != written.Children.Count) {
        throw Error($"arr<T>[{declared}] {node.Text} has a literal of {written.Children.Count} items: the sizes must be the same", node);
      }
      return;
    }
    if (written.Kind == "Index" && written.Children[0].Kind == "GenericRef" && written.Children.Count == 2) {
      long? other = IntLiteralValue(written.Children[1]) is System.Numerics.BigInteger otherValue ? (long)otherValue : null;
      if (declared != null && other != null && declared != other) {
        throw Error($"the size on the declaration ({declared}) and the size of arr<T>[{other}] are different", node);
      }
      return;
    }
    throw Error("arr<T>[n] name takes nothing, [], a literal of n items or arr<T>[n]", node);
  }

  // the comparer of the runtime for an item type (RuntimeSort), or null when there is none
  string? CompareFunction(string elem) {
    if (enums.TryGetValue(elem, out var info)) {
      return CompareFunction(info.ValueType);
    }
    return elem switch {
      "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or "r32" or "r64" => "df_cmp_" + elem,
      "bool" => "df_cmp_bool",
      "char" => "df_cmp_u32",
      "string" => "df_cmp_str",
      "date" or "lapse" => "df_cmp_i64",
      "time" => "df_cmp_i32",
      "clock" => "df_cmp_u64",
      _ => null
    };
  }

  // the length of an array made from a literal list or arr<T>[constant]; null when it is not known
  static long? ConstantArrayLength(Node init) {
    if (init.Kind == "List") {
      return init.Children.Count;
    }
    if (init.Kind == "Index" && init.Children.Count == 2 && init.Children[0].Kind == "GenericRef" && IntLiteralValue(init.Children[1]) is System.Numerics.BigInteger size
        && size >= 0 && size < int.MaxValue) {
      return (long)size;
    }
    return null;
  }

  // is the variable assigned again, or handed out as (&)name, anywhere in the function (or the program) that declares it?
  bool AssignedAgain(string name) {
    bool Walk(Node node) {
      if (node.Kind == "Assign" && node.Children.Count > 0 && node.Children[0] is { Kind: "Name" } target && target.Text == name) {
        return true;
      }
      if (node.Kind == "WritableAddressOf" && node.Children.Count > 0 && node.Children[0] is { Kind: "Name" } address && address.Text == name) {
        return true;
      }
      if (node.Kind == "MultiAssign" && node.Children.Count > 0 && node.Children[0].Children.Any(target => target.Kind == "Name" && target.Text == name)) {
        return true;
      }
      return node.Children.Any(Walk);
    }
    var root = currentFun?.Node ?? ProgramNode;
    return root == null || Walk(root);
  }

  long? KnownLengthOf(Node? node, Scope scope) {
    return node is { Kind: "Name" } && scope.Find(node.Text!) is { KnownLength: long length } ? length : null;
  }

  static long? ConstantIndex(Node node) {
    return IntLiteralValue(node) is System.Numerics.BigInteger value && value >= long.MinValue && value <= long.MaxValue ? (long)value : null;
  }

  // copyFrom with constant indexes on arrays of known length: a copy that cannot fit is a compile error (spec 6.6)
  void CheckCopyFromRanges(List<Node> args, Scope scope, Node at) {
    Node? receiver = at.Kind == "Call" && at.Children[0].Kind == "Member" ? at.Children[0].Children[0] : null;
    long? destinationLength = KnownLengthOf(receiver, scope);
    long? sourceLength = KnownLengthOf(args[1], scope);
    long? dstIndex = ConstantIndex(args[0]);
    long? srcIndex;
    long? count;
    if (args.Count == 4) {
      srcIndex = ConstantIndex(args[2]);
      count = ConstantIndex(args[3]);
    } else if (args[2].Kind == "Range") {
      var range = args[2];
      srcIndex = range.Children[0].Kind == "Open" ? 0 : ConstantIndex(range.Children[0]);
      long? end = range.Children[1].Kind == "Open" ? sourceLength : ConstantIndex(range.Children[1]);
      count = srcIndex != null && end != null ? end - srcIndex + (range.Text == "..=" ? 1 : 0) : null;
    } else {
      return;
    }
    if ((dstIndex ?? 0) < 0 || (srcIndex ?? 0) < 0 || (count ?? 0) < 0) {
      throw Error("copyFrom: the indexes and the count cannot be negative", at);
    }
    if (dstIndex != null && count != null && destinationLength != null && dstIndex + count > destinationLength) {
      throw Error($"copyFrom: {count} items do not fit from index {dstIndex} of {destinationLength} items", at);
    }
    if (srcIndex != null && count != null && sourceLength != null && srcIndex + count > sourceLength) {
      throw Error($"copyFrom: {count} items from index {srcIndex} do not fit in the source of {sourceLength} items", at);
    }
  }

  // destination.copyFrom(dstIndex, source, srcIndex, count) and destination.copyFrom(dstIndex, source, range) (spec 6.6)
  Expr CopyFrom(Expr destination, List<Node> args, Scope scope, Node at) {
    if (args.Any(arg => arg.Kind == "Named") || args.Count is not (3 or 4)) {
      throw Error("copyFrom takes (dstIndex, source, srcIndex, count) or (dstIndex, source, range)", at);
    }
    string elem = ArrElem(destination.Type);
    if (IsClass(elem)) {
      throw Error("copyFrom clones object items, which needs cloning (not built yet)", at);
    }
    var source = ExprOf(args[1], scope);
    if ((IsVec(source.Type) || IsList(source.Type)) && IndexCollectionElem(source.Type) == elem) {
      return CollectionCopyFrom(destination, args, scope, at);   // from a vec or a list (spec 6.6)
    }
    if (!IsArr(source.Type) || ArrElem(source.Type) != elem) {
      throw Error($"copyFrom needs an {destination.Type} as the source, not {source.Type}", args[1]);
    }
    if (destination.Code == source.Code && SimpleCode.IsMatch(source.Code)) {
      throw Error("copyFrom cannot use the same array as source and destination", at);
    }
    string Index(Node node) {
      var value = ExprOf(node, scope, "u64");
      if (!IsInt(value.Type)) {
        throw Error($"an index must be an integer, not {value.Type}", node);
      }
      return SignedTypes.Contains(value.Type) ? $"df_idx_arg((int64_t)({value.Code}))" : IndexValue(value, node);
    }
    CheckCopyFromRanges(args, scope, at);
    NeedChunk("sort");
    string dstIndex = Index(args[0]);
    string source0 = NewTemp();
    string srcIndex;
    string count;
    if (args.Count == 4) {
      srcIndex = Index(args[2]);
      count = Index(args[3]);
    } else {
      var range = args[2];
      if (range.Kind != "Range") {
        throw Error("copyFrom takes a count or a range after the source", range);
      }
      srcIndex = range.Children[0].Kind == "Open" ? "0" : Index(range.Children[0]);
      if (range.Children[1].Kind == "Open") {
        count = $"({source0}.len < ({srcIndex}) ? DF_UNS_MAX : {source0}.len - ({srcIndex}))";
      } else {
        string end = Index(range.Children[1]);
        string last = range.Text == "..=" ? $"({end}) + 1" : $"({end})";
        count = $"(({last}) < ({srcIndex}) ? DF_UNS_MAX : ({last}) - ({srcIndex}))";
      }
    }
    string d = NewTemp();
    string di = NewTemp();
    string si = NewTemp();
    string n = NewTemp();
    string code = $"({{ {CType(destination.Type)} {d} = {destination.Code}; {CType(source.Type)} {source0} = {source.Code}; "
      + $"df_uns {di} = {dstIndex}; df_uns {si} = {srcIndex}; df_uns {n} = {count}; "
      + $"if ({d}.p != NULL && {d}.p == {source0}.p) {{ df_raise(&df_desc_ModifyingProtectedSourceException, {fn!.NameConst}); {RaiseJump} }} "
      + $"if ({di} > {d}.len || {n} > {d}.len - {di} || {si} > {source0}.len || {n} > {source0}.len - {si}) {{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }} "
      + (elem == "string"
        ? $"for (df_uns k_{n} = 0; k_{n} < {n}; k_{n}++) {{ df_str_put(&{d}.p[{di} + k_{n}], df_str_dup({source0}.p[{si} + k_{n}]), true); }} }})"
        : $"if ({n} > 0) {{ memcpy({d}.p + {di}, {source0}.p + {si}, {n} * sizeof(*{d}.p)); }} }})");
    return new Expr(code, "void");
  }

  // Root.Random.shuffle(array or string variable)
  Expr ShuffleCall(List<Node> args, Scope scope, Node at) {
    if (args.Count != 1 || args[0].Kind == "Named") {
      throw Error("shuffle takes one collection", at);
    }
    var arg = args[0];
    var value = ExprOf(arg, scope);
    if (IsArr(value.Type)) {
      string code = WithTemp(value, a => $"df_shuffle({a}.p, {a}.len, sizeof(*{a}.p))");
      return new Expr(Need("text", code), "void");
    }
    if (value.Type == "string") {
      if (arg.Kind != "Name" && arg.Kind != "Member") {
        throw Error("shuffle of a string needs a variable to hold the result", at);
      }
      // in place (spec 4.1)
      var target = LValue(arg, scope);
      var (slot, flag) = StrPlace(arg, scope, at)!.Value;
      return new Expr(Need("text", $"df_str_take({slot}, {flag}, df_str_shuffle({target.Code}))"), "void");
    }
    throw Error($"shuffle works on arrays and strings, not {value.Type}", at);
  }

  // the for loops over a vec or a list being compiled: the C variable that points to the counter of open iterators, and the collection
  readonly Dictionary<Node, (string? Counter, Expr Collection)> countedLoops = new();
  readonly Dictionary<Node, string> mapLoopParts = new();   // a loop over m.keys or m.values: which part (CodeGen.Map.cs)

  // for T item in collection { }: arrays by position, strings by character
  void EmitForEach(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var varNode = s.Children[0];
    var source = s.Children[1].Children[0];
    var block = s.Children.Last();
    Expr collection;
    // for k in m.keys, for v in m.values, and a view of a map held in a variable: the loop walks the map (CodeGen.Map.cs)
    string? mapPart = null;
    if (countedLoops.TryGetValue(s, out var counted)) {
      collection = counted.Collection;
      mapLoopParts.TryGetValue(s, out mapPart);
    } else if (MapViewOf(source, scope) is (Expr viewMap, bool viewKeys)) {
      collection = viewMap;
      mapPart = viewKeys ? "keys" : "values";
    } else if (source.Kind == "List" && source.Children.Count > 0 && varNode.Kind == "Var") {
      // for i32 k in [0, -1, 1]: an array of the type of the variable; for var k in [...]: the type of the items
      var itemType = varNode.Children.FirstOrDefault();
      collection = ExprOf(source, scope, itemType == null || itemType is { Kind: "Type", Text: "var", Children.Count: 0 }
        ? InferArrayType(source, scope) : $"arr<{ResolveType(itemType)}>");
    } else {
      collection = ExprOf(source, scope);
      if (MapViewKind(collection.Type) is string viewKind) {
        collection = MapOfView(collection);
        mapPart = viewKind == "mapKeys" ? "keys" : "values";
      }
    }
    bool destructure = varNode.Kind == "Destructure";
    if (IsSet(collection.Type)) {
      // a set is walked like the keys of its table (CodeGen.Hset.cs); its items are read-only (spec 6.4)
      if (destructure) {
        throw Error("a set gives one item at a time: for x in s", s);
      }
      if (varNode.Kind == "WritableVar") {
        throw Error("the items of a set are read-only: (&) is not allowed (spec 6.4); to change an item, remove it and add the new one", s);
      }
      mapPart = "keys";
    }
    if (mapPart != null) {
      mapLoopParts[s] = mapPart;
    }
    string elem;
    bool overMap = IsMap(collection.Type) || IsSet(collection.Type);
    if (mapPart != null && destructure) {
      throw Error($"m.{mapPart} gives one value at a time: for x in m.{mapPart}; for (k, v) in m gives both", s);
    }
    if (destructure && !overMap) {
      throw Error("for (a, b) in ... takes the (key, value) entries of a map; other collections give one item: for x in c", s);
    }
    if (overMap && !destructure && mapPart == null) {
      throw Error("a map gives (key, value) entries: for (k, v) in m, for (K k, V v) in m or for var (k, v) in m (spec 6.4)", s);
    }
    bool overList = IsList(collection.Type);
    bool overView = IsView(collection.Type);
    bool overVec = IsVec(collection.Type) || overList;   // the list is walked through its nodes: its own loop below
    if ((overVec || overMap) && counted.Counter == null) {
      // the loop holds the counter of open iterators of the collection (spec 6.4): it is wrapped in a block whose atEnd gives it back on every
      // way out (the end, break, return, an exception)
      tempCounter++;
      string counter = $"df_open{tempCounter}";
      countedLoops[s] = (counter, collection);
      var release = Synthetic("RawC", s, $"if ({counter}) {{ *{counter} -= 1; }}");
      var wrapper = Synthetic("Block", s).Add(s).Add(Synthetic("AtEnd", s, "drop").Add(Synthetic("Block", s).Add(release)));
      sb.Append($"{pad}{{\n{pad}  df_uns* {counter} = NULL;\n");
      EmitBlock(wrapper, sb, scope, indent + 1);
      sb.Append($"{pad}}}\n");
      countedLoops.Remove(s);
      return;
    }
    string? customElem = overVec || overView || overMap ? null : CustomCollectionElem(collection.Type);
    if (customElem != null && counted.Counter == null) {
      // a custom collection: the iterator is an owned variable of a block around the loop, so it ends (and gives back the counter of open
      // iterators, spec 6.4) on every way out
      tempCounter++;
      string iteratorName = $"__iter{tempCounter}";
      countedLoops[s] = (iteratorName, collection);
      var getIterator = Synthetic("Call", s).Add(Synthetic("Member", s, "getIterator").Add(source)).Add(Synthetic("Args", s));
      var declaration = Synthetic("Decl", s, iteratorName).Add(Synthetic("Type", s, "var")).Add(Synthetic("Init", s).Add(getIterator));
      EmitBlock(Synthetic("Block", s).Add(declaration).Add(s), sb, scope, indent);
      countedLoops.Remove(s);
      return;
    }
    if (overMap) {
      elem = mapPart == "values" ? MapValue(collection.Type) : MapKey(collection.Type);
    } else if (overView) {
      elem = ViewElem(collection.Type);
    } else if (overList) {
      elem = ListElem(collection.Type);
    } else if (overVec) {
      elem = VecElem(collection.Type);
    } else if (customElem != null) {
      elem = customElem;
    } else if (IsArr(collection.Type)) {
      elem = ArrElem(collection.Type);
    } else if (collection.Type == "string") {
      elem = "char";
    } else {
      throw Error($"cannot loop over {collection.Type} yet", s);
    }
    // the loop variables: (the name, its type, the type of what it gets)
    var loopVars = new List<(Node Node, string Type, string Elem)>();
    if (overMap && mapPart == null) {
      var names = varNode.Children;
      if (names.Count != 2) {
        throw Error("a map gives two values per entry: for (k, v) in m", s);
      }
      if (names[0].Kind == "WritableVar") {
        throw Error("the keys of a map are read-only: (&) on the key is not allowed (spec 6.4); to change a key, remove the entry and add it again", s);
      }
      if (names[1].Kind == "WritableVar") {
        throw Error("for (k, (&)V v) in m is not built yet (writable values come with a later step)", s);
      }
      loopVars.Add((names[0], "", MapKey(collection.Type)));
      loopVars.Add((names[1], "", MapValue(collection.Type)));
    } else {
      loopVars.Add((varNode, "", elem));
    }
    var innerScope = new Scope(scope);
    for (int index = 0; index < loopVars.Count; index++) {
      var (node, _, itemType) = loopVars[index];
      string declared = itemType;
      if (node.Children.Count > 0 && !(node.Children[0] is { Kind: "Type", Text: "var" })) {
        declared = ResolveType(node.Children[0]);
        if (declared != itemType && !Assignable(declared, itemType)) {
          throw Error(overMap
            ? $"the 'for' variable '{node.Text}' is {declared}, but the {(index == 0 ? "keys" : "values")} are {itemType}"
            : $"the 'for' variable is {declared}, but the items are {itemType}", s);
        }
      }
      CheckNotShadowing(node.Text!, scope, s);
      if (overMap && index == 1 && node.Text == loopVars[0].Node.Text) {
        throw Error($"the key and the value cannot both be called '{node.Text}'", s);
      }
      innerScope.Vars[node.Text!] = new VarInfo(declared, "v_" + node.Text, false) { ReadOnlyString = declared == "string" };
      loopVars[index] = (node, declared, itemType);
    }
    string type = loopVars[0].Type;
    string cname = "v_" + loopVars[0].Node.Text;
    var body = PrepareFrame(LoopStatements(block.Children), innerScope, FrameKind.LoopBody);
    bool cleanup = body.AtEnds.Count > 0;

    tempCounter++;
    string whole = $"df_coll{tempCounter}";
    string position = $"df_pos{tempCounter}";
    string byteOffset = $"df_off{tempCounter}";
    string wholeType = CType(collection.Type);

    sb.Append($"{pad}{{\n");
    int inner = indent + 1;
    string innerPad = Pad(inner);
    if (cleanup) {
      EmitHoists(body, sb, innerPad);
    }
    if (customElem == null) {
      sb.Append($"{innerPad}{wholeType} {whole} = {(overVec || overView || overMap ? NullChecked(collection.Code, collection.Type) : collection.Code)};\n");
    }
    if (overVec || overMap) {
      sb.Append($"{innerPad}{counted.Counter} = &{whole}->{VecField(collection.Type, "iteratorCounter")};\n{innerPad}*{counted.Counter} += 1;\n");
    }
    string iteration = "";
    if (customElem != null) {
      // the iterator was asked for once, in the block around the loop; each pass calls next() and stops when isValid is false
      var iterator = ExprOf(Synthetic("Name", s, counted.Counter!), scope);
      var step = ObjectCall(iterator, "next", new List<Node>(), scope, s) ?? throw Error("no next()", s);
      string tuple = $"df_step{tempCounter}";
      iteration = $"({{ {CType(step.Type)} {tuple} = {step.Code}; if (!{tuple}.e1) {{ break; }} {tuple}.e0; }})";
    }
    string itemCode;
    string loop;
    string? keptText = null;
    if (collection.Type == "string") {
      Need("text", "");
      // a block string is read from a copy of its text, freed after the loop
      string text = NewTemp();
      keptText = NewTemp();
      sb.Append($"{innerPad}char* {keptText} = NULL;\n{innerPad}df_sv {text} = df_view_keep({whole}, &{keptText});\n");
      sb.Append($"{innerPad}uint64_t {byteOffset} = 0;\n");
      loop = $"for (uint64_t {position} = 0; {byteOffset} < {text}.len; {position}++)";
      itemCode = $"df_utf8_next({text}.p, {text}.len, &{byteOffset})";
    } else if (customElem != null) {
      loop = "for (;;)";
      itemCode = iteration;
    } else if (overMap) {
      // the entries in key order: the null key first, then the leaves (spec 6.8.4.1)
      NeedMapChunk(collection.Type);
      loop = EntryLoop(whole, collection.Type, position);
      itemCode = mapPart == "values" ? ReadSlot(collection.Type, CursorValue(whole, collection.Type, position)) : CursorKey(collection.Type, position);
    } else if (overView) {
      (loop, itemCode) = ViewLoop(collection.Type, whole, position, sb, innerPad);
    } else if (overList) {
      loop = $"for ({ListNode(collection.Type)}* {position} = {whole}->{VecField(collection.Type, "first")}; {position} != NULL; {position} = {position}->next)";
      itemCode = $"{position}->value";
    } else if (overVec) {
      loop = $"for (df_uns {position} = 0; {position} < {whole}->{VecField(collection.Type, "count")}; {position}++)";
      itemCode = $"{whole}->{VecField(collection.Type, "items")}.p[{position}]";
    } else {
      loop = $"for (df_uns {position} = 0; {position} < {whole}.len; {position}++)";
      itemCode = $"{whole}.p[{position}]";
    }
    sb.Append($"{innerPad}{loop} {{\n");
    if (cleanup) {
      sb.Append($"{innerPad}  {body.RanFlag} = true;\n");
    }
    sb.Append($"{innerPad}  {CType(type)} {cname} = {Convert(new Expr(itemCode, elem), type, s)};\n");
    if (overMap && mapPart == null) {
      var (valueNode, valueType, valueElem) = loopVars[1];
      sb.Append($"{innerPad}  {CType(valueType)} v_{valueNode.Text} = {Convert(new Expr(ReadSlot(collection.Type, CursorValue(whole, collection.Type, position)), valueElem), valueType, s)};\n");
    }
    var before = assign.Clone();
    loopDepth++;
    EmitFrameBody(body, sb, inner + 1);
    loopDepth--;
    assign = before;
    sb.Append($"{innerPad}}}\n");
    if (cleanup) {
      EmitLoopTail(body, sb, inner);
    }
    if (keptText != null) {
      sb.Append($"{innerPad}free({keptText});\n");
    }
    if (collection.Type == "string" && collection.Fresh) {
      sb.Append($"{innerPad}df_str_free({whole});\n");   // the new string the loop went through (a return out of the loop leaves it)
    }
    sb.Append($"{pad}}}\n");
  }
}
