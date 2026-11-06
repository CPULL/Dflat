using dfparser;

namespace dfcompiler;

// The ownership of arrays (spec 6.8.1, point 20): an array field is owned by its object. Every way into the field gives it an array of
// its own: a new array goes in as it is, ->a moves the block (a becomes empty), and any other array is copied when its items are plain
// (an exact copy) or is a compile error otherwise (->a or a!). The class frees its array fields when the object ends (EmitDropGlue).
public sealed partial class CodeGen {
  // the items of an array that a copy reproduces exactly: natives, strings, enums, tuples of natives (not objects, not arrays)
  bool HasPlainItems(string arrType) {
    string elem = ArrElem(arrType);
    if (IsClass(elem) || IsArr(elem) || IsTraitType(elem)) {
      return false;
    }
    return !IsTuple(elem) || TupleElems(elem).All(item => item.Type != "string" && !IsClass(item.Type) && !IsArr(item.Type) && !IsTuple(item.Type));
  }

  // an array that goes into an item of a collection (an arr, a vec, a list) is owned by it, as by a field: a new array or null goes in as
  // it is, ->a moves it, plain items are copied; other items need ->a or a! (spec 6.8.1). The code of the library passes as it is
  Expr ArrItemValue(Node valueNode, Expr value, string elem, Node at) {
    if (!IsArr(elem) || at.Line >= LibraryLine || LibraryCode || GivesOwnArray(valueNode)) {
      return value;
    }
    if (HasPlainItems(elem)) {
      return new Expr($"{ArrCloneHelper(elem, at)}({Convert(value, elem, at)})", elem, true);
    }
    string name = valueNode.Text ?? "a";
    throw Error($"a collection owns its arrays of {ArrElem(elem)}: write ->{name} to move it in, or {name}! to put a copy (spec 6.8.1)", at);
  }

  // an item that is replaced ends, unless it is the same block
  string ReplaceArrItem(string elem, string slot, string incoming) {
    string replaced = NewTemp();
    return $"{CType(elem)} {replaced} = {slot}; {slot} = {incoming}; if ({replaced}.p != {incoming}.p) {{ {ArrDropHelper(elem)}({replaced}); }} ";
  }

  // ->c[i] of an array item of an arr or a vec: the array leaves the slot, which is left empty
  Expr ArrGiftIndex(Expr obj, bool isVec, Node operand, Scope scope, Node gift) {
    string elem = isVec ? VecElem(obj.Type) : ArrElem(obj.Type);
    var index = ExprOf(operand.Children[1], scope, "u64");
    string holder = NewTemp();
    string position = NewTemp();
    string taken = NewTemp();
    string items = isVec ? $"{holder}->{VecField(obj.Type, "items")}" : holder;
    string length = isVec ? $"{holder}->{VecField(obj.Type, "count")}" : $"{holder}.len";
    string source = isVec ? NullChecked(obj.Code, obj.Type) : obj.Code;
    string code = CheckRaise($"({{ {CType(obj.Type)} {holder} = {source}; df_uns {position} = {IndexValue(index, gift)}; {CType(elem)} {taken} = {ZeroValue(elem)}; "
      + $"if (df_idx_ok({length}, {position}, {fn!.NameConst})) {{ {taken} = {items}.p[{position}]; {items}.p[{position}] = {ZeroValue(elem)}; }} {taken}; }})", elem);
    return new Expr(code, elem, true);
  }

  // ->a for an array: the block moves out and a is left empty; only an owner can give it (not a borrowed parameter or local, not a const)
  Expr ArrGift(Node gift, Node operand, string type, Scope scope) {
    var source = LValue(operand, scope);
    if (!source.IsConst && source.Variable is { BorrowsArray: true }) {
      throw Error($"'{operand.Text}' only borrows its array: ->{operand.Text} cannot take it; copy it with {operand.Text}! (spec 6.8.1)", gift);
    }
    if (source.IsConst || source.Variable is { ReadOnlyRef: true }) {
      throw Error($"'{operand.Text}' is read-only: ->{operand.Text} cannot take its array", gift);
    }
    string taken = NewTemp();
    return new Expr($"({{ {CType(type)} {taken} = {source.Code}; {source.Code}.p = NULL; {source.Code}.len = 0; {taken}; }})", type, true);
  }

