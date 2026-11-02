using dfparser;

namespace dfcompiler;

// Tuples that hold arrays (spec 6.8.1, point 20): a tuple is a value, so it carries its own copy of its arrays. A typed local, a field or a
// static of such a tuple owns its arrays: every value that goes in is a new tuple (a literal, where an array that is not new is copied,
// or the result of a function of the program) or a copy (any other tuple), and the arrays end with the tuple or when it is replaced.
// A return gives the caller its own copy; a tuple inside a tuple follows the same rules. Not covered: parameters (they borrow),
// destructuring and collections of such tuples (an error).
public sealed partial class CodeGen {
  bool TupleHoldsArrays(string type) => IsTuple(type) && TupleElems(type).Any(elem => IsArr(elem.Type) || TupleHoldsArrays(elem.Type));

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
      if (IsArr(elems[index].Type)) {
        drops += $"{ArrDropHelper(elems[index].Type)}({code}.e{index}); {code}.e{index} = {ZeroValue(elems[index].Type)}; ";
      } else if (TupleHoldsArrays(elems[index].Type)) {
        drops += TupleDropCode(elems[index].Type, $"{code}.e{index}");   // a tuple inside the tuple
      }
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
    if (LibraryCode) {
      return false;
    }
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
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

  // a collection of tuples that hold arrays would copy them without ending them: not built yet
  void CheckTupleItems(string type, Node at) {
    string? elem = IsArr(type) ? ArrElem(type) : IsVec(type) ? VecElem(type) : IsList(type) ? ListElem(type) : IsMap(type) ? MapValue(type) : null;
    if (elem == null) {
      return;
    }
    if (TupleHoldsArrays(elem)) {
      throw NotSupported("Collections of tuples that hold arrays", at);
    }
    CheckTupleItems(elem, at);
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
    if (!fromProgram || !(TupleHoldsArrays(e.Type) || IsMulti(e.Type) && TupleElems(e.Type).Any(elem => IsArr(elem.Type) || TupleHoldsArrays(elem.Type)))) {
      return null;
    }
    string held = NewTemp();
    string drops = "";
    var elems = TupleElems(e.Type);
    for (int index = 0; index < elems.Count; index++) {
      if (IsArr(elems[index].Type)) {
        drops += $"{ArrDropHelper(elems[index].Type)}({held}.e{index}); ";
      } else if (TupleHoldsArrays(elems[index].Type)) {
        drops += TupleDropCode(elems[index].Type, $"{held}.e{index}");
      }
    }
    return $"({{ {CType(e.Type)} {held} = {e.Code}; {drops}}})";
  }
}
