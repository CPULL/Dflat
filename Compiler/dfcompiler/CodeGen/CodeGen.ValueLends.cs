using System.Text;
using dfparser;

namespace dfcompiler;

// &T results of values (spec 13.1.1): fun &i32 scoreRef() { score } returns the address of the place (numbers, bool, char, enums, string,
// structs). Inside the compiler its result type is "&T" (C: T*). A call reads through the address, (*f()), which is also a place: f() = v,
// f() += v, f()++ work. &i32 s = f() keeps the address (a local whose C name is (*l_s), as a & parameter); a plain i32 x = f() copies the
// value. The lender stays locked until the last use of the local (Lends.cs).
public sealed partial class CodeGen {
  static bool IsValueLend(string type) => type.Length > 1 && type[0] == '&';

  // a value type that a &T result can lend (objects and arrays are lent as they are)
  bool LendsAsAddress(string type) => !IsClass(type) && !IsArr(type) && !IsMulti(type) && !IsTuple(type) && type != "void" && !IsCountedType(type);

  // the call of a function with a &T result of a value: read and written through the address
  Expr DerefLend(Expr e) => IsValueLend(e.Type) ? new Expr($"(*{e.Code})", e.Type[1..]) : e;

  // return x in a function with a &T result of a value: the address of the place
  string LendReturnCode(Node value, Scope scope) {
    string type = currentRet![1..];
    CheckWritableLend(value, type, scope);
    if (value.Kind == "Call") {
      var lent = ExprOf(value, scope);
      if (lent.Type != type) {
        throw Error($"this function lends a {DisplayName(type)}, not a {DisplayName(lent.Type)}", value);
      }
      return $"(&{lent.Code})";   // the &T result of another call: its address
    }
    return PlaceAddress(value, type, scope);
  }

  // the address of a place that holds a value: a variable, a field, a & parameter, an item of an arr or a vec
  string PlaceAddress(Node place, string type, Scope scope) {
    while (place.Kind == "Paren") {
      place = place.Children[0];
    }
    if (place.Kind == "Index" && place.Children.Count > 1 && place.Children[1].Kind != "Range") {
      var obj = ExprOf(place.Children[0], scope);
      bool isVec = IsVec(obj.Type);
      if (!isVec && !IsArr(obj.Type)) {
        throw NotSupported($"Lending an item of a {DisplayName(obj.Type)} (only arr and vec items have a fixed address)", place);
      }
      string elem = isVec ? VecElem(obj.Type) : ArrElem(obj.Type);
      if (elem != type) {
        throw Error($"this function lends a {DisplayName(type)}, not a {DisplayName(elem)}", place);
      }
      var index = ExprOf(place.Children[1], scope, "u64");
      string holder = NewTemp();
      string position = NewTemp();
      string result = NewTemp();
      string items = isVec ? $"{holder}->{VecField(obj.Type, "items")}" : holder;
      string length = isVec ? $"{holder}->{VecField(obj.Type, "count")}" : $"{holder}.len";
      string source = isVec ? NullChecked(obj.Code, obj.Type) : obj.Code;
      return CheckRaise($"({{ {CType(obj.Type)} {holder} = {source}; df_uns {position} = {IndexValue(index, place)}; {CType(elem)}* {result} = NULL; "
        + $"if (df_idx_ok({length}, {position}, {fn!.NameConst})) {{ {result} = &{items}.p[{position}]; }} {result}; }})", "&" + elem);
    }
    var target = LValue(place, scope);
    if (target.Type != type) {
      throw Error($"this function lends a {DisplayName(type)}, not a {DisplayName(target.Type)}", place);
    }
    return $"(&({target.Code}))";
  }

  // &i32 s = board.scoreRef(), &string t = board.title: a local that keeps the address of a place it may write (spec 13.1.1)
  void EmitLendDecl(Node s, string type, Node? init, StringBuilder sb, Scope scope, string pad) {
    if (init == null) {
      throw Error($"&{DisplayName(type)} {s.Text}: a read/write lend needs the place it lends: &{DisplayName(type)} {s.Text} = ...", s);
    }
    var value = init.Children[0];
    if (ReadOnlySource(value, scope) is string why) {
      throw Error($"&{DisplayName(type)} {s.Text}: {why}; write {DisplayName(type)} {s.Text} (spec 13.1.1)", s);
    }
    frames.Peek().Hoisted.TryGetValue(s.Text!, out var hoisted);
    CheckNotShadowing(s.Text!, scope, s);
    string address;
    if (value.Kind == "Call") {
      var lent = ExprOf(value, scope);
      if (lent.Type != type) {
        throw Error($"this call lends a {DisplayName(lent.Type)}, not a {DisplayName(type)}", value);
      }
      if (!lent.Code.StartsWith("(*")) {
        throw Error($"&{DisplayName(type)} {s.Text}: this call gives a value, not a place (it needs a &{DisplayName(type)} result)", value);
      }
      address = $"(&{lent.Code})";
    } else {
      address = PlaceAddress(value, type, scope);
    }
    VarInfo variable;
    if (hoisted is { LendPointer: string hoistedPointer }) {
      variable = hoisted;
      sb.Append($"{pad}{hoistedPointer} = {address};\n");   // declared at the start of its scope (EmitHoists)
    } else {
      string pointer = $"l_{s.Text}_{++tempCounter}";
      variable = new VarInfo(type, $"(*{pointer})", false) { LendPointer = pointer, RefSlot = type == "string" };
      sb.Append($"{pad}{CType(type)}* {pointer} = {address};\n");
    }
    variable.IsLocal = currentFun != null && !fn!.IsMain;
    scope.Vars[s.Text!] = variable;
    assign.Unassigned.Remove(variable);
    if (!LibraryCode && followingStatements.TryGetValue(s, out var after)) {
      var lenders = LenderPaths(value, scope);
      if (lenders.Count > 0) {
        LockLenders(s, after, lenders, value.Kind is "Index" or "Call", true, scope);   // the place stays where it is (spec 12)
      }
    }
  }
}
