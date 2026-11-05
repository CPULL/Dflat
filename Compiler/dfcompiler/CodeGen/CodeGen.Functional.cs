using System.Text;
using dfparser;

namespace dfcompiler;

// sort(lambda) and the functional members filter, map, reduce of arr, vec and list (spec 6.6, 13.3).
//
// sort(lambda): the comparer of the runtime sort (df_sort, df_list_sort) is a C function df_cmpfn_<T> that calls the lambda kept in
// df_sort_fn; the call saves and restores df_sort_fn, so a sort inside a comparer works. The lambda returns an int, only its sign counts.
//
// filter(bool(T)), map<U>(U(T)) and reduce(A init, A(A, T)) give new values and leave the collection as it is: filter and map make a
// collection of the same kind (arr, vec or list). The items kept by filter are copied (numbers, bool, char, dates, enums, strings); map
// takes the new values its function returns (any type the function gives: numbers, strings, objects). reduce carries a number, a bool,
// a char, a date or a string.
public sealed partial class CodeGen {
  readonly StringBuilder lambdaHelpers = new();
  readonly HashSet<string> sortComparers = new();

  static readonly HashSet<string> FunctionalMembers = new() { "filter", "map", "reduce" };

  // the argument of sort when it is a function (a lambda or a function value); null when it is a SortDirection or nothing
  (string Compare, string Before, string After)? SortByFunction(string elem, List<Node> args, Scope scope) {
    if (args.Count != 1) {
      return null;
    }
    var arg = args[0];
    bool isFunction = arg.Kind == "Lambda" || arg.Kind == "Name" && scope.Find(arg.Text!) is VarInfo held && IsFunType(held.Type);
    if (!isFunction) {
      return null;
    }
    string fnType = MakeFunType(Normalize("int"), new[] { elem, elem });
    string value = FnArg(new ParamSig("comparer", fnType, null), arg, scope);
    string compare = SortComparer(elem);
    string saved = NewTemp();
    string savedErr = NewTemp();
    string given = NewTemp();
    string got = NewTemp();
    string before = $"df_fn {given} = {value}; if (!{given}.code) {{ df_raise(&df_desc_UnassignedFunctionCallException, {fn!.NameConst}); {RaiseJump} }} "
      + $"df_fn {saved} = df_sort_fn; df_sort_fn = {given}; df_exception* {savedErr} = df_sort_err; df_sort_err = NULL; ";
    // the exception the lambda let out is raised after the sort (a silent one is ignored unless a catch here handles it, spec 15.4)
    string after = $"df_sort_fn = {saved}; df_exception* {got} = df_sort_err; df_sort_err = {savedErr}; "
      + $"if ({got}) {{ if ({SilentIgnored(got)}) {{ df_exc_release({got}); }} else {{ df_err = {got}; df_err_thrown = false; goto {Handler}; }} }} ";
    return (compare, before, after);
  }

  // the comparer of the runtime sort that calls the lambda of sort(lambda) for items of type elem
  string SortComparer(string elem) {
    string name = "df_cmpfn_" + System.Text.RegularExpressions.Regex.Replace(Mangle(elem), @"[^A-Za-z0-9_]", "_");
    if (sortComparers.Add(name)) {
      string item = CType(elem);
      string pair = ResultPair(CType(Normalize("int")));
      // after an exception the comparisons answer 0: the sort finishes, and the exception is raised after it
      lambdaHelpers.Append($"static int {name}(const void* a, const void* b) {{\n  if (df_sort_err) {{\n    return 0;\n  }}\n"
        + $"  {pair} r = (({pair} (*)(void*, {item}, {item}))df_sort_fn.code)((void*)df_sort_fn.ctx, *({item}*)a, *({item}*)b);\n"
        + "  if (r.err) {\n    df_sort_err = r.err;\n    return 0;\n  }\n"
        + "  return r.value < 0 ? -1 : r.value > 0 ? 1 : 0;\n}\n\n");
    }
    return name;
  }

  // c.filter(f), c.reduce(init, f) and c.map<U>(f) (typeArg: the node of U, null when it is not written)
  Expr FunctionalCall(Expr source, string name, List<Node> args, Node? typeArg, Scope scope, Node at) {
    string elem = IndexCollectionElem(source.Type) ?? throw Error($"{name} works on arr, vec and list, not {source.Type}", at);
    if (args.Any(arg => arg.Kind == "Named")) {
      throw Error($"{name} takes its arguments by position", at);
    }
    return name switch {
      "filter" => Filter(source, elem, args, scope, at),
      "map" => MapItems(source, elem, args, typeArg, scope, at),
      _ => Reduce(source, elem, args, scope, at)
    };
  }

