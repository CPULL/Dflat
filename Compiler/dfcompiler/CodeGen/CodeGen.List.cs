using System.Text;
using dfparser;

namespace dfcompiler;

// Lists list<T> (spec 6.8.3): a singly linked list. The class `list` is written in Dflat (CodeGen.Vec.cs, VecPrelude): its methods check the rules
// (the index, the counter of open iterators) and call the node_ members, which are C emitted here. Every node is allocated on its own and holds the
// pointer to the next node and the item (struct df_ln_...); the list keeps the count, the first and the last node, and a null next ends the list.
// The fields first and last are declared u64 in Dflat and are node pointers in the C struct (EmitStruct); only the C of this file uses them.
// c[i] and c[i] = v walk the nodes from the first one (O(n)), the for loop walks them once; the literal, sort, add(collection), removeAt(range),
// copyFrom, transformTo, the text and the drop of the list are C too.
public sealed partial class CodeGen {
  readonly StringBuilder listNodeTypes = new();   // the node structs, after the types of the program (the item can be any of them)
  readonly HashSet<string> listNodeNames = new();

  bool IsList(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "list";

  string ListElem(string type) => classInstanceArgs[type].Args[0];

  // the C type of the nodes of a list type: the link to the next node, then the item (natural size: the C struct of the item type)
  string ListNode(string type) {
    string name = "df_ln_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (listNodeNames.Add(name)) {
      listNodeTypes.Append($"struct {name} {{\n  struct {name}* next;\n  {CType(ListElem(type))} value;\n}};\n\n");
    }
    return "struct " + name;
  }

  // a new node that holds value, not linked yet
  string NewListNode(string type, string node, string value) {
    string nodeType = ListNode(type);
    return $"{nodeType}* {node} = ({nodeType}*)df_alloc(sizeof({nodeType})); {node}->next = NULL; {node}->value = {value}; ";
  }

  // the node goes at the end of the list
  string AppendListNode(string list, string type, string node) {
    string last = VecField(type, "last");
    return $"if ({list}->{last} == NULL) {{ {list}->{VecField(type, "first")} = {node}; }} else {{ {list}->{last}->next = {node}; }} {list}->{last} = {node}; "
      + $"{list}->{VecField(type, "count")} += 1; ";
  }

  // node = the node at position (the position is checked before)
  string WalkToNode(string list, string type, string node, string position) {
    string step = NewTemp();
    return $"{ListNode(type)}* {node} = {list}->{VecField(type, "first")}; for (df_uns {step} = 0; {step} < {position}; {step}++) {{ {node} = {node}->next; }} ";
  }

  // the item of a node leaves the list: an object ends (spec 6.1)
  string DropListItem(string elem, string slot) {
    if (!IsClass(elem)) {
      return "";
    }
    string gone = NewTemp();
    return $"if ({slot} != NULL) {{ void* {gone} = (void*){slot}; {slot} = NULL; df_drop({gone}); }} ";
  }

  // c[i]: O(n), the index is compared with the count first (spec 6.1)
  Expr ListIndex(Expr obj, Expr index, Node n) {
    string elem = ListElem(obj.Type);
    string result = NewTemp();
    string node = NewTemp();
    string code = WithTemp(new Expr(NullChecked(obj.Code, obj.Type), obj.Type), list => WithTemp(new Expr(IndexValue(index, n), NativeUns), position =>
      CheckRaise($"({{ {CType(elem)} {result} = {ZeroValue(elem)}; if (df_idx_ok({list}->{VecField(obj.Type, "count")}, {position}, {fn!.NameConst})) {{ "
        + WalkToNode(list, obj.Type, node, position) + $"{result} = {node}->value; }} {result}; }})", elem)));
    return new Expr(code, elem);
  }

  // c[i] = v, c[i] += v
  string ListIndexAssign(Node s, Scope scope, Expr obj, Node target) {
    string elem = ListElem(obj.Type);
    var index = ExprOf(target.Children[1], scope, "u64");
    string list = NewTemp();
    string position = NewTemp();
    string node = NewTemp();
    string slot = $"{node}->value";
    string op = s.Text!;
    if (op == "_=") {
      throw NotSupported("Ownership", s);
    }
    if (op == "??=") {
      throw Error("'??=' on a list item is not supported yet", s);
    }
    string head = $"({{ {CType(obj.Type)} {list} = {NullChecked(obj.Code, obj.Type)}; df_uns {position} = {IndexValue(index, target)}; ";
    string check = $"if (!df_idx_ok({list}->{VecField(obj.Type, "count")}, {position}, {fn!.NameConst})) {RaiseJump} ";
    if (op == "=") {
      var assigned = Adopted(ExprOf(s.Children[1], scope, elem), s);
      if (IsClass(elem) && !assigned.Fresh && assigned.Type != "null" && s.Line < LibraryLine) {
        throw Error("a collection owns its items: move an existing object in with ->x, or put a new one", s);
      }
      string value = Convert(assigned, elem, s);
      if (IsClass(elem)) {
        string incoming = NewTemp();
        string replaced = NewTemp();
        // the collection owns its items: the item that is replaced ends (unless it is the same object)
        return head + $"{CType(elem)} {incoming} = {value}; " + check + WalkToNode(list, obj.Type, node, position)
          + $"{CType(elem)} {replaced} = {slot}; {slot} = {incoming}; if ({replaced} != {incoming}) {{ df_drop((void*){replaced}); }} }})";
      }
      return head + check + WalkToNode(list, obj.Type, node, position) + $"{slot} = {value}; }})";
    }
    string combined = Convert(BinaryOp(op[..^1], new Expr(slot, elem), s.Children[1], scope, s), elem, s);
    return head + check + WalkToNode(list, obj.Type, node, position) + $"{slot} = {combined}; }})";
  }

