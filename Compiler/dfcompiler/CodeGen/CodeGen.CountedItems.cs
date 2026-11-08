using dfparser;

namespace dfcompiler;

// Collections of counted objects (spec 12.7, 6.2): vec<@T> and list<@T>. The item of such a collection is a hidden box class Rc__T(@T value)
// with one counted field, so the collection code (VecPrelude and the C hot paths) sees plain owned objects: adding a box adds a user, dropping
// a box releases it. The program never sees the boxes: in its code an item is read as the object (UnboxRead, the for loop, CountedRef) and a
// value given as an item is put in a new box (BoxCall: the box's header parameter is @T, so the usual counted arguments apply). The members
// that would hand out boxes are not supported on these collections yet (CheckCountedItemsMember).
public sealed partial class CodeGen {
  readonly Dictionary<string, string> boxInner = new();   // box class -> the counted type of its items
  readonly HashSet<string> writableBoxes = new();          // the boxes of list<&@T> and vec<&@T>: read/write items with a lock
  bool rawBoxRead;                                         // CountedRef wants the box of c[i], not its object

  // list<@Dog> becomes list<Rc__Dog>, with the class Rc__Dog(@Dog value) added to the program. Keys of maps and sets cannot be counted
  // (spec 6.2); map values, arr items and other generics with a counted type argument are not built yet
  void LowerCountedTypeArgs(Node program) {
    var boxes = new List<Node>();
    void Walk(Node node) {
      for (int index = 0; index < node.Children.Count; index++) {
        var child = node.Children[index];
        if (child.Kind == "CountedType") {
          node.Children[index] = BoxTypeFor(node, index, child, boxes);
          continue;
        }
        Walk(child);
      }
    }
    Walk(program);
    program.Children.InsertRange(0, boxes);
  }

  Node BoxTypeFor(Node owner, int index, Node counted, List<Node> boxes) {
    string? collection = owner.Kind == "Type" ? owner.Text : null;
    if (owner.Kind == "GenericRef" && owner.Children[0] is { Kind: "Name" } generic) {
      collection = generic.Text;   // vec<@Dog>(): the type arguments follow the name
      index -= 1;
    }
    if (collection is "map" or "bmap" or "hmap" && index == 0 || collection is "set" or "bset" or "hset") {
      throw Error("counted objects (@T) cannot be keys of a map or items of a set: a key must be stable (spec 6.2)", counted);
    }
    if (collection is not ("vec" or "list" or "arr" or "map" or "bmap" or "hmap")) {
      throw NotSupported("Generics with a counted type argument (spec 12.7)", counted);
    }
    var inner = counted.Children[0];
    if (inner is not { Kind: "Type", Children.Count: 0 } || inner.Text == null) {
      throw NotSupported("Counted items of this type", counted);
    }
    bool writable = counted.Children.Any(child => child.Kind == "ReadWrite");   // list<&@Dog> (spec 12.7)
    string box = (writable ? "RcRw__" : "Rc__") + inner.Text;
    if (!boxInner.ContainsKey(box)) {
      boxInner[box] = inner.Text;
      var param = Synthetic("Param", counted, "value").Add(Synthetic("Type", counted, inner.Text)).Add(Synthetic("Counted", counted));
      if (writable) {
        param.Add(Synthetic("ReadWrite", counted));
        writableBoxes.Add(box);
      }
      boxes.Add(Synthetic("Class", counted, box).Add(Synthetic("PrimaryCtor", counted).Add(param)).Add(Synthetic("Block", counted)));
    }
    return Synthetic("Type", counted, box);
  }

  // the counted type of the items of a box type, or null (also null in the code of the collections, which works on the boxes)
  string? BoxInner(string type) => !LibraryCode && boxInner.TryGetValue(type, out var inner) ? inner : null;

  string BoxField(string box) => FindField(classes[box], "value")!.CName;

