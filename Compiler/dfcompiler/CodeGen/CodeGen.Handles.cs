using System.Text;
using dfparser;

namespace dfcompiler;

// A C handle (spec 22): handle Db addr, handle Buffer u64, handle File i32 invalid -1. In C it is the value of the type it is made of;
// null is its invalid value. It cannot be copied, only moved (->x), so one owner frees it: when an owned handle ends and it is not null,
// its freeing function (the fun marked [~T name]) runs and its result is ignored. A plain parameter lends it; ->T name takes it;
// [~T name] takes it from the caller's variable, which is null after the call.
sealed class HandleInfo {
  public required string Name { get; init; }
  public required string Base { get; init; }      // addr, or an integer type
  public required string Invalid { get; init; }   // the C value of null
  public required Node Node { get; init; }
  public FunSig? Free { get; set; }               // the function with [~T name] (one per handle type)
}

public sealed partial class CodeGen {
  readonly Dictionary<string, HandleInfo> handles = new();

  bool IsHandle(string? type) => type != null && handles.ContainsKey(type);

  // ---------------------------------------------------------------- the declarations

  void DeclareHandles(Node program) {
    foreach (var node in program.Children.Where(statement => statement.Kind == "Handle")) {
      if (handles.ContainsKey(node.Text!) || IsKnownType(node.Text!)) {
        throw Error($"'{node.Text}' is already declared", node);
      }
      string made = Normalize(ResolveType(node.Children.First(child => TypeNodeKinds.Contains(child.Kind))));
      if (made != "addr" && !IsInt(made)) {
        throw Error($"a handle is made of addr or an integer type (u64, i32, ...), not {made} (spec 22)", node);
      }
      string invalid = made == "addr" ? "NULL" : $"(({PrimitiveCType(made)})0)";
      if (node.Children.FirstOrDefault(child => child.Kind == "Invalid") is Node given) {
        if (made == "addr") {
          throw Error("the invalid value of an addr handle is null: it has no invalid (spec 22)", given);
        }
        var value = given.Children[0];
        bool negative = value.Kind == "Neg";
        var literal = negative ? value.Children[0] : value;
        if (literal.Kind != "Int") {
          throw Error($"invalid takes an integer: handle {node.Text} {made} invalid -1 (spec 22)", given);
        }
        var number = ExprOf(value, new Scope(null), made);
        invalid = $"(({PrimitiveCType(made)})({Convert(number, made, given)}))";
      }
      handles[node.Text!] = new HandleInfo { Name = node.Text!, Base = made, Invalid = invalid, Node = node };
    }
  }

  // [~Db db]: the function that frees a handle, one per handle type (spec 22)
  void LinkFreeingFunctions() {
    foreach (var f in funs.Values.SelectMany(list => list).ToList()) {
      var frees = f.Params.Where(p => p.Frees).ToList();
      if (frees.Count == 0) {
        continue;
      }
      if (frees.Count > 1) {
        throw Error("a function frees one handle: one parameter [~T name] (spec 22)", f.Node);
      }
      if (!handles.TryGetValue(frees[0].Type, out var handle)) {
        throw Error($"[~T name] marks the handle the function frees: {frees[0].Type} is not a handle (spec 22)", f.Node);
      }
      if (handle.Free != null) {
        throw Error($"the handle {DisplayName(handle.Name)} already has its freeing function, {handle.Free.Name}: one per handle type (spec 22)", f.Node);
      }
      handle.Free = f;
    }
  }

  // ---------------------------------------------------------------- the end of a handle

  // an owned handle ends: when it is not null its freeing function runs (its result ignored); a freeing function that takes more than the
  // handle is never called by itself (spec 22)
  string HandleEndCode(string type, string place) {
    var handle = handles[type];
    if (handle.Free is not FunSig free || free.Params.Count != 1) {
      return $"{place} = {handle.Invalid};";
    }
    string held = NewTemp();
    return $"if ({place} != {handle.Invalid}) {{ {CType(type)} {held} = {place}; {place} = {handle.Invalid}; (void){free.CName}({held}); }}";
  }

