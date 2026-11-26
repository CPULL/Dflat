using dfparser;

namespace dfcompiler;

// f(&v[i]), f(&l[i]), f(&m[k]) (spec 13.1, 6.4): the address of an item of a vec, a list or a map, lent read/write to a call. While the
// call runs the collection counts one more open iterator, so nothing can change its size (the item cannot move or end); the counter goes
// down with the temporaries of the statement (df_iter_unfreeze). A missing key of a map is added first with the zero value. A map lends
// the values it keeps as they are (numbers, bool, char, enums, dates); its strings and objects are kept in another form: not lent yet.
// a[i] ??= v, l[i] ??= v, m[k] ??= v (spec 7.1): the item is assigned only when it is null (ItemNullAssign).
public sealed partial class CodeGen {
  string? LentItemAddress(Expr obj, Node target, Scope scope, string type, Node at) {
    bool isVec = IsVec(obj.Type);
    bool isList = IsList(obj.Type);
    bool isMap = IsMap(obj.Type) && !IsView(obj.Type);
    if (!isVec && !isList && !isMap) {
      return null;
    }
    string elem = isVec ? VecElem(obj.Type) : isList ? ListElem(obj.Type) : MapValue(obj.Type);
    if (elem != type) {
      throw Error($"the parameter is a {DisplayName(type)}, not a {DisplayName(elem)}", at);
    }
    string held = NewTemp();
    string counter = $"{held}->{VecField(obj.Type, "iteratorCounter")}";
    string freeze = $"{counter}++; df_tmpf(&{counter}, df_iter_unfreeze); ";
    fn!.UsesTmp = true;
    if (isMap) {
      NeedMapChunk(obj.Type);
      if (SlotC(obj.Type) != CType(elem) || IsClass(elem) || IsArr(elem) || elem == "string" || elem == "string?") {
        throw NotSupported($"&m[k] on the {DisplayName(elem)} values of a map (the values a map keeps as they are: numbers, bool, char, enums, dates)", at);
      }
      string key = NewTemp();
      string slot = NewTemp();
      return $"({{ {CType(obj.Type)} {held} = {NullChecked(obj.Code, obj.Type)}; {MapKeyArg(target.Children[1], scope, MapKey(obj.Type), key, taking: true)}"
        + MapSlot(held, obj.Type, key, slot, true) + freeze + $"({CType(elem)}*){slot}; }})";
    }
    var index = ExprOf(target.Children[1], scope, "u64");
    string position = NewTemp();
    string count = $"{held}->{VecField(obj.Type, "count")}";
    string head = $"({{ {CType(obj.Type)} {held} = {NullChecked(obj.Code, obj.Type)}; df_uns {position} = {IndexValue(index, target)}; "
      + $"if (!df_idx_ok({count}, {position}, {fn.NameConst})) {RaiseJump} ";
    if (isVec) {
      return head + freeze + $"&{held}->{VecField(obj.Type, "items")}.p[{position}]; }})";
    }
    string node = NewTemp();
    return head + WalkToNode(held, obj.Type, node, position) + freeze + $"&{node}->value; }})";
  }

  // c[k] ??= v on an arr, a vec, a list or a map: if c[k] == null { c[k] = v }, the index or the key in a temporary when it is not a name or
  // a literal (it runs once); the collection is named by a variable or a field
  Node? ItemNullAssign(Node s, Scope scope) {
    if (s.Kind != "Assign" || s.Text != "??=" || s.Children[0] is not { Kind: "Index" } target || target.Children.Count != 2
        || target.Children[0].Kind == "GenericRef" || target.Children[1].Kind == "Range") {
      return null;
    }
    var holder = target.Children[0];
    string? type = TryExprType(holder, scope);
    if (type == null || !(IsArr(type) || IsVec(type) || IsList(type) || IsMap(type)) || IsView(type)) {
      return null;
    }
    if (Path(holder) == null) {
      throw Error("c[k] ??= v names the collection by a variable or a field: keep it in a variable first", s);
    }
    var block = Synthetic("Block", s);
    var index = target.Children[1];
    if (index.Kind is not ("Name" or "Int" or "String" or "Char" or "Real" or "Bool" or "Null")) {
      string temp = $"__nullidx{++tempCounter}";
      block.Add(Synthetic("Decl", index, temp).Add(Synthetic("Type", index, "var")).Add(Synthetic("Init", index).Add(index)));
      index = Synthetic("Name", index, temp);
    }
    Node Item() => Synthetic("Index", target).Add(CloneNode(holder)).Add(CloneNode(index));
    var test = Synthetic("Binary", s, "==").Add(Item()).Add(Synthetic("Null", s));
    var assign = Synthetic("Assign", s, "=").Add(Item()).Add(s.Children[1]);
    block.Add(Synthetic("If", s).Add(test).Add(Synthetic("Block", s).Add(assign)));
    return block;
  }
}