  // a value given as an item: a new box that takes the usage (the header parameter of the box is @T)
  // the value goes in a Boxed node: its call marker (spec 13.1) was checked by the member that takes it, and a literal or c[i] = x needs none
  Node BoxCall(Node value, string box) =>
    Synthetic("Call", value).Add(Synthetic("Name", value, box)).Add(Synthetic("Args", value).Add(Synthetic("Boxed", value).Add(value)));

  // c[i] of a collection of counted objects reads the object of the box
  Expr UnboxRead(Expr item) {
    if (rawBoxRead || BoxInner(item.Type) is not string inner) {
      return item;
    }
    string box = NewTemp();
    if (IsCountedValueType(inner)) {
      // a counted value item: the value of its cell; an empty slot reads as the zero value (spec 12.7)
      return new Expr($"({{ {CType(item.Type)} {box} = {item.Code}; {CellRead($"({box} != NULL ? {box}->{BoxField(item.Type)} : NULL)", inner)}; }})", inner);
    }
    return new Expr($"({{ {CType(item.Type)} {box} = {item.Code}; {box} != NULL ? DF_RC_OBJ({CType(inner)}, {box}->{BoxField(item.Type)}) : NULL; }})", inner);
  }

  // a box handed out by the collection (replace, takeAt): its usage passes to the caller as a counted result, the empty box ends
  Expr UnboxTaken(Expr box) {
    string inner = boxInner[box.Type];
    string field = BoxField(box.Type);
    string held = NewTemp();
    string usage = NewTemp();
    string code = $"({{ {CType(box.Type)} {held} = {box.Code}; {CType("@" + inner)} {usage} = NULL; "
      + $"if ({held} != NULL) {{ {usage} = {held}->{field}; {held}->{field} = NULL; df_drop((void*){held}); }} {usage}; }})";
    return new Expr(code, "@" + inner, true);
  }

  // the counting block of a value given to a member that only looks at it or shares it: a counted object or null
  string CountedBlockOf(Node arg, string box, Scope scope) {
    if (arg is { Kind: "CountedOf", Children.Count: 1 }) {
      arg = arg.Children[0];   // fill(@rex): the call marker of a counted value (spec 13.1)
    }
    if (arg.Kind == "Null") {
      return "NULL";
    }
    if (CountedRef(arg, scope) is not CountedRefInfo given) {
      throw Error($"a collection of counted objects takes here a counted object or null (spec 12.7)", arg);
    }
    if (!IsSubtype(given.Type, boxInner[box]) && !IsSubtype(boxInner[box], given.Type)) {
      throw Error($"cannot use a {given.Type} as an item of a collection of {boxInner[box]}", arg);
    }
    return given.Code;
  }

  // fill(value) of a vec<@T> or list<@T>, and fill(count, value) of a list<@T>: every item becomes a user of the same object, the usages the
  // items had are released; null when the collection does not hold counted objects
  Expr? CountedFill(Expr coll, List<Node> args, Scope scope, Node at) {
    if (IsArr(coll.Type)) {
      return CountedArrFill(coll, args, scope, at);
    }
    bool isVec = IsVec(coll.Type);
    string box = isVec ? VecElem(coll.Type) : ListElem(coll.Type);
    if (BoxInner(box) == null) {
      return null;
    }
    if (args.Count != (isVec ? 1 : args.Count is 1 or 2 ? args.Count : 1)) {
      throw Error(isVec ? "fill takes one value: fill(value)" : "fill takes a value, or a count and a value: fill(value), fill(count, value)", at);
    }
    string held = NewTemp();
    string block = NewTemp();
    string fresh = $"f_{box}(df_rc_use({block}))";   // the box constructor takes a usage
    string head = $"({{ {CType(coll.Type)} {held} = {NullChecked(coll.Code, coll.Type)}; df_rc* {block} = {CountedBlockOf(args[^1], box, scope)}; ";
    string old = NewTemp();
    string code;
    if (isVec) {
      string items = VecField(coll.Type, "items");
      string i = NewTemp();
      code = head + $"for (df_uns {i} = 0; {i} < {held}->{VecField(coll.Type, "count")}; {i}++) {{ {CType(box)} {old} = {held}->{items}.p[{i}]; "
        + $"{held}->{items}.p[{i}] = {fresh}; if ({old} != NULL) {{ df_drop((void*){old}); }} }} }})";
    } else if (args.Count == 1) {
      string node = NewTemp();
      code = head + $"for ({ListNode(coll.Type)}* {node} = {held}->{VecField(coll.Type, "first")}; {node} != NULL; {node} = {node}->next) {{ "
        + $"{CType(box)} {old} = {node}->value; {node}->value = {fresh}; if ({old} != NULL) {{ df_drop((void*){old}); }} }} }})";
    } else {
      var amount = ExprOf(args[0], scope, NativeUns);
      string count = NewTemp();
      string step = NewTemp();
      string node = NewTemp();
      code = head + $"df_uns {count} = {Convert(amount, NativeUns, args[0])}; {LockCheck(held, coll.Type)}"
        + $"for (df_uns {step} = 0; {step} < {count}; {step}++) {{ {NewListNode(coll.Type, node, fresh)}{AppendListNode(held, coll.Type, node)}}} }})";
    }
    return new Expr(CheckRaise(code, "void"), "void");
  }

