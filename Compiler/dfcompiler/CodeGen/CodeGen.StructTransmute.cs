using dfparser;

namespace dfcompiler;

// Transmute with structs (spec 11.1.5.7, 3.5), step 6. [T]x with T or x a struct reads the bytes of x with the layout of T. On a place
// (a variable, a field, an item, a parameter) it is a live view: ([Whole]pair).value = 5 writes into pair; = copies it. On a value that is
// not a place it reads a copy. The view goes through a pointer to a may_alias type, so the C compiler keeps reads and writes of the two
// layouts in order. The compiler checks that the view is not larger than the original (so every access stays inside its bytes); a
// _Static_assert repeats the check in C. Structs that hold strings or objects are not transmuted. '.' binds tighter than transmute
// (2.5.1): the view of a field is [T]s.f, a field of the view is ([T]s).f.
public sealed partial class CodeGen {
  readonly HashSet<string> aliasTypes = new();
  readonly HashSet<string> transmuteChecks = new();

  // a C type that may alias any other: the target of the pointer of a view
  string AliasCType(string type) {
    string cType = CType(type);
    string name = "df_alias_" + cType;
    if (aliasTypes.Add(name)) {
      structProtos.Append($"typedef {cType} __attribute__((__may_alias__)) {name};\n");
    }
    return name;
  }

  bool IsStructTransmute(string to, string from) => IsStruct(to) || IsStruct(from);

  // the checks of [to]x on a value of type from
  void CheckStructTransmute(string to, string from, Node at) {
    foreach (var type in new[] { to, from }) {
      if (IsStruct(type) && StructHoldsObjects(type)) {
        throw Error($"{type} holds strings or objects: only structs of plain values are transmuted (spec 11.1.5.7)", at);
      }
      if (!IsStruct(type) && !(IsNumeric(type) || IsEnum(type) || type is "char" or "addr")) {
        throw Error($"a struct is transmuted to or from another struct or a number, not {type} (spec 11.1.5.7)", at);
      }
    }
    long toSize = LayoutOf(to).Size;
    long fromSize = LayoutOf(from).Size;
    if (toSize > fromSize) {
      throw Error($"[{to}] reads {toSize} bytes, but {Article(from)} {from} has {fromSize}: the view must stay inside the bytes of the original (spec 11.1.5.7)", at);
    }
    if (transmuteChecks.Add(to + "|" + from)) {
      structProtos.Append($"_Static_assert(sizeof({CType(to)}) <= sizeof({CType(from)}), \"[{to}] is larger than {from}\");\n");
    }
  }

  // the place a transmute looks at: a variable, a field, an item, another view
  LValueInfo? TransmutePlace(Node source, Scope scope) {
    var node = source;
    while (node.Kind == "Paren") {
      node = node.Children[0];
    }
    if (node.Kind is not ("Name" or "Member" or "Index" or "Transmute")) {
      return null;
    }
    try {
      return node.Kind == "Index" ? StructItemLValue(node, scope) ?? LValue(node, scope) : LValue(node, scope);
    } catch (CompileException) {
      return null;   // not a place (a property, a constant of the program): the view reads a copy
    }
  }

  // [T]x read: on a place the bytes of the place, on a value the bytes of a copy
  Expr StructTransmuteExpr(Node n, string to, Expr value, Scope scope) {
    CheckStructTransmute(to, value.Type, n);
    if (TransmutePlace(n.Children[1], scope) is LValueInfo place && place.Type == value.Type) {
      return new Expr($"(*({AliasCType(to)}*)&({place.Code}))", to);
    }
    string copy = NewTemp();
    return new Expr($"({{ {CType(value.Type)} {copy} = {value.Code}; *({AliasCType(to)}*)&{copy}; }})", to);
  }

  // ([T]x).f = v, ([T]x)[i] = v: the view as a place; null when it is not a transmute with a struct
  LValueInfo? StructTransmuteLValue(Node n, Scope scope) {
    string to = ResolveType(n.Children[0]);
    var value = ExprOf(n.Children[1], scope);
    if (!IsStructTransmute(to, value.Type)) {
      return null;
    }
    CheckStructTransmute(to, value.Type, n);
    var place = TransmutePlace(n.Children[1], scope)
      ?? throw Error("a view that is written needs a variable, a field or an item: the value would be a copy (spec 11.1.5.7)", n);
    return new LValueInfo($"(*({AliasCType(to)}*)&({place.Code}))", to, place.IsConst, place.Variable);
  }
}
