using dfparser;

namespace dfcompiler;

// Tuples that hold arrays (spec 6.8.1, point 20): a tuple is a value, so it carries its own copy of its arrays. A typed local, a field or a
// static of such a tuple owns its arrays: every value that goes in is a new tuple (a literal, where an array that is not new is copied,
// or the result of a function of the program) or a copy (any other tuple), and the arrays end with the tuple or when it is replaced.
// A return gives the caller its own copy; a tuple inside a tuple follows the same rules. Not covered: parameters (they borrow),
// destructuring and collections of such tuples (an error).
// Objects follow the same rules as in a struct (spec 3.2, 11.1.5.3): the tuple owns them, a copy clones them, a new tuple is taken as it
// is, and they end with the tuple; a counted part (@T) is one more user on a copy and releases its usage at the end.
public sealed partial class CodeGen {
  bool TupleHoldsArrays(string type) => IsTuple(type) && TupleElems(type).Any(elem => IsTupleOwnedPart(elem.Type) || TupleHoldsArrays(elem.Type));

  // the parts a tuple owns: arrays, function values, objects (spec 3.2) and counted usages (spec 12.7)
  bool IsTupleOwnedPart(string type) => IsArr(type) || IsFunType(type) || IsClass(type) || IsCountedType(type) || StructHoldsObjects(type);

  // the end of one part of a tuple
  string TuplePartDrop(string type, string code) =>
    IsArr(type) ? $"{ArrDropHelper(type)}({code}); {code} = {ZeroValue(type)}; "
    : IsFunType(type) ? $"df_fn_drop(&{code}); "
    : IsCountedType(type) ? $"df_rc_release({code}); {code} = NULL; "
    : IsClass(type) ? $"if ({code} != NULL) {{ void* o = (void*){code}; {code} = NULL; df_drop(o); }} "
    : StructHoldsObjects(type) ? StructDropCode(type, code) + " "   // a struct part ends its strings and objects (spec 11.1.5.3)
    : TupleHoldsArrays(type) ? TupleDropCode(type, code)
    : "";

  // the value a tuple owner takes: its arrays are its own (also those of the tuples inside it)
  string TupleOwnedCode(Node valueNode, Expr value, string type, Node at) {
    string converted = Convert(value, type, at);
    if (LibraryCode || !TupleHoldsArrays(type)) {
      return converted;
    }
    string held = NewTemp();
    string copies = TupleCopies(valueNode, type, held, at);
    return copies == "" ? converted : $"({{ {CType(type)} {held} = {converted}; {copies}{held}; }})";
  }