  // v.pop(&x) of a vec<@T>: the usage of the last item passes to the counted x, the usage x had is released; false and null when empty
  Expr CountedVecPop(Expr vec, Node targetNode, Scope scope, Node at) {
    string box = VecElem(vec.Type);
    string inner = boxInner[box];
    if (CountedRef(targetNode, scope) is not CountedRefInfo target) {
      throw Error($"pop of a vec<@{inner}> gives a counted object: '{targetNode.Text}' must be @{inner} (spec 12.7)", at);
    }
    if (!IsSubtype(inner, target.Type)) {
      throw Error($"pop gives {inner}, but '{target.Name}' is {target.Type}", at);
    }
    MarkAssigned(target.Variable);
    string items = VecField(vec.Type, "items");
    string count = VecField(vec.Type, "count");
    string field = BoxField(box);
    string held = NewTemp();
    string ok = NewTemp();
    string taken = NewTemp();
    string old = NewTemp();
    string code = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; bool {ok} = false; {CType(box)} {taken} = NULL; {LockCheck(held, vec.Type)}"
      + $"if ({held}->{count} > 0) {{ {held}->{count} -= 1; {taken} = {held}->{items}.p[{held}->{count}]; {held}->{items}.p[{held}->{count}] = NULL; {ok} = true; }} "
      + $"df_rc* {old} = {target.Code}; {target.Code} = NULL; "
      + $"if ({taken} != NULL) {{ {target.Code} = {taken}->{field}; {taken}->{field} = NULL; df_drop((void*){taken}); }} "
      + $"df_rc_release({old}); {ok}; }})";
    return new Expr(CheckRaise(code, "bool"), "bool");
  }

  // the counting block of c[i] when c holds counted objects (for CountedRef), or null
  CountedRefInfo? CountedItemRef(Node index, Scope scope) {
    if (index.Kind != "Index" || LibraryCode || index.Children.Count < 2 || index.Children[1].Kind == "Range") {
      return null;
    }
    bool saved = rawBoxRead;
    rawBoxRead = true;
    Expr item;
    try {
      item = ExprOf(index, scope);
    } catch (CompileException) {
      return null;
    } finally {
      rawBoxRead = saved;
    }
    if (BoxInner(item.Type) is not string inner) {
      return null;
    }
    string box = NewTemp();
    return new CountedRefInfo($"({{ {CType(item.Type)} {box} = {item.Code}; {box} != NULL ? {box}->{BoxField(item.Type)} : NULL; }})", inner,
      $"{index.Children[0].Text ?? "c"}[...]", null, null, writableBoxes.Contains(item.Type));
  }

