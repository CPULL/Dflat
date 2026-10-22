using System.Text;
using dfparser;

namespace dfcompiler;

// Lists list<T> (spec 6.8.3): a singly linked list. Like vec, the class `list` is written in Dflat (CodeGen.Vec.cs, VecPrelude) and the hot paths are C emitted by
// the compiler: c[i] and c[i] = v walk the links from the first node (O(n)), the for loop walks them once, the literal, sort and the drop of the list are C.
// The nodes are the slots of two arrays (items and links); the list keeps the count, the first and the last node.
public sealed partial class CodeGen {
  const string ListEnd = "UINT64_MAX";   // the link of the last node, and of an empty list

  bool IsList(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "list";

  string ListElem(string type) => classInstanceArgs[type].Args[0];

  // c[i]: O(n), the index is compared with the count first (spec 6.1)
  Expr ListIndex(Expr obj, Expr index, Node n) {
    string elem = ListElem(obj.Type);
    string items = VecField(obj.Type, "items");
    string links = VecField(obj.Type, "links");
    string first = VecField(obj.Type, "first");
    string count = VecField(obj.Type, "count");
    string result = NewTemp();
    string node = NewTemp();
    string step = NewTemp();
    string code = WithTemp(new Expr(NullChecked(obj.Code, obj.Type), obj.Type), list => WithTemp(new Expr(IndexValue(index, n), "u64"), position =>
      CheckRaise($"({{ {CType(elem)} {result} = {ZeroValue(elem)}; if (df_idx_ok({list}->{count}, {position}, {fn!.NameConst})) {{ uint64_t {node} = {list}->{first}; "
        + $"for (uint64_t {step} = 0; {step} < {position}; {step}++) {{ {node} = {list}->{links}.p[{node}]; }} {result} = {list}->{items}.p[{node}]; }} {result}; }})", elem)));
    return new Expr(code, elem);
  }

  // c[i] = v, c[i] += v
  string ListIndexAssign(Node s, Scope scope, Expr obj, Node target) {
    string elem = ListElem(obj.Type);
    string items = VecField(obj.Type, "items");
    string links = VecField(obj.Type, "links");
    string first = VecField(obj.Type, "first");
    string count = VecField(obj.Type, "count");
    string version = VecField(obj.Type, "version");
    var index = ExprOf(target.Children[1], scope, "u64");
    string list = NewTemp();
    string position = NewTemp();
    string node = NewTemp();
    string step = NewTemp();
    string slot = $"{list}->{items}.p[{node}]";
    string op = s.Text!;
    if (op == "_=") {
      throw NotSupported("Ownership", s);
    }
    if (op == "??=") {
      throw Error("'??=' on a list item is not supported yet", s);
    }
    string walk = $"uint64_t {node} = {list}->{first}; for (uint64_t {step} = 0; {step} < {position}; {step}++) {{ {node} = {list}->{links}.p[{node}]; }} ";
    string head = $"({{ {CType(obj.Type)} {list} = {NullChecked(obj.Code, obj.Type)}; uint64_t {position} = {IndexValue(index, target)}; ";
    string check = $"if (!df_idx_ok({list}->{count}, {position}, {fn!.NameConst})) {RaiseJump} ";
    if (op == "=") {
      var assigned = ExprOf(s.Children[1], scope, elem);
      if (IsClass(elem) && !assigned.Fresh && assigned.Type != "null" && s.Line < LibraryLine) {
        throw Error("a collection owns its items: move an existing object in with ->x, or put a new one", s);
      }
      string value = Convert(assigned, elem, s);
      if (IsClass(elem)) {
        string incoming = NewTemp();
        string replaced = NewTemp();
        // the collection owns its items: the item that is replaced ends (unless it is the same object)
        return head + $"{CType(elem)} {incoming} = {value}; " + check + walk + $"{CType(elem)} {replaced} = {slot}; {slot} = {incoming}; {list}->{version} += 1; "
          + $"if ({replaced} != {incoming}) {{ df_drop((void*){replaced}); }} }})";
      }
      return head + check + walk + $"{slot} = {value}; {list}->{version} += 1; }})";
    }
    string combined = Convert(BinaryOp(op[..^1], new Expr(slot, elem), s.Children[1], scope, s), elem, s);
    return head + check + walk + $"{slot} = {combined}; {list}->{version} += 1; }})";
  }

  // an empty list with room for the items of a literal
  Expr ListLiteral(Node literal, Scope scope, string type) {
    string elem = ListElem(type);
    string items = VecField(type, "items");
    string links = VecField(type, "links");
    string held = NewTemp();
    int count = literal.Children.Count;
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {VecNewEmpty(type)}; ");
    if (count > 0) {
      sb.Append($"{held}->{items} = ({ArrCType(elem)}){{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; ");
      sb.Append($"{held}->{links} = ({ArrCType("u64")}){{ (uint64_t*)df_calloc({count}, sizeof(uint64_t)), {count} }}; ");
    }
    for (int index = 0; index < count; index++) {
      var item = literal.Children[index];
      var value = ExprOf(item, scope, elem);
      if (IsClass(elem) && !value.Fresh && value.Type != "null") {
        throw Error("a list owns its items: put a new object in the literal, or move one in with ->x", item);
      }
      sb.Append($"{held}->{items}.p[{index}] = {Convert(value, elem, item)}; {held}->{links}.p[{index}] = {(index == count - 1 ? ListEnd : (index + 1).ToString())}; ");
    }
    if (count > 0) {
      sb.Append($"{held}->{VecField(type, "first")} = 0; {held}->{VecField(type, "last")} = {count - 1}; {held}->{VecField(type, "used")} = {count}; ");
    }
    sb.Append($"{held}->{VecField(type, "count")} = {count}; {held}; }})");
    return new Expr(sb.ToString(), type, true);
  }

  // the type of a bare 'list' declared with a literal: list values = [1, 2, 3]
  string InferListType(Node literal, Scope scope, Node at) {
    if (literal.Children.Count == 0) {
      throw Error("an empty literal needs a declared type: list<T>", literal);
    }
    var values = literal.Children.Select(item => ExprOf(item, scope)).ToList();
    return ClassInstanceName(genericClasses["list"], new List<string> { CommonType(values, literal) }, at);
  }

  // C function that prints a list as [a, b, c]
  string ListStrFunction(string listType, Node at) {
    string name = "df_list_str_" + System.Text.RegularExpressions.Regex.Replace(listType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      string elem = ListElem(listType);
      string item = StrOf(new Expr($"v->{VecField(listType, "items")}.p[node]", elem), at);
      arrayCode.Append($"static df_string {name}({CType(listType)} v) {{\n");
      arrayCode.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      arrayCode.Append("  df_string r = df_str_lit(\"[\", 1);\n  bool firstItem = true;\n");
      arrayCode.Append($"  for (uint64_t node = v->{VecField(listType, "first")}; node != {ListEnd}; node = v->{VecField(listType, "links")}.p[node]) {{\n");
      arrayCode.Append("    if (!firstItem) {\n      r = df_str_concat(r, df_str_lit(\", \", 2));\n    }\n    firstItem = false;\n");
      arrayCode.Append($"    r = df_str_concat(r, {item});\n  }}\n");
      arrayCode.Append("  return df_str_concat(r, df_str_lit(\"]\", 1));\n}\n\n");
    }
    return name;
  }

  // the end of a list: the items it owns end (a loop, so a long list does not recurse), then its two blocks of memory are freed
  void EmitListDropGlue(StringBuilder sb, ClassInfo cls) {
    string items = VecField(cls.Name, "items");
    string links = VecField(cls.Name, "links");
    string first = VecField(cls.Name, "first");
    if (IsClass(ListElem(cls.Name))) {
      sb.Append($"  for (uint64_t node = self->{first}; node != {ListEnd}; node = self->{links}.p[node]) {{ if (self->{items}.p[node] != NULL) {{ void* o = (void*)self->{items}.p[node]; self->{items}.p[node] = NULL; df_drop(o); }} }}\n");
    }
    sb.Append($"  free(self->{items}.p);\n  self->{items}.p = NULL;\n  self->{items}.len = 0;\n  free(self->{links}.p);\n  self->{links}.p = NULL;\n  self->{links}.len = 0;\n");
    sb.Append($"  self->{first} = {ListEnd};\n  self->{VecField(cls.Name, "count")} = 0;\n");
  }

  // the members of a list that are not plain calls of the class
  Expr? ListMember(Expr list, string name, List<Node> args, Scope scope, Node at) {
    string elem = ListElem(list.Type);
    if (name == "fill" && IsClass(elem) && args.Count > 0 && args[^1].Kind != "Null") {
      throw Error("fill would put one object in every item: a class instance is not allowed, fill(null) is", at);
    }
    if (name is "sort" or "sortUnstable") {
      // the items go to a temporary array, the arrays' sort orders them, and they go back in the same nodes
      string held = NewTemp();
      string temp = NewTemp();
      string node = NewTemp();
      string position = NewTemp();
      string items = VecField(list.Type, "items");
      string links = VecField(list.Type, "links");
      string first = VecField(list.Type, "first");
      string arrType = "arr<" + elem + ">";
      var sorted = ArrayMethod(new Expr(temp, arrType), name, args, scope, at);
      if (sorted == null) {
        return null;
      }
      string count = $"{held}->{VecField(list.Type, "count")}";
      string code = $"({{ {CType(list.Type)} {held} = {NullChecked(list.Code, list.Type)}; {ArrCType(elem)} {temp} = {{ ({CType(elem)}*)df_calloc({count}, sizeof({CType(elem)})), {count} }}; "
        + $"uint64_t {position} = 0; for (uint64_t {node} = {held}->{first}; {node} != {ListEnd}; {node} = {held}->{links}.p[{node}]) {{ {temp}.p[{position}++] = {held}->{items}.p[{node}]; }} "
        + $"{sorted.Value.Code}; {position} = 0; for (uint64_t {node} = {held}->{first}; {node} != {ListEnd}; {node} = {held}->{links}.p[{node}]) {{ {held}->{items}.p[{node}] = {temp}.p[{position}++]; }} "
        + $"{held}->{VecField(list.Type, "version")} += 1; free({temp}.p); }})";
      return new Expr(code, "void");
    }
    if (name == "swap") {
      if (args.Count != 2 || args.Any(arg => arg.Kind == "Named")) {
        throw Error("swap takes two positions, or a position and a new item", at);
      }
      // the same rule as the arrays: with two integer arguments the call is the exchange
      var second = ExprOf(args[1], scope);
      bool exchange = !IsClass(elem) && args[1].Kind != "Gift" && IsInt(second.Type) && (IsInt(elem) || !Assignable(elem, second.Type));
      var call = ObjectCall(list, exchange ? "exchange" : "replace", args, scope, at) ?? throw Error("no swap", at);
      return exchange ? call : new Expr(call.Code, call.Type, IsClass(elem));
    }
    return null;
  }
}
