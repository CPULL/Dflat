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
      typeDefs.Append($"typedef struct {{\n  {elemC}* p;\n  uint64_t len;\n}} {name};\n\n");
    }
    return name;
  }

  // C function that prints an array as [a, b, c]
  string ArrStrFunction(string elem, Node at) {
    string name = "df_arr_str_" + Regex.Replace(Mangle(elem), @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      string item = StrOf(new Expr("a.p[i]", elem), at);
      arrayCode.Append($"static df_string {name}({ArrCType(elem)} a) {{\n");
      arrayCode.Append("  df_string r = df_str_lit(\"[\", 1);\n");
      arrayCode.Append("  for (uint64_t i = 0; i < a.len; i++) {\n");
      arrayCode.Append("    if (i > 0) {\n      r = df_str_concat(r, df_str_lit(\", \", 2));\n    }\n");
      arrayCode.Append($"    r = df_str_concat(r, {item});\n  }}\n");
      arrayCode.Append("  return df_str_concat(r, df_str_lit(\"]\", 1));\n}\n\n");
    }
    return name;
  }

  // a C expression for an index of any integer type; a negative value becomes huge and fails the bounds check
  string IndexValue(Expr index, Node at) {
    if (!IsInt(index.Type)) {
      throw Error($"an index must be an integer, not {index.Type}", at);
    }
    return SignedTypes.Contains(index.Type) ? $"((uint64_t)(int64_t)({index.Code}))" : $"((uint64_t)({index.Code}))";
  }

  string RaiseJump => $"{{ df_err = df_pending; df_pending = NULL; df_err_thrown = false; goto {Handler}; }}";

  // arr<T>[size]: elements start as zero
  Expr ArrayNew(Node generic, Node sizeNode, Scope scope) {
    string target = generic.Children[0].Text!;
    if (target == "vec" && vecPrelude) {
      return VecNewWithCapacity(generic, sizeNode, scope);
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
    string code = $"({{ uint64_t {count} = {IndexValue(size, sizeNode)}; ({CType(type)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; }})";
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
      sb.Append($"{array}.p[{index}] = {Convert(ExprOf(item, scope, elem), elem, item)}; ");
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
    var obj = ExprOf(n.Children[0], scope);
    var index = ExprOf(n.Children[1], scope, "u64");
    if (obj.Type == "string") {
      string call = $"df_str_char_at({obj.Code}, {IndexValue(index, n)}, {fn!.NameConst})";
      return new Expr(CheckRaise(Need("text", call), "char"), "char");
    }
    if (IsVec(obj.Type)) {
      return VecIndex(obj, index, n);
    }
    if (!IsArr(obj.Type)) {
      throw NotSupported("Collections", n);
    }
    string elem = ArrElem(obj.Type);
    string code = WithTemp(obj, array => WithTemp(new Expr(IndexValue(index, n), "u64"), position =>
      CheckRaise($"(df_idx_ok({array}.len, {position}, {fn!.NameConst}) ? {array}.p[{position}] : {ZeroValue(elem)})", elem)));
    return new Expr(code, elem);
  }

  // a[i] = v, a[i] += v ...
  string IndexAssign(Node s, Scope scope) {
    var target = s.Children[0];
    var obj = ExprOf(target.Children[0], scope);
    if (obj.Type == "string") {
      throw Error("writing a character by index is not decided yet (spec 4)", s);
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
      value = Convert(ExprOf(s.Children[1], scope, elem), elem, s);
    } else {
      value = Convert(BinaryOp(op[..^1], new Expr(slot, elem), s.Children[1], scope, s), elem, s);
    }
    if (isVec && op == "=" && IsClass(elem)) {
      // the vector owns its items: the one that is replaced ends (unless it is the same object)
      string replaced = NewTemp();
      string incoming = NewTemp();
      return $"({{ {CType(obj.Type)} {array} = {holder}; uint64_t {position} = {IndexValue(index, target)}; {CType(elem)} {incoming} = {value}; "
        + $"if (!df_idx_ok({length}, {position}, {fn!.NameConst})) {RaiseJump} {CType(elem)} {replaced} = {slot}; {slot} = {incoming}; "
        + $"if ({replaced} != {incoming}) {{ df_drop((void*){replaced}); }} }})";
    }
    return $"({{ {CType(obj.Type)} {array} = {holder}; uint64_t {position} = {IndexValue(index, target)}; "
      + $"if (!df_idx_ok({length}, {position}, {fn!.NameConst})) {RaiseJump} {slot} = {value}; }})";
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
            ? $"({{ bool {found} = false; for (uint64_t {i} = 0; {i} < {a}.len; {i}++) {{ if ({same}) {{ {found} = true; break; }} }} {found}; }})"
            : $"({{ uint64_t {found} = UINT64_MAX; for (uint64_t {i} = 0; {i} < {a}.len; {i}++) {{ if ({same}) {{ {found} = {i}; break; }} }} {found}; }})";
        }));
        return new Expr(code, name == "has" ? "bool" : "u64");
      }
      case "fill": {
        Arity(1);
        var value = Value(args[0]);
        string i = NewTemp();
        string code = WithTemp(array, a => WithTemp(value, v =>
          $"({{ for (uint64_t {i} = 0; {i} < {a}.len; {i}++) {{ {Elem(a, i)} = {v}; }} }})"));
        return new Expr(code, "void");
      }
      case "clear": {
        Arity(0);
        string i = NewTemp();
        string code = WithTemp(array, a =>
          $"({{ for (uint64_t {i} = 0; {i} < {a}.len; {i}++) {{ {Elem(a, i)} = {ZeroValue(elem)}; }} }})");
        return new Expr(code, "void");
      }
      case "reverse": {
        Arity(0);
        string i = NewTemp();
        string swap = NewTemp();
        string code = WithTemp(array, a =>
          $"({{ for (uint64_t {i} = 0; {i} < {a}.len / 2; {i}++) {{ {CType(elem)} {swap} = {Elem(a, i)}; {Elem(a, i)} = {a}.p[{a}.len - 1 - {i}]; {a}.p[{a}.len - 1 - {i}] = {swap}; }} }})");
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
        string descending = "false";
        if (args.Count == 1) {
          if (args[0].Kind is "Lambda" or "Fun" or "Closure") {
            throw NotSupported("Lambdas", args[0]);
          }
          var direction = ExprOf(args[0], scope);
          if (direction.Type != "SortDirection") {
            throw Error($"{name} takes SortDirection.ASC or SortDirection.DESC, not {direction.Type}", args[0]);
          }
          descending = $"(({direction.Code}) == {enums["SortDirection"].Find("DESC")})";
        }
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
      default:
        return null;
    }
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
      return SignedTypes.Contains(value.Type) ? $"df_idx_arg((int64_t)({value.Code}))" : $"((uint64_t)({value.Code}))";
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
        count = $"({source0}.len < ({srcIndex}) ? UINT64_MAX : {source0}.len - ({srcIndex}))";
      } else {
        string end = Index(range.Children[1]);
        string last = range.Text == "..=" ? $"({end}) + 1" : $"({end})";
        count = $"(({last}) < ({srcIndex}) ? UINT64_MAX : ({last}) - ({srcIndex}))";
      }
    }
    string d = NewTemp();
    string di = NewTemp();
    string si = NewTemp();
    string n = NewTemp();
    string code = $"({{ {CType(destination.Type)} {d} = {destination.Code}; {CType(source.Type)} {source0} = {source.Code}; "
      + $"uint64_t {di} = {dstIndex}; uint64_t {si} = {srcIndex}; uint64_t {n} = {count}; "
      + $"if ({d}.p != NULL && {d}.p == {source0}.p) {{ df_raise(&df_desc_ModifyingProtectedSourceException, {fn!.NameConst}); {RaiseJump} }} "
      + $"if ({di} > {d}.len || {n} > {d}.len - {di} || {si} > {source0}.len || {n} > {source0}.len - {si}) {{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }} "
      + $"if ({n} > 0) {{ memcpy({d}.p + {di}, {source0}.p + {si}, {n} * sizeof(*{d}.p)); }} }})";
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
      var target = LValue(arg, scope);
      if (target.IsConst) {
        throw Error($"'{arg.Text}' is const", at);
      }
      MarkAssigned(target.Variable);
      return new Expr(Need("text", $"({target.Code} = df_str_shuffle({target.Code}))"), "void");
    }
    throw Error($"shuffle works on arrays and strings, not {value.Type}", at);
  }

  // for T item in collection { }: arrays by position, strings by character
  void EmitForEach(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var varNode = s.Children[0];
    var source = s.Children[1].Children[0];
    var block = s.Children.Last();
    var collection = ExprOf(source, scope);
    string elem;
    bool overVec = IsVec(collection.Type);
    string? customElem = overVec ? null : CustomCollectionElem(collection.Type);
    if (overVec) {
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
    string type = elem;
    if (varNode.Children.Count > 0 && !(varNode.Children[0] is { Kind: "Type", Text: "var" })) {
      type = ResolveType(varNode.Children[0]);
      if (type != elem && !Assignable(type, elem)) {
        throw Error($"the 'for' variable is {type}, but the items are {elem}", s);
      }
    }
    CheckNotShadowing(varNode.Text!, scope, s);

    var innerScope = new Scope(scope);
    string cname = "v_" + varNode.Text;
    innerScope.Vars[varNode.Text!] = new VarInfo(type, cname, false);
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
    sb.Append($"{innerPad}{wholeType} {whole} = {(overVec ? NullChecked(collection.Code, collection.Type) : collection.Code)};\n");
    string iteration = "";
    if (customElem != null) {
      // the iterator is asked for once; each pass calls next() and stops when isValid is false
      var iterator = ObjectCall(new Expr(whole, collection.Type), "getIterator", new List<Node>(), scope, s) ?? throw Error("no iterator", s);
      string iterVar = $"df_iter{tempCounter}";
      sb.Append($"{innerPad}{CType(iterator.Type)} {iterVar} = {iterator.Code};\n");
      var step = ObjectCall(new Expr(iterVar, iterator.Type), "next", new List<Node>(), scope, s) ?? throw Error("no next()", s);
      string tuple = $"df_step{tempCounter}";
      iteration = $"({{ {CType(step.Type)} {tuple} = {step.Code}; if (!{tuple}.e1) {{ break; }} {tuple}.e0; }})";
    }
    string itemCode;
    string loop;
    if (collection.Type == "string") {
      Need("text", "");
      sb.Append($"{innerPad}uint64_t {byteOffset} = 0;\n");
      loop = $"for (uint64_t {position} = 0; {byteOffset} < {whole}.len; {position}++)";
      itemCode = $"df_utf8_next({whole}.p, {whole}.len, &{byteOffset})";
    } else if (customElem != null) {
      loop = "for (;;)";
      itemCode = iteration;
    } else if (overVec) {
      loop = $"for (uint64_t {position} = 0; {position} < {whole}->{VecField(collection.Type, "count")}; {position}++)";
      itemCode = $"{whole}->{VecField(collection.Type, "items")}.p[{position}]";
    } else {
      loop = $"for (uint64_t {position} = 0; {position} < {whole}.len; {position}++)";
      itemCode = $"{whole}.p[{position}]";
    }
    sb.Append($"{innerPad}{loop} {{\n");
    if (cleanup) {
      sb.Append($"{innerPad}  {body.RanFlag} = true;\n");
    }
    sb.Append($"{innerPad}  {CType(type)} {cname} = {Convert(new Expr(itemCode, elem), type, s)};\n");
    var before = assign.Clone();
    loopDepth++;
    EmitFrameBody(body, sb, inner + 1);
    loopDepth--;
    assign = before;
    sb.Append($"{innerPad}}}\n");
    if (cleanup) {
      EmitLoopTail(body, sb, inner);
    }
    sb.Append($"{pad}}}\n");
  }
}