  // the members of a collection of counted objects that are built; the others would hand out or compare boxes (not built yet)
  static readonly HashSet<string> CountedItemsMembers = new() {
    "add", "insert", "removeAt", "clear", "truncate", "reserve", "shrinkToFit", "swap", "get_len", "get_capacity",
    "has", "indexOf", "remove", "removeAll", "replace", "takeAt", "pop", "fill", "transformTo", "reverse",
    "sort", "sortUnstable", "resize", "fillAtCapacity", "getIterator"
  };

  void CheckCountedItemsMember(ClassInfo cls, string name, Node at) {
    if (LibraryCode || !(IsVec(cls.Name) || IsList(cls.Name))) {
      return;
    }
    string elem = IsVec(cls.Name) ? VecElem(cls.Name) : ListElem(cls.Name);
    if (BoxInner(elem) != null && !CountedItemsMembers.Contains(name)) {
      throw NotSupported($"'{name}' on a collection of counted objects", at);
    }
  }

  // the value searched by has, indexOf, remove and removeAll of a collection of counted objects: a box on the stack around the counting block
  // of the value, with no usage of its own (nothing keeps it, nothing releases it)
  string LookupBox(Node arg, string box, Scope scope) =>
    $"(&({CType(box)[..^1]}){{ .{BoxField(box)} = {CountedBlockOf(arg, box, scope)} }})";

  // two boxes compare their objects with the rules of the objects (equals, ==, or identity, 11.1.3); a slot left empty (no box) is null
  string? BoxEquals(Expr l, Expr r, Node at) {
    if (!boxInner.TryGetValue(l.Type, out var inner) || r.Type != l.Type) {
      return null;
    }
    string field = BoxField(l.Type);
    if (IsCountedValueType(inner)) {
      // counted values compare their values; an empty slot (no box) only equals another empty slot (spec 12.7)
      return WithTemp(l, left => WithTemp(r, right => {
        string same = EqualsCode(new Expr(CellRead($"{left}->{field}", inner), inner), new Expr(CellRead($"{right}->{field}", inner), inner), at);
        return $"(({left} == NULL || {right} == NULL) ? {left} == {right} : {same})";
      }));
    }
    string Obj(string box) => $"({box} != NULL ? DF_RC_OBJ({CType(inner)}, {box}->{field}) : NULL)";
    return WithTemp(l, left => WithTemp(r, right => {
      string leftObj = NewTemp();
      string rightObj = NewTemp();
      string same = ObjectEquals(new Expr(leftObj, inner), new Expr(rightObj, inner), at);
      return $"({{ {CType(inner)} {leftObj} = {Obj(left)}; {CType(inner)} {rightObj} = {Obj(right)}; "
        + $"({leftObj} == NULL || {rightObj} == NULL) ? {leftObj} == {rightObj} : {same}; }})";
    }));
  }

  // the cost of an argument for a parameter whose type is a box (the item of add, insert ...): -1 when it is not that case
  int BoxArgCost(Node arg, string paramType, Scope scope) {
    if (BoxInner(paramType) is not string inner) {
      return -1;
    }
    return CountedArgCost(arg, "@" + inner, scope);
  }

  // fill(value) of an arr<@T>: every slot gets a box of its own, a user of the same object; the boxes the slots had end
  Expr? CountedArrFill(Expr array, List<Node> args, Scope scope, Node at) {
    string box = ArrElem(array.Type);
    if (BoxInner(box) == null) {
      return null;
    }
    if (args.Count != 1) {
      throw Error("fill takes one value: fill(value)", at);
    }
    string held = NewTemp();
    string block = NewTemp();
    string i = NewTemp();
    string old = NewTemp();
    string code = $"({{ {CType(array.Type)} {held} = {array.Code}; df_rc* {block} = {CountedBlockOf(args[0], box, scope)}; "
      + $"for (df_uns {i} = 0; {i} < {held}.len; {i}++) {{ {CType(box)} {old} = {held}.p[{i}]; {held}.p[{i}] = f_{box}(df_rc_use({block})); "
      + $"if ({old} != NULL) {{ df_drop((void*){old}); }} }} }})";
    return new Expr(CheckRaise(code, "void"), "void");
  }

