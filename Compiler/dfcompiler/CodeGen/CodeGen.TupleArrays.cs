using dfparser;

namespace dfcompiler;

// Tuples that hold arrays (spec 6.8.1, point 20): a tuple is a value, so it carries its own copy of its arrays. A typed local, a field or a
// static of such a tuple owns its arrays: every value that goes in is a new tuple (a literal, where an array that is not new is copied,
// or the result of a function of the program) or a copy (any other tuple), and the arrays end with the tuple or when it is replaced.
// A return gives the caller its own copy. Not covered: var tuples, parameters (they borrow), destructuring, tuples inside tuples and
// collections of such tuples (an error).
public sealed partial class CodeGen {
  bool TupleHoldsArrays(string type) => IsTuple(type) && TupleElems(type).Any(elem => IsArr(elem.Type));

  // the value a tuple owner takes: its arrays are its own
  string TupleOwnedCode(Node valueNode, Expr value, string type, Node at) {
    string converted = Convert(value, type, at);
    if (LibraryCode || !TupleHoldsArrays(type)) {
      return converted;
    }
    var elems = TupleElems(type);
    var copies = new List<int>();
    for (int index = 0; index < elems.Count; index++) {
      if (!IsArr(elems[index].Type)) {
        continue;
      }
      bool owned = valueNode.Kind == "Tuple" && index < valueNode.Children.Count ? GivesOwnArray(TupleItemNode(valueNode.Children[index]))
        : valueNode.Kind == "Call" && CalleesOf(valueNode) is { Count: > 0 } callees && callees.All(f => f.Node.Line < LibraryLine);
      if (!owned) {
        copies.Add(index);
      }
    }
    if (copies.Count == 0) {
      return converted;
    }
    string held = NewTemp();
    string code = $"({{ {CType(type)} {held} = {converted}; ";
    foreach (int index in copies) {
      code += $"{held}.e{index} = {ArrCloneHelper(elems[index].Type, at)}({held}.e{index}); ";
    }
    return code + $"{held}; }})";
  }

  static Node TupleItemNode(Node item) => item.Kind == "Named" && item.Children.Count > 0 ? item.Children[^1] : item;

  // the arrays of a tuple end
  string TupleDropCode(string type, string code) {
    var elems = TupleElems(type);
    string drops = "";
    for (int index = 0; index < elems.Count; index++) {
      if (IsArr(elems[index].Type)) {
        drops += $"{ArrDropHelper(elems[index].Type)}({code}.e{index}); {code}.e{index} = {ZeroValue(elems[index].Type)}; ";
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
      return init is { Kind: "Tuple" } && init.Children.Any(item => item.Kind is "List" or "Name" or "Copy" or "Gift" or "Call" or "Member" or "Index")
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
}