  // the copies of the arrays of a tuple value that are not new: a literal says it item by item, a call of functions of the program gives
  // new ones, anything else is copied
  string TupleCopies(Node? valueNode, string type, string code, Node at) {
    bool fromProgram = valueNode is { Kind: "Call" } && CalleesOf(valueNode) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine);
    if (fromProgram) {
      return "";
    }
    var elems = TupleElems(type);
    string copies = "";
    for (int index = 0; index < elems.Count; index++) {
      Node? item = valueNode is { Kind: "Tuple" } && index < valueNode.Children.Count ? TupleItemNode(valueNode.Children[index]) : null;
      if (IsArr(elems[index].Type) && !(item != null && GivesOwnArray(item))) {
        copies += $"{code}.e{index} = {ArrCloneHelper(elems[index].Type, at)}({code}.e{index}); ";
      } else if (IsFunType(elems[index].Type) && !(item is { Kind: "Lambda" or "Call" or "Copy" or "Null" })) {
        copies += $"{code}.e{index} = df_fn_retain({code}.e{index}); ";   // one more usage (spec 13.3.5)
      } else if (IsCountedType(elems[index].Type) && !(item is { Kind: "Call" or "Null" or "Gift" })) {
        copies += $"{code}.e{index} = df_rc_use({code}.e{index}); ";   // one more user (spec 12.7)
      } else if (IsClass(elems[index].Type) && !(item is { Kind: "Call" or "Copy" or "Null" or "Gift" })) {
        if (NotClonableReason(ObjInfo(elems[index].Type)) is string reason) {
          throw Error($"a copy of a tuple clones its objects: {DisplayName(elems[index].Type)} cannot be cloned, {reason}; give the tuple a new object or move it with -> (spec 3.2)", at);
        }
        cloneUsed = true;
        copies += $"{code}.e{index} = {CloneValue($"{code}.e{index}", elems[index].Type, at)}; ";   // a copy clones the object (spec 3.2)
      } else if (StructHoldsObjects(elems[index].Type)) {
        if (!(item is { Kind: "Call" or "Copy" or "Gift" })) {
          copies += $"{code}.e{index} = {StructCloneHelper(elems[index].Type)}({code}.e{index}); ";   // a copy clones the struct (spec 11.1.5.3)
        }
      } else if (TupleHoldsArrays(elems[index].Type)) {
        copies += TupleCopies(item, elems[index].Type, $"{code}.e{index}", at);   // a tuple inside the tuple
      }
    }
    return copies;
  }

  static Node TupleItemNode(Node item) => item.Kind == "Named" && item.Children.Count > 0 ? item.Children[^1] : item;

  // the arrays of a tuple end
  string TupleDropCode(string type, string code) {
    var elems = TupleElems(type);
    string drops = "";
    for (int index = 0; index < elems.Count; index++) {
      drops += TuplePartDrop(elems[index].Type, $"{code}.e{index}");   // a tuple inside the tuple ends its own parts
    }
    return drops;
  }

  // t = value for a tuple owner: the old arrays end after the new value is made
  string TupleAssign(LValueInfo target, Node value, Scope scope, Node at) {
    var assigned = ExprOf(value, scope, target.Type);
    string code = TupleOwnedCode(value, assigned, target.Type, at);
    MarkAssigned(target.Variable);
    string old = NewTemp();
    return $"({{ {CType(target.Type)} {old} = {target.Code}; {target.Code} = {code}; {TupleDropCode(target.Type, old)}}})";
  }

  bool IsTupleOwner(LValueInfo target) =>
    !LibraryCode && TupleHoldsArrays(target.Type) && (target.IsField || target.IsStatic || target.Variable is { OwnsTuple: true });

  // t.0 = a where t owns its arrays: the item follows the rule of an array field
  bool IsOwnedTupleItem(Node targetNode, Scope scope) {
    if (targetNode.Kind != "Member" || LibraryCode) {
      return false;
    }
    try {
      return IsTupleOwner(LValue(targetNode.Children[0], scope));
    } catch (CompileException) {
      return false;
    }
  }

  // a declaration of a tuple that holds arrays: it owns them and they end with the scope. The type is written, or it is var with a tuple
  // literal or a call of functions of the program that return such a tuple (the type of a literal is checked when it is emitted)
  bool IsTupleArrDecl(Node decl) {
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (LibraryCode) {
      // the collections of the library: an item taken out of them (T gone = ->items[i], T gone = takeAt(i)) ends with the local (spec 3.2)
      var taken = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
      return taken is { Kind: "Gift" } or { Kind: "Call", Text: "takeAt" } or { Kind: "Call", Children: [{ Text: "takeAt" }, ..] }
        && typeNode != null && TryResolveType(typeNode) is string taken_type && TupleHoldsArrays(taken_type);
    }
    if (typeNode is { Kind: "Type", Text: "var" }) {
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
      return init is { Kind: "Tuple" } && init.Children.Any(item => item.Kind is "List" or "Name" or "Copy" or "Gift" or "Call" or "Member" or "Index" or "Tuple")
        || init is { Kind: "Call" } && CalleesOf(init) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine && TupleHoldsArrays(f.Ret));
    }
    return typeNode is { Kind: "TupleType" or "ResolvedType" } && TryResolveType(typeNode) is string type && TupleHoldsArrays(type);
  }

  // the values of a call of functions of the program that return several values: their arrays are new (MultiReturnCode)
  bool GivesOwnMultiValues(Node multi) =>
    !LibraryCode && multi.Children.Count > 1 && multi.Children[1].Children is [{ Kind: "Call" } call]
    && CalleesOf(call) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine && IsMulti(f.Ret));

  static Node TupleDropNode(string name, Node at) =>
    Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("TupleDrop", at, name)));

  // arr, vec, list and the values of map and hmap own the parts of their tuple items (spec 3.2, 6.6)
  void CheckTupleItems(string type, Node at) {
    string? elem = IsArr(type) ? ArrElem(type) : IsVec(type) ? VecElem(type) : IsList(type) ? ListElem(type) : IsMap(type) ? MapValue(type) : null;
    if (elem == null) {
      return;
    }
    if (ItemOwnsParts(elem)) {
      tupleItemAt ??= at;
    }
    CheckTupleItems(elem, at);
  }

  // tuple items of arr, vec and list that own objects, arrays, function values or counted usages (spec 3.2): the collection owns them,
  // a copy of an item clones them, a removed or replaced item ends them, and they end with the collection
  // structs that hold strings or objects follow the same rules (spec 11.1.5.3)
  bool IsTupleItem(string elem) => !LibraryCode && ItemOwnsParts(elem);

  // a tuple or a struct item that owns what it holds: the collection ends it with the item (the C does not depend on the code that asks)
  bool ItemOwnsParts(string elem) => TupleHoldsArrays(elem) || StructHoldsObjects(elem);

  Node? tupleItemAt;   // a place for the errors of the helpers below (the first declaration of such a collection)

  // the C function that ends the parts of a tuple item
  string TupleItemDropHelper(string type) {
    string name = "df_tidrop_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (tupleItemHelpers.Add(name)) {
      structProtos.Append($"static void {name}({CType(type)} v);\n");
      string drops = StructHoldsObjects(type) ? StructDropCode(type, "v") : TupleDropCode(type, "v");   // the helpers of the parts come first
      tupleItemCode.Append($"static void {name}({CType(type)} v) {{\n  {drops}\n}}\n\n");
    }
    return name;
  }

  // the C function that copies a tuple item for a new owner: its objects cloned, its arrays copied, one more usage of the rest
  string TupleItemDupHelper(string type, Node? at = null) {
    string name = "df_tidup_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (tupleItemHelpers.Add(name)) {
      structProtos.Append($"static {CType(type)} {name}({CType(type)} v);\n");
      string copies = StructHoldsObjects(type) ? $"c = {StructCloneHelper(type)}(c);" : TupleCopies(null, type, "c", at ?? tupleItemAt!);
      tupleItemCode.Append($"static {CType(type)} {name}({CType(type)} v) {{\n  {CType(type)} c = v;\n  {copies}\n  return c;\n}}\n\n");
    }
    return name;
  }

  readonly HashSet<string> tupleItemHelpers = new();
  readonly System.Text.StringBuilder tupleItemCode = new();   // after the class code: the helpers of the parts are there

  // the value a tuple item takes: a new tuple as it is, the parts of any other one copied (as TupleOwnedCode)
  string TupleItemAs(Expr value, Node? node, string type, string converted) {
    if (StructHoldsObjects(type)) {
      return value.Fresh ? converted : $"{StructCloneHelper(type)}({converted})";   // a new struct as it is, any other one cloned
    }
    if (node == null) {
      return $"{TupleItemDupHelper(type)}({converted})";
    }
    tupleItemAt ??= node;
    string held = NewTemp();
    string copies = TupleCopies(node, type, held, node);
    return copies == "" ? converted : $"({{ {CType(type)} {held} = {converted}; {copies}{held}; }})";
  }

  // a new tuple that holds arrays (the result of a function of the program) that nothing keeps: f().0.len, g(f()). A copy of it goes on
  // the temporaries of the statement, which end its arrays when the statement ends (as a new object, point 85)
  Expr TempTuple(Expr e, Node valueNode) {
    if (LibraryCode || !TupleHoldsArrays(e.Type) || !(valueNode is { Kind: "Call" } && CalleesOf(valueNode) is { Count: > 0 } callees
        && callees.All(f => f.Node.Line < LibraryLine && TupleHoldsArrays(f.Ret)))) {
      return e;
    }
    return e with { Code = TempBoxCode(e.Type, e.Code, TupleDropCode(e.Type, "(*t)")) };
  }

  // the value is kept in a block of its own on the temporaries of the statement; drop ends what it holds (t points to the value)
  string TempBoxCode(string type, string code, string drop) {
    string name = "df_tmpdrop_" + System.Text.RegularExpressions.Regex.Replace(type, @"[^A-Za-z0-9_]", "_");
    if (tempDropHelpers.Add(name)) {
      classCode.Append($"static void {name}(void* p) {{\n  {CType(type)}* t = ({CType(type)}*)p;\n  {drop}\n  free(p);\n}}\n\n");
    }
    string box = NewTemp();
    return $"({{ {CType(type)}* {box} = ({CType(type)}*)malloc(sizeof({CType(type)})); if (!{box}) {{ df_out_of_memory(); }} *{box} = {code}; "
      + $"*({CType(type)}*)df_tmpf({box}, {name}); }})";
  }

  readonly HashSet<string> tempDropHelpers = new();

  // a result nobody keeps (a statement f()): the new arrays it gives end at once; null when there are none
  string? DroppedResultCode(Node call, Expr e) {
    if (LibraryCode || call.Kind != "Call") {
      return null;
    }
    if (IsArr(e.Type) && IsNewArray(call)) {
      return $"{ArrDropHelper(e.Type)}({e.Code})";
    }
    bool fromProgram = CalleesOf(call) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine);
    if (!fromProgram || !(TupleHoldsArrays(e.Type) || IsMulti(e.Type) && TupleElems(e.Type).Any(elem => IsTupleOwnedPart(elem.Type) || TupleHoldsArrays(elem.Type)))) {
      return null;
    }
    string held = NewTemp();
    string drops = "";
    var elems = TupleElems(e.Type);
    var resultCallees = CalleesOf(call);
    for (int index = 0; index < elems.Count; index++) {
      // several results: an object part ends only when the function gives it (->T); a plain one is a lend (spec 13.1.1)
      if (IsMulti(e.Type) && IsClass(elems[index].Type) && !resultCallees.All(f => f.Gives.Contains(index))) {
        continue;
      }
      drops += TuplePartDrop(elems[index].Type, $"{held}.e{index}");
    }
    return $"({{ {CType(e.Type)} {held} = {e.Code}; {drops}}})";
  }
}