  // resize(n, value) and fillAtCapacity(value) of a vec<@T>: each new slot gets a box of its own, a user of the same object (spec 12.7);
  // the boxes that leave end. null when the vec does not hold counted objects
  Expr? CountedResize(Expr vec, string name, List<Node> args, Scope scope, Node at) {
    string box = VecElem(vec.Type);
    if (BoxInner(box) == null) {
      return null;
    }
    int expected = name == "resize" ? 2 : 1;
    if (args.Count != expected || args.Any(arg => arg.Kind == "Named")) {
      throw Error(name == "resize" ? "resize takes the new length and a value: resize(n, value)" : "fillAtCapacity takes one value: fillAtCapacity(value)", at);
    }
    string items = VecField(vec.Type, "items");
    string count = VecField(vec.Type, "count");
    string held = NewTemp();
    string block = NewTemp();
    string i = NewTemp();
    string old = NewTemp();
    string fresh = $"f_{box}(df_rc_use({block}))";
    string head = $"({{ {CType(vec.Type)} {held} = {NullChecked(vec.Code, vec.Type)}; {LockCheck(held, vec.Type)}df_rc* {block} = {CountedBlockOf(args[^1], box, scope)}; ";
    if (name == "fillAtCapacity") {
      string fill = $"for (df_uns {i} = 0; {i} < {held}->{items}.len; {i}++) {{ {CType(box)} {old} = {held}->{items}.p[{i}]; {held}->{items}.p[{i}] = {fresh}; "
        + $"if ({old} != NULL) {{ df_drop((void*){old}); }} }} {held}->{count} = {held}->{items}.len; }})";
      return new Expr(CheckRaise(head + fill, "void"), "void");
    }
    var wanted = ExprOf(args[0], scope, NativeUns);
    string length = NewTemp();
    var reserve = ObjectCall(new Expr(held, vec.Type), "reserve", new List<Node> { Synthetic("CValue", at, length).Add(Synthetic("Type", at, NativeUns)) }, scope, at)
      ?? throw Error("no reserve", at);
    string code = head + $"df_uns {length} = {Convert(wanted, NativeUns, args[0])}; "
      + $"if ({length} > {held}->{count}) {{ {reserve.Code}; for (df_uns {i} = {held}->{count}; {i} < {length}; {i}++) {{ {held}->{items}.p[{i}] = {fresh}; }} }} "
      + $"else {{ for (df_uns {i} = {length}; {i} < {held}->{count}; {i}++) {{ {CType(box)} {old} = {held}->{items}.p[{i}]; {held}->{items}.p[{i}] = NULL; "
      + $"if ({old} != NULL) {{ df_drop((void*){old}); }} }} }} {held}->{count} = {length}; }})";
    return new Expr(CheckRaise(code, "void"), "void");
  }

  // the result of a member of a collection of counted objects in the program's code: a tuple with a box (next() of an iterator) gives the
  // object of the box, borrowed; the collection cannot change while an iterator is open (spec 6.4, 12.7)
  Expr UnboxTupleResult(Expr result) {
    if (LibraryCode || !IsTuple(result.Type) || !TupleElems(result.Type).Any(elem => BoxInner(elem.Type) != null)) {
      return result;
    }
    var elems = TupleElems(result.Type);
    string held = NewTemp();
    var parts = new List<string>();
    var types = new List<(string Type, string? Name)>();
    for (int index = 0; index < elems.Count; index++) {
      var item = new Expr($"{held}.e{index}", elems[index].Type);
      var read = BoxInner(elems[index].Type) != null ? UnboxRead(item) : item;
      parts.Add(read.Code);
      types.Add((read.Type, elems[index].Name));
    }
    string tuple = MakeTuple(types);
    return new Expr($"({{ {CType(result.Type)} {held} = {result.Code}; ({CType(tuple)}){{ {string.Join(", ", parts)} }}; }})", tuple);
  }
}
