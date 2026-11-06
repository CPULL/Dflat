using System.Text;
using dfparser;

namespace dfcompiler;

// Buffers and big structs (spec 11.1.5.4, 11.1.5.5), step 4. A field arr<T>[N] keeps its N items inside the struct (a C array
// T f_name[N]); read, it is an arr on those items, so indexing, len, loops, printing and the members of arr work on the struct
// itself. It is written item by item, or as a whole from an array literal of N items or from another buffer of the same type and size.
// A local struct larger than 64 KB lives on the heap: its variable is (*v_name), allocated where the scope starts and freed where it
// ends. A plain parameter of such a struct is passed by address (it is a read-only lend anyway).
public sealed partial class CodeGen {
  const long BigStructBytes = 64 * 1024;
  readonly HashSet<Node> bigStructDecls = new();

  // { item, item, ... } for a buffer from an array literal of exactly N items
  string BufferInitializer(StructField field, Node list, Scope scope) {
    if (list.Kind != "List" || list.Children.Count != field.Buffer) {
      throw Error($"the buffer '{field.Name}' takes an array literal of {field.Buffer} items", list);
    }
    string elem = ArrElem(field.Type);
    var items = list.Children.Select(item => Convert(ExprOf(item, scope, elem), elem, item));
    return $"{{ {string.Join(", ", items)} }}";
  }

  // the items of another buffer of the same type and size: the C address of its first item
  string BufferSource(StructField field, Node value, Scope scope) {
    if (value.Kind == "Member" && StructBufferField(value, scope) is (_, StructField source) && source.Type == field.Type && source.Buffer == field.Buffer) {
      return $"({ExprOf(value, scope).Code}).p";
    }
    throw Error($"the buffer '{field.Name}' takes an array literal of {field.Buffer} items or another buffer {field.Type}[{field.Buffer}]", value);
  }

  // a == b on two buffers: item by item
  string BufferEquals(StructField field, string left, string right, long length, Node at) {
    string equal = NewTemp();
    string index = NewTemp();
    string elem = ArrElem(field.Type);
    string items = EqualsCode(new Expr($"{left}.f_{field.Name}[{index}]", elem), new Expr($"{right}.f_{field.Name}[{index}]", elem), at);
    return $"({{ bool {equal} = true; for (df_uns {index} = 0; {equal} && {index} < {length}; {index}++) {{ {equal} = {items}; }} {equal}; }})";
  }

  // s.m: the struct place and the field when m is a buffer of a struct, or null
  (Node Owner, StructField Field)? StructBufferField(Node member, Scope scope) {
    if (member.Kind != "Member") {
      return null;
    }
    Expr owner;
    try {
      owner = ExprOf(member.Children[0], scope);
    } catch (CompileException) {
      return null;
    }
    if (!IsStruct(owner.Type)) {
      return null;
    }
    var field = structs[owner.Type].Fields.FirstOrDefault(candidate => candidate.Name == member.Text);
    return field is { Buffer: not null } ? (member.Children[0], field) : null;
  }

  // a struct place written through one of its fields: not through a read-only place (spec 13.1, 11.1.5.2)
  void CheckStructWritable(Node place, string fieldName, Scope scope, Node at) {
    switch (ReadOnlyPlace(place, scope)) {
      case (string name, ReadOnlyKind.This):
        throw Error($"an unrestricted method only reads its struct: it cannot write the field '{fieldName}' of {name} (spec 11.1.5.2)", at);
      case (string name, ReadOnlyKind.Param):
        throw Error($"'{name}' is a read-only parameter: its fields cannot be written; declare it &{scope.Find(name)!.Type} {name} to change it (spec 13.1, 11.1.5.2)", at);
      case (string name, ReadOnlyKind.Reference):
        throw Error($"'{name}' is a read-only reference: what it holds cannot be written (spec 1.6.2)", at);
      case (string name, ReadOnlyKind.Constant):
        throw Error($"'{name}' is a constant: its fields cannot be written", at);
    }
  }

  // s.m = [ ... ] or s.m = other.m: the items are copied into the struct
  string? BufferAssign(Node s, Scope scope) {
    var targetNode = s.Children[0];
    if (StructBufferField(targetNode, scope) is not (Node ownerNode, StructField field)) {
      return null;
    }
    if (s.Text != "=") {
      throw Error($"the buffer '{field.Name}' takes only =: an array literal of {field.Buffer} items or another buffer", s);
    }
    CheckStructWritable(ownerNode, field.Name, scope, targetNode);
    var owner = ownerNode.Kind == "Index" && StructItemLValue(ownerNode, scope) is LValueInfo item ? item : LValue(ownerNode, scope);
    string target = $"{owner.Code}.f_{field.Name}";
    var value = s.Children[1];
    if (value.Kind == "List") {
      string items = NewTemp();
      return $"({{ {CType(ArrElem(field.Type))} {items}[{field.Buffer}] = {BufferInitializer(field, value, scope)}; memcpy({target}, {items}, sizeof({items})); }})";
    }
    return $"memmove({target}, {BufferSource(field, value, scope)}, sizeof({target}))";
  }