  static Node HandleDropNode(string name, Node at) =>
    Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(Synthetic("HandleDrop", at, name)));

  void EmitHandleDrop(Node s, StringBuilder sb, Scope scope, string pad) {
    // a local always owns its handle (it cannot borrow one: that would be a copy); only a plain parameter is lent
    if (scope.Find(s.Text!) is VarInfo held && IsHandle(held.Type) && !(held.IsParam && !held.Own)) {
      sb.Append(pad).Append(HandleEndCode(held.Type, held.CName)).Append('\n');
    }
  }

  // Db db = open(...), var db = open(...), Db db: a local that owns a handle ends with its scope
  bool IsHandleDecl(Node decl) {
    if (decl.Kind != "Decl" || handles.Count == 0) {
      return false;
    }
    var typeNode = decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind));
    if (typeNode is { Kind: "Type", Text: "var" }) {
      return decl.Children.FirstOrDefault(child => child.Kind == "Init")?.Children[0] is { Kind: "Call" } call
        && CalleesOf(call) is { Count: > 0 } callees && callees.All(f => IsHandle(f.Ret));
    }
    return typeNode != null && TryResolveType(typeNode) is string type && IsHandle(type);
  }

  // ---------------------------------------------------------------- giving and moving

  // what an owner receives (a declaration, an assignment, a return, a ->T or [~T] parameter): a new handle (a call), null, or ->x, which
  // moves it out of x. x alone would be a copy, which a handle cannot have (spec 22)
  string HandleGiven(Node n, string type, Scope scope, Node at) {
    var inner = n;
    while (inner.Kind == "Paren") {
      inner = inner.Children[0];
    }
    if (inner.Kind == "Null") {
      return handles[type].Invalid;
    }
    if (inner.Kind == "Gift") {
      return HandleMoveOut(inner.Children[0], type, scope, inner);
    }
    if (inner.Kind == "Copy") {
      throw Error("a handle cannot be copied, only moved with -> (spec 22)", at);
    }
    if (inner.Kind is "Name" or "Member" or "Index") {
      throw Error($"a handle cannot be copied, only moved: write ->{SourceText(inner)} (spec 22)", at);
    }
    var value = ExprOf(inner, scope, type);
    if (value.Type == "null") {
      return handles[type].Invalid;
    }
    if (value.Type != type) {
      throw Error($"expected a {DisplayName(type)}, not {DisplayName(value.Type)}", at);
    }
    return value.Code;
  }

  // ->x: the handle leaves x, which becomes null; ->f() is the result itself
  string HandleMoveOut(Node place, string type, Scope scope, Node at) {
    if (place.Kind is not ("Name" or "Member")) {
      var value = ExprOf(place, scope, type);
      if (value.Type != type) {
        throw Error($"expected a {DisplayName(type)}, not {DisplayName(value.Type)}", at);
      }
      return value.Code;
    }
    if (place.Kind == "Name" && scope.Find(place.Text!) is VarInfo { IsParam: true, Own: false }) {
      throw Error($"'{place.Text}' is lent to this function: only its owner moves it (a parameter ->{DisplayName(type)} {place.Text} takes it) (spec 22)", at);
    }
    var target = LValue(place, scope);
    if (target.Type != type) {
      throw Error($"expected a {DisplayName(type)}, not {DisplayName(target.Type)}", at);
    }
    if (target.IsConst) {
      throw Error("a const cannot be moved", at);
    }
    string held = NewTemp();
    return $"({{ {CType(type)} {held} = {target.Code}; {target.Code} = {handles[type].Invalid}; {held}; }})";
  }

  // the handle type of a place (a variable, a field), or null
  string? HandlePlaceType(Node n, Scope scope) {
    if (handles.Count == 0) {
      return null;
    }
    while (n.Kind == "Paren") {
      n = n.Children[0];
    }
    if (n.Kind == "Name") {
      if (scope.Find(n.Text!) is VarInfo variable) {
        return IsHandle(variable.Type) ? variable.Type : null;
      }
    } else if (n.Kind != "Member") {
      return null;
    }
    try {
      var target = LValue(n, scope);
      return IsHandle(target.Type) ? target.Type : null;
    } catch (CompileException) {
      return null;
    }
  }

  // ->x of a handle inside a larger expression (return ->r, ->res)
  Expr? HandleGift(Node gift, Scope scope) {
    if (HandlePlaceType(gift.Children[0], scope) is string type) {
      return new Expr(HandleMoveOut(gift.Children[0], type, scope, gift), type, true);
    }
    return null;
  }

  // ---------------------------------------------------------------- declarations, assignments, returns

  void EmitHandleDecl(Node s, string type, Node? init, StringBuilder sb, Scope scope, string pad, bool isConst) {
    if (isConst) {
      throw Error("a handle cannot be const: it is moved and freed (spec 22)", s);
    }
    string code = init == null ? handles[type].Invalid : HandleGiven(init.Children[0], type, scope, init);
    VarInfo variable;
    if (frames.Peek().Hoisted.TryGetValue(s.Text!, out var hoisted)) {
      variable = hoisted;
      sb.Append($"{pad}{hoisted.CName} = {code};\n");
    } else {
      variable = new VarInfo(type, "v_" + s.Text, false);
      sb.Append($"{pad}{CType(type)} {variable.CName} = {code};\n");
    }
    variable.Own = true;
    variable.IsLocal = currentFun != null && !fn!.IsMain;
    scope.Vars[s.Text!] = variable;
  }

  // x = open(...), x = ->y, x = null, this.db = ...: the old handle ends, then x owns the new one
  string? HandleAssignCode(Node s, Scope scope) {
    if (s.Text != "=" || HandlePlaceType(s.Children[0], scope) is not string type) {
      return null;
    }
    var targetNode = s.Children[0];
    if (targetNode.Kind == "Name" && scope.Find(targetNode.Text!) is VarInfo { IsParam: true, Own: false }) {
      throw Error($"'{targetNode.Text}' is lent to this function: it cannot be assigned (spec 22)", s);
    }
    var target = LValue(targetNode, scope);
    if (target.IsConst) {
      throw ConstWrite(target, targetNode, s);
    }
    string given = HandleGiven(s.Children[1], type, scope, s);
    string held = NewTemp();
    return $"({{ {CType(type)} {held} = {given}; {HandleEndCode(type, target.Code)} {target.Code} = {held}; }})";
  }

  // return open(...), return ->x, return null: a handle result is always given
  string? HandleReturnValue(Node s, Scope scope) {
    if (s.Children.Count != 1 || currentRet == null) {
      return null;
    }
    if (IsHandle(currentRet)) {
      return HandleGiven(s.Children[0], currentRet, scope, s);
    }
    if (IsMulti(currentRet) && TupleElems(currentRet).Any(elem => IsHandle(elem.Type)) && s.Children[0].Kind == "Tuple") {
      var elems = TupleElems(currentRet);
      var values = s.Children[0].Children;
      for (int index = 0; index < Math.Min(elems.Count, values.Count); index++) {
        var value = values[index].Kind == "Named" ? values[index].Children[0] : values[index];
        if (IsHandle(elems[index].Type) && value.Kind is "Name" or "Member" or "Index" or "Copy") {
          throw Error($"a handle cannot be copied, only moved: write ->{SourceText(value)} (spec 22)", value);
        }
      }
    }
    return null;
  }

  // ---------------------------------------------------------------- calls

  // a handle argument: a plain parameter lends a variable or a field; ->T name and [~T name] take it (the variable becomes null)
  string HandleArgCode(ParamSig param, Node arg, Scope scope) {
    var inner = arg;
    while (inner.Kind == "Paren") {
      inner = inner.Children[0];
    }
    if (param.Ref == 2) {
      // &Db db of a native: C writes the handle; what the place held ends first (spec 22)
      if (inner.Kind != "WritableAddressOf" || HandlePlaceType(inner.Children[0], scope) is not string placeType || placeType != param.Type) {
        throw Error($"'{param.Name}' is written by C: pass &x, a {DisplayName(param.Type)} variable (spec 22)", arg);
      }
      var place = LValue(inner.Children[0], scope);
      return $"({{ {HandleEndCode(param.Type, place.Code)} &({place.Code}); }})";
    }
    if (param.Frees) {
      if (inner.Kind is "Name" or "Member" && HandlePlaceType(inner, scope) is string) {
        return HandleMoveOut(inner, param.Type, scope, arg);   // close(db): db is null after the call (spec 22)
      }
      return HandleGiven(inner, param.Type, scope, arg);
    }
    if (param.IsHandleGift) {
      if (inner.Kind is "Name" or "Member" or "Index") {
        throw Error($"'{param.Name}' takes the handle: write ->{SourceText(inner)} (spec 22)", arg);
      }
      return HandleGiven(inner, param.Type, scope, arg);
    }
    if (inner.Kind == "Null") {
      return handles[param.Type].Invalid;
    }
    if (inner.Kind == "Gift") {
      throw Error($"'{param.Name}' only borrows the handle: pass it without -> (spec 22)", arg);
    }
    if (inner.Kind is not ("Name" or "Member" or "Index")) {
      throw Error($"a new handle given to '{param.Name}', which only borrows it, would never be freed: keep it in a variable first (spec 22)", arg);
    }
    var value = ExprOf(inner, scope, param.Type);
    if (value.Type != param.Type) {
      throw Error($"'{param.Name}' takes a {DisplayName(param.Type)}, not {DisplayName(value.Type)}", arg);
    }
    return value.Code;
  }

  // a new handle nobody keeps ends at once (spec 22)
  string? DroppedHandleCode(Expr e) {
    if (!IsHandle(e.Type)) {
      return null;
    }
    string held = NewTemp();
    return $"{{ {CType(e.Type)} {held} = {e.Code}; {HandleEndCode(e.Type, held)} }}";
  }

  // h == null, h != null, a == b (two handles of the same type): the values are compared (spec 22)
  Expr? HandleCompare(Node n, Scope scope) {
    if (n.Text is not ("==" or "!=") || handles.Count == 0) {
      return null;
    }
    var left = n.Children[0];
    var right = n.Children[1];
    string? leftType = HandlePlaceType(left, scope);
    string? rightType = HandlePlaceType(right, scope);
    if (leftType == null && rightType == null) {
      return null;
    }
    string type = (leftType ?? rightType)!;
    string Side(Node side, string? sideType) {
      if (side.Kind == "Null") {
        return handles[type].Invalid;
      }
      if (sideType == null) {
        var value = ExprOf(side, scope, type);
        if (value.Type == "null") {
          return handles[type].Invalid;
        }
        if (value.Type != type) {
          throw Error($"a {DisplayName(type)} is compared with null or with another {DisplayName(type)}, not with {DisplayName(value.Type)} (spec 22)", n);
        }
        return value.Code;
      }
      if (sideType != type) {
        throw Error($"a {DisplayName(type)} is compared with null or with another {DisplayName(type)}, not with {DisplayName(sideType)} (spec 22)", n);
      }
      return ExprOf(side, scope, type).Code;
    }
    return new Expr($"({Side(left, leftType)} {n.Text} {Side(right, rightType)})", "bool");
  }

  // the types that cannot hold a handle yet
  void CheckHandleHolder(string type, Node at) {
    if (handles.Count == 0) {
      return;
    }
    bool holds = IsTuple(type) && TupleElems(type).Any(elem => IsHandle(elem.Type))
      || type.Contains('<') && handles.Keys.Any(handle => System.Text.RegularExpressions.Regex.IsMatch(type, $@"[<,]\s*{System.Text.RegularExpressions.Regex.Escape(handle)}\s*[,>]"));
    if (holds) {
      throw NotSupported("a handle inside a tuple or a collection (spec 22)", at);
    }
  }

  static string SourceText(Node n) => n.Kind switch {
    "Name" => n.Text!,
    "Member" when n.Children.Count > 0 => SourceText(n.Children[0]) + "." + n.Text,
    "Paren" => SourceText(n.Children[0]),
    _ => "x"
  };
}