  // the function argument of a functional member: a df_fn kept in a temporary, checked against null
  (string Setup, string Call) FunctionArg(Node arg, string fnType, Scope scope) {
    string value = FnArg(new ParamSig("function", fnType, null), arg, scope);
    string held = NewTemp();
    string setup = $"df_fn {held} = {value}; if (!{held}.code) {{ df_raise(&df_desc_UnassignedFunctionCallException, {fn!.NameConst}); {RaiseJump} }} ";
    return (setup, held);
  }

  // the end of a source collection that is new ([1, 2].filter(...) on a result): it is not kept by anyone
  string FreshSourceEnd(Expr source, string s) =>
    !source.Fresh ? "" : IsArr(source.Type) ? $"{ArrDropHelper(source.Type)}({s}); " : $"df_drop((void*){s}); ";

  // the end of one item of a block that is being filled (an exception stops the filling)
  string ItemEnd(string elem, string item) =>
    IsStrItem(elem) ? StrItemFree(elem, item) : IsClass(elem) ? $"if ({item} != NULL) {{ df_drop((void*){item}); }} " : "";

  // the copy of an item kept by filter
  string CopiedItem(string elem, string item, Node at) {
    if (IsPlainValueType(elem)) {
      return item;
    }
    if (IsStrItem(elem)) {
      return StrItemDup(elem, item);
    }
    throw NotSupported($"filter on items of type {elem} (objects and arrays need a copy, spec 6.6)", at);
  }

  // a new collection of the kind of the source with items of type elem, filled by the C 'fill' (which calls add(value) for each item); the
  // second argument of fill is the C that ends what was built so far, for an exception of the function
  string NewCollection(string sourceType, string elem, string length, Func<Func<string, string>, string, string> fill, Node at, out string type) {
    string result = NewTemp();
    var sb = new StringBuilder();
    if (IsList(sourceType)) {
      type = ClassInstanceName(genericClasses["list"], new List<string> { elem }, at);
      string listType = type;
      sb.Append($"{CType(type)} {result} = {VecNewEmpty(type)}; ");
      sb.Append(fill(value => {
        string node = NewTemp();
        return NewListNode(listType, node, value) + AppendListNode(result, listType, node);
      }, $"df_drop((void*){result}); "));
      sb.Append($"{result}; ");
      return sb.ToString();
    }
    string elemType = CType(elem);
    string block = NewTemp();
    string count = NewTemp();
    string k = NewTemp();
    bool itemsEnd = IsStrItem(elem) || IsClass(elem);
    string partial = (itemsEnd ? $"for (df_uns {k} = 0; {k} < {count}; {k}++) {{ {ItemEnd(elem, $"{block}[{k}]")}}} " : "") + $"free({block}); ";
    sb.Append($"{elemType}* {block} = ({elemType}*)df_calloc({length} > 0 ? {length} : 1, sizeof({elemType})); df_uns {count} = 0; ");
    sb.Append(fill(value => $"{block}[{count}++] = {value}; ", partial));
    if (IsArr(sourceType)) {
      type = "arr<" + elem + ">";
      sb.Append($"{ArrCType(elem)} {result} = {{ {block}, {count} }}; {result}; ");
    } else {
      type = ClassInstanceName(genericClasses["vec"], new List<string> { elem }, at);
      sb.Append($"{CType(type)} {result} = {VecNewEmpty(type)}; free({result}->{VecField(type, "items")}.p); "
        + $"{result}->{VecField(type, "items")} = ({ArrCType(elem)}){{ {block}, {length} > 0 ? {length} : 1 }}; {result}->{VecField(type, "count")} = {count}; {result}; ");
    }
    return sb.ToString();
  }

  Expr Filter(Expr source, string elem, List<Node> args, Scope scope, Node at) {
    if (args.Count != 1) {
      throw Error("filter takes one function: filter((T item) { condition })", at);
    }
    var (setup, held) = FunctionArg(args[0], MakeFunType("bool", new[] { elem }), scope);
    string s = NewTemp();
    string length = SourceLength(source.Type, s);
    string body = NewCollection(source.Type, elem, length, (add, partial) =>
      ForEachSourceItem(source.Type, s, "0", length, item =>
        $"if ({CheckedLambdaCall(held, "bool", new[] { CType(elem) }, new[] { item }, partial + FreshSourceEnd(source, s))}) {{ {add(CopiedItem(elem, item, at))}}} "), at, out string type);
    return new Expr(EndFreshSource(source, s, $"{CType(source.Type)} {s} = {SourceValue(source)}; {setup}", body, type), type, true);
  }

