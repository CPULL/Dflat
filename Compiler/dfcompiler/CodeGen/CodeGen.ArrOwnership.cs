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

  // field = value for an array field; the old array of the field is freed
  string ArrFieldAssign(LValueInfo target, Node targetNode, Node value, Scope scope, Node at) {
    string type = target.Type;
    string code;
    if (value.Kind == "Gift") {
      var source = LValue(value.Children[0], scope);
      if (source.Type != type) {
        throw Error($"'{targetNode.Text}' is {type}, not {source.Type}", at);
      }
      if (source.Variable is { BorrowsArray: true }) {
        throw Error($"'{value.Children[0].Text}' only borrows its array: ->{value.Children[0].Text} cannot take it; copy it with {value.Children[0].Text}! (spec 6.8.1)", at);
      }
      string taken = NewTemp();
      code = $"({{ {CType(type)} {taken} = {source.Code}; {source.Code}.p = NULL; {source.Code}.len = 0; {taken}; }})";
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

  // the array fields of a class of the program end with its object
  void EmitArrayFieldDrops(ClassInfo cls, System.Text.StringBuilder sb) {
    if (cls.Node.Line >= LibraryLine) {
      return;
    }
    foreach (var field in cls.Fields.Where(field => IsArr(field.Type) && !field.IsStatic)) {
      sb.Append($"  {ArrDropHelper(field.Type)}(self->{field.CName});\n");
      sb.Append($"  self->{field.CName}.p = NULL;\n  self->{field.CName}.len = 0;\n");
    }
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