  // s.m[i] = v and the members of arr that change s.m: not through a read-only place
  void CheckBufferWrite(Node collection, Scope scope, Node at) {
    if (StructBufferField(collection, scope) is (Node ownerNode, StructField field)) {
      CheckStructWritable(ownerNode, field.Name, scope, at);
    }
  }

  // ---------------------------------------------------------------- big structs

  // the size and alignment of a type in a struct, as C lays it out (enough to find the structs above 64 KB)
  (long Size, long Align) LayoutOf(string type) {
    if (IsStruct(type)) {
      long size = 0;
      long align = 1;
      string? layout = structs[type].Layout;
      foreach (var field in structs[type].Fields) {
        var (fieldSize, fieldAlign) = field.Buffer is long length
          ? (LayoutOf(ArrElem(field.Type)).Size * length, LayoutOf(ArrElem(field.Type)).Align)
          : layout == "fixed" && field.Type == "bool" ? (4L, 4L) : LayoutOf(field.Type);
        if (layout == "compact") {
          fieldAlign = 1;   // no alignment (spec 11.1.5.6)
        }
        size = (size + fieldAlign - 1) / fieldAlign * fieldAlign + fieldSize;
        align = Math.Max(align, fieldAlign);
      }
      return ((size + align - 1) / align * align, align);
    }
    if (enums.TryGetValue(type, out var info)) {
      return LayoutOf(info.ValueType);
    }
    if (IsNullable(type) && type != "string?") {
      var (innerSize, innerAlign) = LayoutOf(BaseOf(type));
      return (innerAlign + innerSize, innerAlign);
    }
    if (type is "bool") {
      return (1, 1);
    }
    if (type is "char" or "time") {
      return (4, 4);
    }
    if (type == "string?") {
      return (16, 8);
    }
    if (IsNumeric(type)) {
      long bytes = Math.Max(1, Bits(Normalize(type)) / 8);
      return (bytes, bytes);
    }
    return (8, 8);   // pointers, strings, objects, addr, date, clock, lapse
  }

  bool IsBigStruct(string type) => IsStruct(type) && LayoutOf(type).Size > BigStructBytes;

  // a declaration of a struct above 64 KB: it lives on the heap (spec 11.1.5.5)
  bool IsBigStructDecl(Node decl) {
    if (LibraryCode || structs.Count == 0) {
      return false;
    }
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode is { Kind: "Type", Text: "var" }) {
      var init = decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0];
      return init is { Kind: "Call" } call && call.Children[0] is { Kind: "Name" } called && structs.ContainsKey(called.Text!) && IsBigStruct(called.Text!);
    }
    return typeNode is { Kind: "Type" } && TryResolveType(typeNode) is string type && IsBigStruct(type);
  }

  // Big b, var b = Big(...): the block on the heap starts at zero (df_calloc); the given values and the start values are written into
  // it one field at a time, so no copy of the struct is made on the stack (spec 11.1.5.5)
  string BigStructFill(string target, StructInfo info, Node? creation, Scope scope, string pad) {
    var given = creation == null ? new List<(StructField Field, Node Value)>() : StructGiven(info, creation.Children[1].Children, creation);
    var sb = new StringBuilder();
    void Write(StructField field, Node value, Scope from) {
      if (field.Buffer != null) {
        if (value.Kind == "List") {
          string items = NewTemp();
          sb.Append($"{pad}{{ {CType(ArrElem(field.Type))} {items}[{field.Buffer}] = {BufferInitializer(field, value, from)}; memcpy({target}.f_{field.Name}, {items}, sizeof({items})); }}\n");
        } else {
          sb.Append($"{pad}memmove({target}.f_{field.Name}, {BufferSource(field, value, from)}, sizeof({target}.f_{field.Name}));\n");
        }
        return;
      }
      sb.Append($"{pad}{target}.f_{field.Name} = {StructFieldValue(field, ExprOf(value, from, field.Type), value)};\n");
    }
    foreach (var (field, value) in given) {
      Write(field, value, scope);
    }
    foreach (var field in info.Fields.Where(field => field.Init != null && !given.Any(pair => pair.Field == field))) {
      Write(field, field.Init!, new Scope(null));
    }
    return sb.ToString();
  }
}