  // field = value for an array field, a static or an owned local; the old array is freed
  string ArrFieldAssign(LValueInfo target, Node targetNode, Node value, Scope scope, Node at) {
    string type = target.Type;
    string code;
    if (value.Kind == "Gift") {
      var moved = ExprOf(value, scope);
      if (moved.Type != type) {
        throw Error($"'{targetNode.Text}' is {type}, not {moved.Type}", at);
      }
      code = moved.Code;
    } else if (value.Kind == "Null" || IsNewArray(value)) {
      code = Convert(ExprOf(value, scope, type), type, at);
    } else if (HasPlainItems(type)) {
      code = $"{ArrCloneHelper(type, at)}({Convert(ExprOf(value, scope, type), type, at)})";   // an exact copy of the items
    } else {
      string name = value.Text ?? "a";
      throw Error($"'{targetNode.Text}' owns its array of {ArrElem(type)}: write ->{name} to move it, or {name}! to copy it (spec 6.8.1)", at);
    }
    MarkAssigned(target.Variable);
    string old = NewTemp();
    return $"({{ {CType(type)} {old} = {target.Code}; {target.Code} = {code}; {ArrDropHelper(type)}({old}); }})";
  }

  // a = value for a local that owns or borrows (IsMixedLocalArray): its own old array ends; a new array, ->b or null makes it an owner
  // again, any other array is only borrowed
  string MixedArrAssign(LValueInfo target, Node value, Scope scope, Node at) {
    string type = target.Type;
    string code = Convert(ExprOf(value, scope, type), type, at);
    MarkAssigned(target.Variable);
    string old = NewTemp();
    string flag = target.Variable!.ArrFlag!;
    return $"({{ {CType(type)} {old} = {target.Code}; {target.Code} = {code}; if ({flag} && {old}.p != {target.Code}.p) {{ {ArrDropHelper(type)}({old}); }} "
      + $"{flag} = {(GivesOwnArray(value) ? "true" : "false")}; }})";
  }

  // a ->arr<T> parameter owns its array when its uses can be followed (as for a local, IsOwnedLocalArray); otherwise it is not freed
  bool OwnsGivenArray(FunSig f, ParamSig p) =>
    p.IsGift && IsArr(p.Type) && !IsByPointer(p) && !LibraryCode && f.Node.Children.FirstOrDefault(child => child.Kind == "Block") is Node body
    && ArrayUsesStayLocal(body, null, null, 0, p.Name, true, HasPlainItems(p.Type));

  // the array fields of a class of the program end with its object
  void EmitArrayFieldDrops(ClassInfo cls, System.Text.StringBuilder sb) {
    if (cls.Node.Line >= LibraryLine) {
      return;
    }
    foreach (var field in cls.Fields.Where(field => IsArr(field.Type) && !field.IsStatic)) {
      sb.Append($"  {ArrDropHelper(field.Type)}(self->{field.CName});\n");
      sb.Append($"  self->{field.CName}.p = NULL;\n  self->{field.CName}.len = 0;\n");
    }
    foreach (var field in cls.Fields.Where(field => TupleHoldsArrays(field.Type) && !field.IsStatic)) {
      sb.Append($"  {TupleDropCode(field.Type, "self->" + field.CName)}\n");
    }
    foreach (var field in cls.Fields.Where(field => StructHoldsObjects(field.Type) && !field.IsStatic)) {
      sb.Append($"  {StructDropCode(field.Type, "self->" + field.CName)}\n");   // spec 11.1.5.3
    }
  }

  // the static array fields of the classes of the program end with the program, in the reverse order of their start
  string StaticEndCode() {
    var sb = new System.Text.StringBuilder();
    var statics = staticOrder.Where(field => (IsArr(field.Type) || TupleHoldsArrays(field.Type) || StructHoldsObjects(field.Type)) && field.Owner.Node.Line < LibraryLine).ToList();
    for (int index = statics.Count - 1; index >= 0; index--) {
      var field = statics[index];
      sb.Append(IsArr(field.Type) ? $"  {ArrDropHelper(field.Type)}({field.CName});\n  {field.CName}.p = NULL;\n  {field.CName}.len = 0;\n"
        : StructHoldsObjects(field.Type) ? $"  {StructDropCode(field.Type, field.CName)}\n"
        : $"  {TupleDropCode(field.Type, field.CName)}\n");
    }
    return sb.ToString();
  }

  // the type a declaration names, or null when it cannot be resolved here
  string? TryResolveType(Node typeNode) {
    try {
      return typeNode.Kind == "ResolvedType" ? typeNode.Text : ResolveType(typeNode);
    } catch (CompileException) {
      return null;
    }
  }
}