  Expr MapItems(Expr source, string elem, List<Node> args, Node? typeArg, Scope scope, Node at) {
    if (args.Count != 1) {
      throw Error("map takes one function: map<U>((T item) { new value })", at);
    }
    string? target = typeArg != null ? ResolveType(typeArg)
      : args[0].Kind == "Name" && scope.Find(args[0].Text!) is VarInfo held0 && IsFunType(held0.Type) ? FunParts(held0.Type).Ret
      : null;
    if (target == null) {
      throw Error("write the type of the new items: map<string>((i32 x) { x.toStr() }) (spec 6.6)", at);
    }
    if (target == "void" || IsArr(target) || IsFunType(target) || IsNullable(target) && target != "string?" || IsTuple(target) || IsCountedType(target)) {
      throw NotSupported($"map to items of type {target} (spec 6.6)", at);
    }
    var (setup, held) = FunctionArg(args[0], MakeFunType(target, new[] { elem }), scope);
    string s = NewTemp();
    string length = SourceLength(source.Type, s);
    string body = NewCollection(source.Type, target, length, (add, partial) =>
      ForEachSourceItem(source.Type, s, "0", length, item =>
        add(CheckedLambdaCall(held, target, new[] { CType(elem) }, new[] { item }, partial + FreshSourceEnd(source, s)))), at, out string type);
    return new Expr(EndFreshSource(source, s, $"{CType(source.Type)} {s} = {SourceValue(source)}; {setup}", body, type), type, true);
  }

  Expr Reduce(Expr source, string elem, List<Node> args, Scope scope, Node at) {
    if (args.Count != 2) {
      throw Error("reduce takes a start value and a function: reduce(0, (i32 total, i32 item) { total + item })", at);
    }
    string? acc = args[1].Kind == "Lambda" && args[1].Children.First(child => child.Kind == "Params") is { Children.Count: > 0 } ps && ps.Children[0].Children.Count > 0
      ? ResolveType(ps.Children[0].Children[0])
      : args[1].Kind == "Name" && scope.Find(args[1].Text!) is VarInfo held0 && IsFunType(held0.Type) ? FunParts(held0.Type).Ret
      : null;
    if (acc == null) {
      throw Error("reduce takes a lambda that names the type of the total: reduce(0, (i32 total, i32 item) { total + item }) (spec 6.6)", at);
    }
    if (!IsPlainValueType(acc) && acc != "string") {
      throw NotSupported($"reduce with a total of type {acc} (spec 6.6)", at);
    }
    var start = ExprOf(args[0], scope, acc);
    string startCode = Convert(start, acc, args[0]);
    if (acc == "string") {
      startCode = StrOwned(start, startCode);   // the total is a string of its own
    }
    var (setup, held) = FunctionArg(args[1], MakeFunType(acc, new[] { acc, elem }), scope);
    string s = NewTemp();
    string total = NewTemp();
    string next = NewTemp();
    string accC = CType(acc);
    string length = SourceLength(source.Type, s);
    string loop = ForEachSourceItem(source.Type, s, "0", length, item => {
      string call = CheckedLambdaCall(held, acc, new[] { accC, CType(elem) }, new[] { total, item },
        (acc == "string" ? $"df_str_free({total}); " : "") + FreshSourceEnd(source, s));   // an exception: the total ends
      return acc == "string"
        ? $"{accC} {next} = {call}; df_str_free({total}); {total} = {next}; "   // the old total ends
        : $"{total} = {call}; ";
    });
    string code = EndFreshSource(source, s, $"{CType(source.Type)} {s} = {SourceValue(source)}; {setup}{accC} {total} = {startCode}; ", $"{loop}{total}; ", acc);
    return new Expr(code, acc, acc == "string");
  }

  // ({ head body }) where body ends with the value; a new collection the member was called on ([1, 2].filter(...)) ends after it
  string EndFreshSource(Expr source, string s, string head, string body, string type) {
    if (!source.Fresh) {
      return $"({{ {head}{body}}})";
    }
    string result = NewTemp();
    string end = FreshSourceEnd(source, s);
    int last = body.TrimEnd().LastIndexOf(';', body.TrimEnd().Length - 2);
    string value = body.TrimEnd()[(last + 1)..^1].Trim();
    string before = body.TrimEnd()[..(last + 1)];
    return $"({{ {head}{before} {CType(type)} {result} = {value}; {end}{result}; }})";
  }

  // the collection a functional member reads: a vec or a list is checked against null
  string SourceValue(Expr source) => IsArr(source.Type) ? source.Code : NullChecked(source.Code, source.Type);
}
