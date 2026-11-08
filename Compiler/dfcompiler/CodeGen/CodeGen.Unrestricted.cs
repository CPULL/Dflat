using dfparser;

namespace dfcompiler;

// unrestricted is a promise checked by the compiler (spec 1.6.2): a method of a class marked unrestricted does not change its object. Inside it
// these are compile errors: writing a field of this (also through an item or a field of a field: the check is deep), moving it with ->,
// ending it, passing it as &, calling a method of this that is not unrestricted or static, and calling a method that is not unrestricted (or
// a member of a collection that changes it) on what the object owns. Reading, unrestricted calls and local variables are free. The methods of
// structs have their own check (spec 11.1.5.2).
public sealed partial class CodeGen {
  bool InUnrestrictedMethod => !LibraryCode && currentFun is { IsUnrestricted: true, IsStatic: false } && currentClass != null && currentStruct == null;

  // the field of this a place starts from (this.a.b[3], a.b, a[0].c), or null when it starts from a local, a parameter or a static
  string? SelfRoot(Node place, Scope scope) {
    var node = place;
    while (node.Kind is "Member" or "Index" or "Paren" && node.Children.Count > 0) {
      if (node.Kind == "Member" && node.Children[0] is { Kind: "Name", Text: "this" }) {
        return FieldOfThis(node.Text!) ? node.Text : null;
      }
      node = node.Children[0];
    }
    if (node is { Kind: "Name", Text: "this" }) {
      return "this";
    }
    if (node.Kind == "Name" && scope.Find(node.Text!) == null && FieldOfThis(node.Text!)) {
      return node.Text;
    }
    return null;
  }

  bool FieldOfThis(string name) =>
    currentClass != null && FindField(currentClass, name) is { IsStatic: false, IsParentRef: false, IsWeak: false };

  // a write (=, +=, ++, ->, is atEnd., &x) inside an unrestricted method
  void CheckUnrestrictedWrite(Node target, Scope scope) {
    if (!InUnrestrictedMethod) {
      return;
    }
    if (SelfRoot(target, scope) is string field) {
      throw Error(field == "this"
        ? "an unrestricted method does not change its object: it cannot write this (spec 1.6.2)"
        : $"an unrestricted method does not change its object: it cannot write '{field}' or what it holds (spec 1.6.2)", target);
    }
  }

  // a method called inside an unrestricted method on this or on what this owns: only unrestricted ones, and for the collections of the
  // library only the members that do not change them
  void CheckUnrestrictedCall(FunSig chosen, string name, string receiverCode, Node at) {
    if (!InUnrestrictedMethod || chosen.IsStatic || chosen.IsUnrestricted || ParentReceiver(receiverCode) != null) {
      return;
    }
    if (chosen.IsAccessor && name.StartsWith("get_")) {
      return;   // reading a property (its R:) is a read (spec 1.6.2, 11.1.4)
    }
    if (!(receiverCode == "self" || receiverCode.Contains("self->") || receiverCode.Contains("(self)"))) {
      return;
    }
    bool library = chosen.Class != null && chosen.Class.Node.Line >= LibraryLine;
    if (library && (ReadOnlyCollectionMembers.Contains(name) || name.StartsWith("get_")
        || chosen.Class!.Name == "File" && ReadOnlyFileMembers.Contains(name))) {
      return;
    }
    throw Error(library
      ? $"an unrestricted method does not change its object: '{name}' can change a collection it owns (spec 1.6.2)"
      : $"an unrestricted method does not change its object: '{name}' is not unrestricted{DefinedAt(new[] { chosen }, name)} (spec 1.6.2)", at);
  }

  // the members of a value receiver (arrays, strings) called on what this owns: the ones that change it in place are writes
  static readonly HashSet<string> StrChangingInPlace = new() { "append", "prepend", "insert", "remove", "replaceAt", "clear" };

  void CheckUnrestrictedValueCall(Node receiverNode, Expr receiver, string name, Scope scope, Node at) {
    if (!InUnrestrictedMethod || SelfRoot(receiverNode, scope) is not string field) {
      return;
    }
    bool changes = receiver.Type == "string" ? StrChangingInPlace.Contains(name)
      : (IsArr(receiver.Type) || IsVec(receiver.Type) || IsList(receiver.Type) || IsMap(receiver.Type) || IsSet(receiver.Type))
        && !ReadOnlyCollectionMembers.Contains(name) && !name.StartsWith("get_");
    if (changes) {
      throw Error($"an unrestricted method does not change its object: '{name}' changes '{field}' (spec 1.6.2)", at);
    }
  }
}