  // a list made from a literal: one node per item, in order
  Expr ListLiteral(Node literal, Scope scope, string type) {
    string elem = ListElem(type);
    string held = NewTemp();
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(type)} {held} = {VecNewEmpty(type)}; ");
    foreach (var item in literal.Children) {
      var value = Adopted(ExprOf(item, scope, elem), item);
      if (IsClass(elem) && !value.Fresh && value.Type != "null") {
        throw Error("a list owns its items: put a new object in the literal, or move one in with ->x", item);
      }
      string node = NewTemp();
      sb.Append("{ ").Append(NewListNode(type, node, Convert(value, elem, item))).Append(AppendListNode(held, type, node)).Append("} ");
    }
    sb.Append($"{held}; }})");
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
      string item = StrOf(new Expr("node->value", elem), at);
      arrayCode.Append($"static df_string {name}({CType(listType)} v) {{\n");
      arrayCode.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      arrayCode.Append("  df_string r = df_str_lit(\"[\", 1);\n  bool firstItem = true;\n");
      arrayCode.Append($"  for ({ListNode(listType)}* node = v->{VecField(listType, "first")}; node != NULL; node = node->next) {{\n");
      arrayCode.Append("    if (!firstItem) {\n      r = df_str_concat(r, df_str_lit(\", \", 2));\n    }\n    firstItem = false;\n");
      arrayCode.Append($"    r = df_str_concat(r, {item});\n  }}\n");
      arrayCode.Append("  return df_str_concat(r, df_str_lit(\"]\", 1));\n}\n\n");
    }
    return name;
  }

  // the end of a list: the items it owns end and the nodes are freed, in a loop (a long list does not recurse)
  void EmitListDropGlue(StringBuilder sb, ClassInfo cls) {
    string first = VecField(cls.Name, "first");
    string nodeType = ListNode(cls.Name);
    sb.Append($"  for ({nodeType}* node = self->{first}; node != NULL; ) {{\n    {nodeType}* after = node->next;\n");
    if (IsClass(ListElem(cls.Name))) {
      sb.Append("    if (node->value != NULL) { void* o = (void*)node->value; node->value = NULL; df_drop(o); }\n");
    }
    sb.Append("    free(node);\n    node = after;\n  }\n");
    sb.Append($"  self->{first} = NULL;\n  self->{VecField(cls.Name, "last")} = NULL;\n  self->{VecField(cls.Name, "count")} = 0;\n");
  }

  // the members of a list that are not plain calls of the class
  Expr? ListMember(Expr list, string name, List<Node> args, Scope scope, Node at) {
    string elem = ListElem(list.Type);
    if (name.StartsWith("node_")) {
      // the C of the methods of the class (VecPrelude): only its own code calls them
      return at.Line >= LibraryLine ? ListNodeMember(list, name, args, scope, at) : null;
    }
    if (name == "fill" && IsClass(elem) && args.Count > 0 && args[^1].Kind != "Null") {
      throw Error("fill would put one object in every item: a class instance is not allowed, fill(null) is", at);
    }
    if (name is "sort" or "sortUnstable") {
      // a bottom-up merge sort that relinks the nodes (spec 6.6): the items do not move
      string compare = CompareFunction(elem) ?? throw Error($"{name} cannot order {elem} yet (objects and tuples need a comparer)", at);
      string descending = SortDescending(name, args, scope, at);
      NeedChunk("sort");
      string held = NewTemp();
      string nodeType = ListNode(list.Type);
      string first = $"{held}->{VecField(list.Type, "first")}";
      string code = $"({{ {CType(list.Type)} {held} = {NullChecked(list.Code, list.Type)}; {LockCheck(held, list.Type)}"
        + $"{first} = ({nodeType}*)df_list_sort((df_lnode*){first}, {held}->{VecField(list.Type, "count")}, offsetof({nodeType}, value), {compare}, {descending}, "
        + $"(df_lnode**)&{held}->{VecField(list.Type, "last")}); }})";
      return new Expr(code, "void");
    }
    if (name == "swap") {
      // two positions only: a new item goes in with replace (spec 6.6)
      CheckSwapArguments(args, scope, at);
      return ObjectCall(list, "exchange", args, scope, at) ?? throw Error("no swap", at);
    }
    if (name == "add" && args.Count == 1 && AddedCollectionType(args[0], scope, elem) is string sourceType) {
      return ListAddCollection(list, args[0], sourceType, scope, at);
    }
    if (name == "removeAt" && args.Count == 1 && args[0].Kind == "Range") {
      return ListRemoveRange(list, args[0], scope, at);
    }
    if (name == "copyFrom") {
      return CollectionCopyFrom(list, args, scope, at);
    }
    return null;
  }

  // the node_ members called by the methods of the class: the receiver is the object of the method
  Expr ListNodeMember(Expr list, string name, List<Node> args, Scope scope, Node at) {
    string type = list.Type;
    string elem = ListElem(type);
    string elemType = CType(elem);
    string nodeType = ListNode(type);
    string first = VecField(type, "first");
    string last = VecField(type, "last");
    string count = VecField(type, "count");
    string l = NewTemp();
    string head = $"({{ {CType(type)} {l} = {list.Code}; ";

    string Arg(int index, string argType) => Convert(ExprOf(args[index], scope, argType), argType, args[index]);

    string Item(int index) {
      var value = ExprOf(args[index], scope, elem);
      return Convert(value, elem, args[index]);
    }

    switch (name) {
      case "node_append": {
        string value = NewTemp();
        string node = NewTemp();
        return new Expr(head + $"{elemType} {value} = {Item(0)}; " + NewListNode(type, node, value) + AppendListNode(l, type, node) + "})", "void");
      }
      case "node_insert": {
        string position = NewTemp();
        string value = NewTemp();
        string node = NewTemp();
        string before = NewTemp();
        string code = head + $"df_uns {position} = {Arg(0, NativeUns)}; {elemType} {value} = {Item(1)}; " + NewListNode(type, node, value)
          + $"if ({position} == 0) {{ {node}->next = {l}->{first}; {l}->{first} = {node}; if ({l}->{last} == NULL) {{ {l}->{last} = {node}; }} }} "
          + $"else {{ {WalkToNode(l, type, before, $"{position} - 1")}{node}->next = {before}->next; {before}->next = {node}; if ({before} == {l}->{last}) {{ {l}->{last} = {node}; }} }} "
          + $"{l}->{count} += 1; }})";
        return new Expr(code, "void");
      }
      case "node_take": {
        string position = NewTemp();
        string node = NewTemp();
        string before = NewTemp();
        string taken = NewTemp();
        string code = head + $"df_uns {position} = {Arg(0, NativeUns)}; {nodeType}* {node} = {l}->{first}; "
          + $"if ({position} == 0) {{ {l}->{first} = {node}->next; if ({l}->{first} == NULL) {{ {l}->{last} = NULL; }} }} "
          + $"else {{ {WalkToNode(l, type, before, $"{position} - 1")}{node} = {before}->next; {before}->next = {node}->next; if ({node} == {l}->{last}) {{ {l}->{last} = {before}; }} }} "
          + $"{elemType} {taken} = {node}->value; free({node}); {l}->{count} -= 1; {taken}; }})";
        return new Expr(code, elem, IsClass(elem));
      }
      case "node_indexOf": {
        string value = NewTemp();
        string found = NewTemp();
        string index = NewTemp();
        string node = NewTemp();
        string same = EqualsCode(new Expr($"{node}->value", elem), new Expr(value, elem), at);
        string code = head + $"{elemType} {value} = {Item(0)}; df_uns {found} = DF_UNS_MAX; df_uns {index} = 0; "
          + $"for ({nodeType}* {node} = {l}->{first}; {node} != NULL; {node} = {node}->next, {index}++) {{ if ({same}) {{ {found} = {index}; break; }} }} {found}; }})";
        return new Expr(code, NativeUns);
      }
      case "node_removeAll": {
        string value = NewTemp();
        string removed = NewTemp();
        string before = NewTemp();
        string node = NewTemp();
        string after = NewTemp();
        string same = EqualsCode(new Expr($"{node}->value", elem), new Expr(value, elem), at);
        string code = head + $"{elemType} {value} = {Item(0)}; df_uns {removed} = 0; {nodeType}* {before} = NULL; {nodeType}* {node} = {l}->{first}; "
          + $"while ({node} != NULL) {{ {nodeType}* {after} = {node}->next; "
          + $"if ({same}) {{ if ({before} == NULL) {{ {l}->{first} = {after}; }} else {{ {before}->next = {after}; }} if ({node} == {l}->{last}) {{ {l}->{last} = {before}; }} "
          + $"{DropListItem(elem, $"{node}->value")}free({node}); {removed}++; }} else {{ {before} = {node}; }} {node} = {after}; }} "
          + $"{l}->{count} -= {removed}; {removed}; }})";
        return new Expr(code, NativeUns);
      }
      case "node_clear": {
        string node = NewTemp();
        string reset = IsClass(elem) ? DropListItem(elem, $"{node}->value") : $"{node}->value = {ZeroValue(elem)}; ";
        return new Expr(head + $"for ({nodeType}* {node} = {l}->{first}; {node} != NULL; {node} = {node}->next) {{ {reset}}} }})", "void");
      }
      case "node_fill": {
        string value = NewTemp();
        string node = NewTemp();
        string old = NewTemp();
        string store = IsClass(elem)
          ? $"{elemType} {old} = {node}->value; {node}->value = {value}; if ({old} != {value}) {{ df_drop((void*){old}); }} "
          : $"{node}->value = {value}; ";
        return new Expr(head + $"{elemType} {value} = {Item(0)}; for ({nodeType}* {node} = {l}->{first}; {node} != NULL; {node} = {node}->next) {{ {store}}} }})", "void");
      }
      case "node_fillCount": {
        string amount = NewTemp();
        string value = NewTemp();
        string step = NewTemp();
        string node = NewTemp();
        return new Expr(head + $"df_uns {amount} = {Arg(0, NativeUns)}; {elemType} {value} = {Item(1)}; "
          + $"for (df_uns {step} = 0; {step} < {amount}; {step}++) {{ {NewListNode(type, node, value)}{AppendListNode(l, type, node)}}} }})", "void");
      }
      case "node_reverse": {
        string previous = NewTemp();
        string node = NewTemp();
        string after = NewTemp();
        return new Expr(head + $"{nodeType}* {previous} = NULL; {nodeType}* {node} = {l}->{first}; {l}->{last} = {l}->{first}; "
          + $"while ({node} != NULL) {{ {nodeType}* {after} = {node}->next; {node}->next = {previous}; {previous} = {node}; {node} = {after}; }} {l}->{first} = {previous}; }})", "void");
      }
      case "node_exchange": {
        string a = NewTemp();
        string b = NewTemp();
        string nodeA = NewTemp();
        string nodeB = NewTemp();
        string held = NewTemp();
        return new Expr(head + $"df_uns {a} = {Arg(0, NativeUns)}; df_uns {b} = {Arg(1, NativeUns)}; " + WalkToNode(l, type, nodeA, a) + WalkToNode(l, type, nodeB, b)
          + $"{elemType} {held} = {nodeA}->value; {nodeA}->value = {nodeB}->value; {nodeB}->value = {held}; }})", "void");
      }
      case "node_replace": {
        string position = NewTemp();
        string value = NewTemp();
        string node = NewTemp();
        string old = NewTemp();
        return new Expr(head + $"df_uns {position} = {Arg(0, NativeUns)}; {elemType} {value} = {Item(1)}; " + WalkToNode(l, type, node, position)
          + $"{elemType} {old} = {node}->value; {node}->value = {value}; {old}; }})", elem, IsClass(elem));
      }
      case "node_truncate": {
        string keep = NewTemp();
        string cut = NewTemp();
        string before = NewTemp();
        string after = NewTemp();
        string code = head + $"df_uns {keep} = {Arg(0, NativeUns)}; if ({keep} < {l}->{count}) {{ {nodeType}* {cut} = NULL; "
          + $"if ({keep} == 0) {{ {cut} = {l}->{first}; {l}->{first} = NULL; {l}->{last} = NULL; }} "
          + $"else {{ {WalkToNode(l, type, before, $"{keep} - 1")}{cut} = {before}->next; {before}->next = NULL; {l}->{last} = {before}; }} "
          + $"while ({cut} != NULL) {{ {nodeType}* {after} = {cut}->next; {DropListItem(elem, $"{cut}->value")}free({cut}); {cut} = {after}; }} {l}->{count} = {keep}; }} }})";
        return new Expr(code, "void");
      }
      case "node_indexOfFrom": {
        // the position among amount items from start, relative to start (the indexOf of a list view)
        string start = NewTemp();
        string amount = NewTemp();
        string value = NewTemp();
        string found = NewTemp();
        string index = NewTemp();
        string node = NewTemp();
        string same = EqualsCode(new Expr($"{node}->value", elem), new Expr(value, elem), at);
        string code = head + $"df_uns {start} = {Arg(0, NativeUns)}; df_uns {amount} = {Arg(1, NativeUns)}; {elemType} {value} = {Item(2)}; df_uns {found} = DF_UNS_MAX; "
          + WalkToNode(l, type, node, start)
          + $"for (df_uns {index} = 0; {index} < {amount}; {index}++, {node} = {node}->next) {{ if ({same}) {{ {found} = {index}; break; }} }} {found}; }})";
        return new Expr(code, NativeUns);
      }
      default:
        throw Error($"internal error: no list member {name}", at);
    }
  }

  // ---------------------------------------------------------------- the iterator of a list

  bool IsListIterator(string type) => vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name == "listIterator";

  // the C of the steps of listIterator.next (VecPrelude): it starts at the first node of its list and follows the links
  Expr ListIteratorMember(Expr iterator, string name, Node at) {
    var cls = ObjInfo(iterator.Type);
    string listType = cls.ParentField!.Type;
    string self = iterator.Code;
    string node = $"{self}->{FindField(cls, "node")!.CName}";
    string started = $"{self}->{FindField(cls, "started")!.CName}";
    string list = $"{self}->{cls.ParentField.CName}";
    return name switch {
      "node_more" => new Expr($"({{ if (!{started}) {{ {node} = {list}->{VecField(listType, "first")}; {started} = true; }} {node} != NULL; }})", "bool"),
      "node_value" => new Expr($"({node}->value)", ListElem(listType)),
      "node_advance" => new Expr($"({{ {node} = {node}->next; }})", "void"),
      _ => throw Error($"internal error: no list iterator member {name}", at)
    };
  }

  // ---------------------------------------------------------------- views (spec 6.5)

  string? ViewTemplate(string type) =>
    vecPrelude && classInstanceArgs.TryGetValue(type, out var instance) && instance.Template.Name is "vecView" or "listView" or "arrView" ? instance.Template.Name : null;

  bool IsView(string type) => ViewTemplate(type) != null;

  string ViewElem(string type) => classInstanceArgs[type].Args[0];

  // View<vec<i32>> written as a type: the view class of that kind of collection
  string ViewTypeOf(Node n) {
    if (n.Children.Count != 1) {
      throw Error("View takes the type of its collection: View<vec<T>>", n);
    }
    string collection = ResolveType(n.Children[0]);
    string kind = IsArr(collection) ? "arrView" : IsVec(collection) ? "vecView" : IsList(collection) ? "listView"
      : throw Error($"views exist on arr, vec and list, not on {collection} (spec 6.5)", n);
    return ClassInstanceName(genericClasses[kind], new List<string> { IndexCollectionElem(collection)! }, n);
  }

  // c[a..b], c[a..=b], c[..b], c[a..]: a view of a part of the collection, no copy (spec 6.5). The range is checked (IndexOutOfRangeException); the view of a vec
  // or a list holds the counter of open iterators and views of its collection
  Expr ViewExpr(Node n, Scope scope) {
    var range = n.Children[1];
    var obj = ExprOf(n.Children[0], scope);
    string? elem = IndexCollectionElem(obj.Type);
    if (elem == null) {
      throw Error($"views exist on arr, vec and list, not on {obj.Type} (spec 6.5)", n);
    }
    string kind = IsArr(obj.Type) ? "arrView" : IsVec(obj.Type) ? "vecView" : "listView";
    string viewType = ClassInstanceName(genericClasses[kind], new List<string> { elem }, n);
    var ctor = funs[viewType][0];
    string held = NewTemp();
    string start = NewTemp();
    string end = NewTemp();
    string length = SourceLength(obj.Type, held);
    string outside = $"{{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }}";
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(obj.Type)} {held} = {(IsArr(obj.Type) ? obj.Code : NullChecked(obj.Code, obj.Type))}; ");
    sb.Append($"df_uns {start} = {(range.Children[0].Kind == "Open" ? "0" : IndexArgument(range.Children[0], scope))}; ");
    if (range.Children[1].Kind == "Open") {
      sb.Append($"df_uns {end} = {length}; ");
    } else if (range.Text == "..=") {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; if ({end} == DF_UNS_MAX) {outside} {end} += 1; ");
    } else {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; ");
    }
    sb.Append($"if ({start} > {end} || {end} > {length}) {outside} ");
    if (!IsArr(obj.Type)) {
      sb.Append($"{held}->{VecField(obj.Type, "iterators")} += 1; ");   // released by the atEnd of the view
    }
    string call = UserCallCode(ctor, $"{ctor.CName}({held}, {start}, {end} - {start})");
    sb.Append($"{call}; }})");
    return new Expr(sb.ToString(), viewType, true);
  }

  // the for loop over a view: (the loop header, the C of the item); a view whose collection ended raises UsageOfDestroyedObjectException
  (string Loop, string Item) ViewLoop(string viewType, string view, string position, StringBuilder sb, string pad) {
    var cls = ObjInfo(viewType);
    string start = $"{view}->{FindField(cls, "start")!.CName}";
    string count = $"{view}->{FindField(cls, "count")!.CName}";
    string loop = $"for (df_uns {position} = 0; {position} < {count}; {position}++)";
    if (ViewTemplate(viewType) == "arrView") {
      return (loop, $"{view}->{FindField(cls, "items")!.CName}.p[{start} + {position}]");
    }
    string collection = $"{view}->{cls.ParentField!.CName}";
    string collectionType = cls.ParentField.Type;
    sb.Append($"{pad}if ({collection} == NULL) {{ df_raise(&df_desc_UsageOfDestroyedObjectException, {fn!.NameConst}); {RaiseJump} }}\n");
    if (IsVec(collectionType)) {
      return (loop, $"{collection}->{VecField(collectionType, "items")}.p[{start} + {position}]");
    }
    // a list view: one walk to the first node of the view, then one link per item
    string node = NewTemp();
    sb.Append($"{pad}{WalkToNode(collection, collectionType, node, start)}\n");
    return ($"for (df_uns {position} = 0; {position} < {count}; {position}++, {node} = {node}->next)", $"{node}->value");
  }

  // C function that prints a view as [a, b]
  string ViewStrFunction(string viewType, Node at) {
    string name = "df_view_str_" + System.Text.RegularExpressions.Regex.Replace(viewType, @"[^A-Za-z0-9_]", "_");
    if (arrayHelpers.Add(name)) {
      var cls = ObjInfo(viewType);
      string elem = ViewElem(viewType);
      string start = $"v->{FindField(cls, "start")!.CName}";
      string count = $"v->{FindField(cls, "count")!.CName}";
      string item;
      var sb = new StringBuilder();
      sb.Append($"static df_string {name}({CType(viewType)} v) {{\n");
      sb.Append("  if (v == NULL) {\n    return df_str_lit(\"null\", 4);\n  }\n");
      if (ViewTemplate(viewType) == "arrView") {
        item = $"v->{FindField(cls, "items")!.CName}.p[{start} + i]";
        sb.Append("  df_string r = df_str_lit(\"[\", 1);\n");
        sb.Append($"  for (df_uns i = 0; i < {count}; i++) {{\n");
      } else {
        string collectionType = cls.ParentField!.Type;
        string collection = $"v->{cls.ParentField.CName}";
        sb.Append($"  if ({collection} == NULL) {{\n    return df_str_lit(\"(destroyed)\", 11);\n  }}\n");
        sb.Append("  df_string r = df_str_lit(\"[\", 1);\n");
        if (IsVec(collectionType)) {
          item = $"{collection}->{VecField(collectionType, "items")}.p[{start} + i]";
          sb.Append($"  for (df_uns i = 0; i < {count}; i++) {{\n");
        } else {
          item = "node->value";
          sb.Append($"  {ListNode(collectionType)}* node = {collection}->{VecField(collectionType, "first")};\n");
          sb.Append($"  for (df_uns i = 0; i < {start}; i++) {{\n    node = node->next;\n  }}\n");
          sb.Append($"  for (df_uns i = 0; i < {count}; i++, node = node->next) {{\n");
        }
      }
      sb.Append("    if (i > 0) {\n      r = df_str_concat(r, df_str_lit(\", \", 2));\n    }\n");
      sb.Append($"    r = df_str_concat(r, {StrOf(new Expr(item, elem), at)});\n  }}\n");
      sb.Append("  return df_str_concat(r, df_str_lit(\"]\", 1));\n}\n\n");
      arrayCode.Append(sb);
    }
    return name;
  }

  // ---------------------------------------------------------------- the index collections as sources

  // the item type of an arr, a vec or a list, or null
  string? IndexCollectionElem(string type) {
    return IsArr(type) ? ArrElem(type) : IsVec(type) ? VecElem(type) : IsList(type) ? ListElem(type) : null;
  }

  // the C that runs body(item) for the items from (the C of) start, count of them, of a source held in source (spec 6.6: any index collection)
  string ForEachSourceItem(string sourceType, string source, string start, string amount, Func<string, string> body) {
    string step = NewTemp();
    if (IsArr(sourceType)) {
      return $"for (df_uns {step} = 0; {step} < {amount}; {step}++) {{ {body($"{source}.p[{start} + {step}]")}}} ";
    }
    if (IsVec(sourceType)) {
      return $"for (df_uns {step} = 0; {step} < {amount}; {step}++) {{ {body($"{source}->{VecField(sourceType, "items")}.p[{start} + {step}]")}}} ";
    }
    string node = NewTemp();
    return $"{{ {WalkToNode(source, sourceType, node, start)}for (df_uns {step} = 0; {step} < {amount}; {step}++, {node} = {node}->next) {{ {body($"{node}->value")}}} }} ";
  }

  string SourceLength(string sourceType, string source) {
    return IsArr(sourceType) ? $"{source}.len" : IsVec(sourceType) ? $"{source}->{VecField(sourceType, "count")}" : $"{source}->{VecField(sourceType, "count")}";
  }

  // the C of an index: an integer, a negative one becomes an index that is always outside
  string IndexArgument(Node node, Scope scope) {
    var value = ExprOf(node, scope, "u64");
    if (!IsInt(value.Type)) {
      throw Error($"an index must be an integer, not {value.Type}", node);
    }
    return SignedTypes.Contains(value.Type) ? $"df_idx_arg((int64_t)({value.Code}))" : IndexValue(value, node);
  }

  // ---------------------------------------------------------------- add(collection), removeAt(range), copyFrom

  // the type of what add(x) is given when it is a collection of the items of the list (and not an item itself), or null
  string? AddedCollectionType(Node arg, Scope scope, string elem) {
    var operand = arg.Kind == "Gift" ? arg.Children[0] : arg;
    if (operand.Kind is not ("Name" or "Member")) {
      return null;
    }
    string type;
    try {
      type = ExprOf(operand, scope).Type;
    } catch (CompileException) {
      return null;   // the call itself reports it
    }
    return type != elem && !Assignable(elem, type) && IndexCollectionElem(type) == elem ? type : null;
  }

  // list.add(other) copies the items of an index collection; list.add(->other) moves them, and the collection given ends (spec 6.6)
  Expr ListAddCollection(Expr list, Node arg, string sourceType, Scope scope, Node at) {
    string elem = ListElem(list.Type);
    bool moving = arg.Kind == "Gift";
    if (IsClass(elem) && !moving) {
      throw Error("add(collection) with objects moves them: write add(->other) (a copy needs cloning, not built yet)", at);
    }
    var source = ExprOf(arg, scope);
    string held = NewTemp();
    string s = NewTemp();
    bool sourceIsObject = !IsArr(sourceType);
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(list.Type)} {held} = {NullChecked(list.Code, list.Type)}; {CType(sourceType)} {s} = {(sourceIsObject ? NullChecked(source.Code, sourceType) : source.Code)}; ");
    if (sourceType == list.Type) {
      sb.Append($"if ({s} == {held}) {{ df_raise(&df_desc_ModifyingProtectedSourceException, {fn!.NameConst}); {RaiseJump} }} ");
    }
    sb.Append(LockCheck(held, list.Type));
    if (moving && sourceType == list.Type) {
      // the nodes of the other list are linked at the end
      string sFirst = $"{s}->{VecField(sourceType, "first")}";
      string sLast = $"{s}->{VecField(sourceType, "last")}";
      string sCount = $"{s}->{VecField(sourceType, "count")}";
      string last = $"{held}->{VecField(list.Type, "last")}";
      sb.Append($"if ({sFirst} != NULL) {{ if ({last} == NULL) {{ {held}->{VecField(list.Type, "first")} = {sFirst}; }} else {{ {last}->next = {sFirst}; }} {last} = {sLast}; "
        + $"{held}->{VecField(list.Type, "count")} += {sCount}; {sFirst} = NULL; {sLast} = NULL; {sCount} = 0; }} ");
    } else {
      sb.Append(ForEachSourceItem(sourceType, s, "0", SourceLength(sourceType, s), item => {
        string node = NewTemp();
        string take = moving && IsClass(elem) ? $"{item} = NULL; " : "";
        return NewListNode(list.Type, node, item) + take + AppendListNode(held, list.Type, node);
      }));
    }
    if (moving && sourceIsObject) {
      sb.Append($"df_drop((void*){s}); ");   // the collection was moved in: it is empty now and ends
    }
    sb.Append("})");
    return new Expr(sb.ToString(), "void");
  }

  // list.removeAt(a..b), a..=b, ..b, a..: the items of the range end; a range outside the list raises IndexOutOfRangeException (spec 6.6)
  Expr ListRemoveRange(Expr list, Node range, Scope scope, Node at) {
    string elem = ListElem(list.Type);
    string held = NewTemp();
    string start = NewTemp();
    string end = NewTemp();
    string before = NewTemp();
    string node = NewTemp();
    string after = NewTemp();
    string step = NewTemp();
    string nodeType = ListNode(list.Type);
    string count = $"{held}->{VecField(list.Type, "count")}";
    string outside = $"{{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }}";
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(list.Type)} {held} = {NullChecked(list.Code, list.Type)}; ");
    sb.Append($"df_uns {start} = {(range.Children[0].Kind == "Open" ? "0" : IndexArgument(range.Children[0], scope))}; ");
    if (range.Children[1].Kind == "Open") {
      sb.Append($"df_uns {end} = {count}; ");
    } else if (range.Text == "..=") {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; if ({end} == DF_UNS_MAX) {outside} {end} += 1; ");
    } else {
      sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; ");
    }
    sb.Append($"if ({start} > {end} || {end} > {count}) {outside} ");
    sb.Append(LockCheck(held, list.Type));
    sb.Append($"{nodeType}* {before} = NULL; {nodeType}* {node} = {held}->{VecField(list.Type, "first")}; ");
    sb.Append($"for (df_uns {step} = 0; {step} < {start}; {step}++) {{ {before} = {node}; {node} = {node}->next; }} ");
    sb.Append($"for (df_uns {step} = {start}; {step} < {end}; {step}++) {{ {nodeType}* {after} = {node}->next; {DropListItem(elem, $"{node}->value")}free({node}); {node} = {after}; }} ");
    sb.Append($"if ({before} == NULL) {{ {held}->{VecField(list.Type, "first")} = {node}; }} else {{ {before}->next = {node}; }} ");
    sb.Append($"if ({node} == NULL) {{ {held}->{VecField(list.Type, "last")} = {before}; }} {count} -= {end} - {start}; }})");
    return new Expr(sb.ToString(), "void");
  }

  // c.copyFrom(dstIndex, source, srcIndex, count) and copyFrom(dstIndex, source, range) on a list or a vec, and on an arr with a vec or a list as the source
  // (an arr from an arr is CopyFrom of the arrays, with its compile-time checks). The source is any index collection of the same items. On a vec and a list
  // dstIndex goes up to .len and the items past the end are added; on an arr the items must fit (spec 6.6)
  Expr CollectionCopyFrom(Expr destination, List<Node> args, Scope scope, Node at) {
    if (args.Any(arg => arg.Kind == "Named") || args.Count is not (3 or 4)) {
      throw Error("copyFrom takes (dstIndex, source, srcIndex, count) or (dstIndex, source, range)", at);
    }
    string elem = IndexCollectionElem(destination.Type)!;
    if (IsClass(elem)) {
      throw Error("copyFrom clones object items, which needs cloning (not built yet)", at);
    }
    var source = ExprOf(args[1], scope);
    bool intoArr = IsArr(destination.Type);
    bool intoVec = IsVec(destination.Type);
    if (IndexCollectionElem(source.Type) != elem) {
      throw Error($"copyFrom needs an arr, a vec or a list of {elem} as the source, not {source.Type}", args[1]);
    }
    string held = NewTemp();
    string s = NewTemp();
    string di = NewTemp();
    string si = NewTemp();
    string n = NewTemp();
    string buffer = NewTemp();
    string index = NewTemp();
    string node = NewTemp();
    string step = NewTemp();
    string elemType = CType(elem);
    string count = SourceLength(destination.Type, held);
    string sourceLength = SourceLength(source.Type, s);
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(destination.Type)} {held} = {(intoArr ? destination.Code : NullChecked(destination.Code, destination.Type))}; ");
    sb.Append($"{CType(source.Type)} {s} = {(IsArr(source.Type) ? source.Code : NullChecked(source.Code, source.Type))}; ");
    sb.Append($"df_uns {di} = {IndexArgument(args[0], scope)}; ");
    if (args.Count == 4) {
      sb.Append($"df_uns {si} = {IndexArgument(args[2], scope)}; df_uns {n} = {IndexArgument(args[3], scope)}; ");
    } else {
      var range = args[2];
      if (range.Kind != "Range") {
        throw Error("copyFrom takes a count or a range after the source", range);
      }
      sb.Append($"df_uns {si} = {(range.Children[0].Kind == "Open" ? "0" : IndexArgument(range.Children[0], scope))}; ");
      if (range.Children[1].Kind == "Open") {
        sb.Append($"df_uns {n} = {sourceLength} < {si} ? DF_UNS_MAX : {sourceLength} - {si}; ");
      } else {
        string end = NewTemp();
        sb.Append($"df_uns {end} = {IndexArgument(range.Children[1], scope)}; ");
        if (range.Text == "..=") {
          sb.Append($"{end} = {end} == DF_UNS_MAX ? 0 : {end} + 1; ");
        }
        sb.Append($"df_uns {n} = {end} < {si} ? DF_UNS_MAX : {end} - {si}; ");
      }
    }
    if (source.Type == destination.Type) {
      sb.Append($"if ({s} == {held}) {{ df_raise(&df_desc_ModifyingProtectedSourceException, {fn!.NameConst}); {RaiseJump} }} ");
    }
    string fits = intoArr ? $" || {n} > {count} - {di}" : "";
    sb.Append($"if ({di} > {count} || {si} > {sourceLength} || {n} > {sourceLength} - {si}{fits}) {{ df_raise(&df_desc_IndexOutOfRangeException, {fn!.NameConst}); {RaiseJump} }} ");
    if (!intoArr) {
      sb.Append(LockCheck(held, destination.Type));   // copyFrom cannot be used on an iterated collection (spec 6.4)
    }
    // the items are read first, then written: the source and the destination do not mix
    sb.Append($"{elemType}* {buffer} = ({elemType}*)df_calloc({n}, sizeof({elemType})); df_uns {index} = 0; ");
    sb.Append(ForEachSourceItem(source.Type, s, si, n, item => $"{buffer}[{index}++] = {item}; "));
    if (intoArr || intoVec) {
      string items = intoArr ? held : $"{held}->{VecField(destination.Type, "items")}";
      if (intoVec) {
        sb.Append($"if ({di} + {n} > {count}) {{ {VecReserve(held, destination.Type, $"{di} + {n}")}}} ");
      }
      sb.Append($"for (df_uns {step} = 0; {step} < {n}; {step}++) {{ {items}.p[{di} + {step}] = {buffer}[{step}]; }} ");
      if (intoVec) {
        sb.Append($"if ({di} + {n} > {count}) {{ {count} = {di} + {n}; }} ");
      }
      sb.Append($"free({buffer}); }})");
      return new Expr(sb.ToString(), "void");
    }
    sb.Append(WalkToNode(held, destination.Type, node, di));
    string added = NewTemp();
    sb.Append($"for (df_uns {step} = 0; {step} < {n}; {step}++) {{ if ({node} != NULL) {{ {node}->value = {buffer}[{step}]; {node} = {node}->next; }} "
      + $"else {{ {NewListNode(destination.Type, added, $"{buffer}[{step}]")}{AppendListNode(held, destination.Type, added)}}} }} ");
    sb.Append($"free({buffer}); }})");
    return new Expr(sb.ToString(), "void");
  }

  // ---------------------------------------------------------------- transformTo<Kind>()

  // c.transformTo<vec>(): the collection becomes another kind of index collection with the same items, which move without a copy of the
  // objects; the original ends (spec 6.6). Between arr, vec and list
  Expr TransformTo(Node receiverNode, List<Node> typeArgs, List<Node> args, Scope scope, Node at) {
    if (args.Count != 0 || typeArgs.Count != 1) {
      throw Error("transformTo takes the kind of collection as its only type argument: transformTo<vec>()", at);
    }
    var original = ExprOf(receiverNode, scope);
    string elem = IndexCollectionElem(original.Type) ?? throw Error($"transformTo works on arr, vec and list, not {original.Type}", at);
    var kindNode = typeArgs[0];
    string kind = kindNode.Text ?? "";
    if (kind is not ("arr" or "vec" or "list")) {
      throw Error($"transformTo turns a collection into an arr, a vec or a list, not {kind}", kindNode);
    }
    string target = kind == "arr" ? "arr<" + elem + ">" : ClassInstanceName(genericClasses[kind], new List<string> { elem }, at);
    if (kindNode.Children.Count > 0 && ResolveType(kindNode) != target) {
      string Shown(string type) => IsArr(type) ? type : DisplayName(type);
      throw Error($"transformTo keeps the item type: {Shown(original.Type)} becomes {Shown(target)}, not {Shown(ResolveType(kindNode))}", kindNode);
    }
    // the original is moved: a variable or a field is left null (an array is left empty)
    Expr moved = original;
    if (!original.Fresh && receiverNode.Kind is "Name" or "Member") {
      if (IsArr(original.Type)) {
        var slotTarget = LValue(receiverNode, scope);
        if (slotTarget.IsConst) {
          throw Error("a const cannot be moved", receiverNode);
        }
        string slot = NewTemp();
        string taken = NewTemp();
        moved = new Expr($"({{ {CType(original.Type)}* {slot} = &({slotTarget.Code}); {CType(original.Type)} {taken} = *{slot}; {slot}->p = NULL; {slot}->len = 0; {taken}; }})", original.Type, true);
      } else {
        moved = ExprOf(Synthetic("Gift", at).Add(receiverNode), scope);
      }
    }
    if (target == original.Type) {
      return new Expr(moved.Code, target, true);
    }
    string elemType = CType(elem);
    string s = NewTemp();
    string result = NewTemp();
    var sb = new StringBuilder();
    sb.Append($"({{ {CType(original.Type)} {s} = {(IsArr(original.Type) ? moved.Code : NullChecked(moved.Code, original.Type))}; ");
    if (!IsArr(original.Type)) {
      sb.Append(LockCheck(s, original.Type));   // the original ends: it cannot be iterated
    }
    string length = SourceLength(original.Type, s);
    if (IsList(original.Type)) {
      // the items leave the nodes, which are freed
      string node = NewTemp();
      string after = NewTemp();
      string index = NewTemp();
      string block = NewTemp();
      sb.Append($"{elemType}* {block} = ({elemType}*)df_calloc({length}, sizeof({elemType})); df_uns {index} = 0; ");
      sb.Append($"for ({ListNode(original.Type)}* {node} = {s}->{VecField(original.Type, "first")}; {node} != NULL; ) {{ {block}[{index}++] = {node}->value; "
        + $"{ListNode(original.Type)}* {after} = {node}->next; free({node}); {node} = {after}; }} ");
      sb.Append($"{s}->{VecField(original.Type, "first")} = NULL; {s}->{VecField(original.Type, "last")} = NULL; ");
      if (kind == "arr") {
        sb.Append($"{ArrCType(elem)} {result} = {{ {block}, {length} }}; ");
      } else {
        sb.Append($"{CType(target)} {result} = {VecNewEmpty(target)}; {result}->{VecField(target, "items")} = ({ArrCType(elem)}){{ {block}, {length} }}; {result}->{VecField(target, "count")} = {length}; ");
      }
      sb.Append($"{s}->{VecField(original.Type, "count")} = 0; df_drop((void*){s}); {result}; }})");
    } else if (kind == "list") {
      // an arr or a vec becomes nodes
      sb.Append($"{CType(target)} {result} = {VecNewEmpty(target)}; ");
      sb.Append(ForEachSourceItem(original.Type, s, "0", length, item => {
        string node = NewTemp();
        return NewListNode(target, node, item) + AppendListNode(result, target, node);
      }));
      if (IsVec(original.Type)) {
        sb.Append($"{s}->{VecField(original.Type, "count")} = 0; df_drop((void*){s}); ");   // the items moved: the vec ends with its block
      }
      sb.Append($"{result}; }})");
    } else if (IsArr(original.Type)) {
      // arr to vec: the block of the array becomes the block of the vec
      sb.Append($"{CType(target)} {result} = {VecNewEmpty(target)}; {result}->{VecField(target, "items")} = {s}; {result}->{VecField(target, "count")} = {s}.len; {result}; }})");
    } else {
      // vec to arr: the block is shrunk to the length and becomes the block of the array
      string items = $"{s}->{VecField(original.Type, "items")}";
      string count = $"{s}->{VecField(original.Type, "count")}";
      string shrunk = NewTemp();
      sb.Append($"{ArrCType(elem)} {result} = {{ {items}.p, {count} }}; ");
      sb.Append($"if ({count} == 0) {{ free({items}.p); {result}.p = NULL; }} else if ({count} < {items}.len) {{ {elemType}* {shrunk} = ({elemType}*)realloc({result}.p, {count} * sizeof({elemType})); if ({shrunk} != NULL) {{ {result}.p = {shrunk}; }} }} ");
      sb.Append($"{items}.p = NULL; {items}.len = 0; {count} = 0; df_drop((void*){s}); {result}; }})");
    }
    return new Expr(sb.ToString(), target, true);
  }

  // .sizeof of an arr, a vec or a list: the header and the allocated block (the capacity), or the header and the nodes, without what the items own (spec 6.6)
  Expr CollectionSizeof(Expr obj) {
    string held = NewTemp();
    string elemType = CType(IndexCollectionElem(obj.Type)!);
    if (IsArr(obj.Type)) {
      return new Expr($"({{ {CType(obj.Type)} {held} = {obj.Code}; (df_uns)(sizeof({held}) + {held}.len * sizeof({elemType})); }})", NativeUns);
    }
    string header = $"sizeof(struct {ObjInfo(obj.Type).StructName})";
    string block = IsVec(obj.Type)
      ? $"{held}->{VecField(obj.Type, "items")}.len * sizeof({elemType})"
      : $"{held}->{VecField(obj.Type, "count")} * sizeof({ListNode(obj.Type)})";
    return new Expr($"({{ {CType(obj.Type)} {held} = {NullChecked(obj.Code, obj.Type)}; (df_uns)({header} + {block}); }})", NativeUns);
  }
}
