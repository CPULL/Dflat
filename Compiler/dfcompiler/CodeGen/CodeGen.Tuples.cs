using System.Text;
using dfparser;

namespace dfcompiler;

// Tuples, multiple return values, multiple assignment and swap.
// A tuple is a C struct { e0, e1, ... }; a function with several return values returns the same kind of struct.
public sealed partial class CodeGen {
  // (a, b, ...): elements are evaluated left to right
  Expr TupleLiteral(Node n, Scope scope, string? expected) {
    List<(string Type, string? Name)>? expectedElems = null;
    if (expected != null && IsTuple(BaseOf(expected))) {
      var candidate = TupleElems(BaseOf(expected));
      if (candidate.Count == n.Children.Count) {
        expectedElems = candidate;
      }
    }
    var values = n.Children.Select((child, index) => ExprOf(child, scope, expectedElems?[index].Type)).ToList();
    List<(string Type, string? Name)> elems;
    if (expectedElems != null && values.Zip(expectedElems).All(pair => Assignable(pair.Second.Type, pair.First.Type))) {
      elems = expectedElems;
    } else {
      elems = new List<(string Type, string? Name)>();
      foreach (var (value, index) in values.Select((value, index) => (value, index))) {
        if (value.Type == "null") {
          throw Error("cannot infer the type of null in a tuple; declare the tuple type", n.Children[index]);
        }
        if (value.Type == "void" || IsMulti(value.Type)) {
          throw Error("a tuple element needs a single value", n.Children[index]);
        }
        elems.Add((value.Type, null));
      }
    }
    string tupleType = MakeTuple(elems);
    var declarations = new StringBuilder();
    var temps = new List<string>();
    for (int index = 0; index < values.Count; index++) {
      string temp = NewTemp();
      temps.Add(temp);
      string code = StrOwnedAs(values[index], elems[index].Type, Convert(values[index], elems[index].Type, n.Children[index]));
      declarations.Append($"{CType(elems[index].Type)} {temp} = {code}; ");
    }
    return new Expr($"({{ {declarations}({CType(tupleType)}){{ {string.Join(", ", temps)} }}; }})", tupleType);
  }

  // return a, b: the values are computed left to right into temporaries
  string MultiReturnCode(Node s, Scope scope, StringBuilder sb, string pad) {
    var elems = TupleElems(currentRet!);
    if (s.Children.Count == 1) {
      // forwarding another call with the same return values
      var single = ExprOf(s.Children[0], scope);
      if (single.Type != currentRet) {
        throw Error($"this function returns {elems.Count} values", s);
      }
      return single.Code;
    }
    if (s.Children.Count != elems.Count) {
      throw Error($"this function returns {elems.Count} values, not {s.Children.Count}", s);
    }
    var temps = new List<string>();
    for (int index = 0; index < elems.Count; index++) {
      string temp = NewTemp();
      temps.Add(temp);
      var value = ExprOf(s.Children[index], scope, elems[index].Type);
      if (elems[index].Type == "string") {
        value = StrReturned(value, s.Children[index], scope);
      }
      sb.Append($"{pad}{CType(elems[index].Type)} {temp} = {Convert(value, elems[index].Type, s.Children[index])};\n");
    }
    return $"(({CType(currentRet!)}){{ {string.Join(", ", temps)} }})";
  }

  // i32 id, string name = getUser()   |   x, y = y, x   |   existing, _, i32 fresh = f()
  // Every value is evaluated before any target is written.
  void EmitMultiAssign(Node s, StringBuilder sb, Scope scope, string pad) {
    var targets = s.Children[0].Children;
    var values = s.Children[1].Children;
    var resolved = new List<Expr?>();

    if (values.Count == 1) {
      var value = ExprOf(values[0], scope);
      if (!IsMulti(value.Type)) {
        if (IsTuple(value.Type)) {
          throw Error("tuple destructuring on the left is not supported yet (open item in the spec)", s);
        }
        throw Error($"{targets.Count} targets but only one value", s);
      }
      var elems = TupleElems(value.Type);
      if (elems.Count != targets.Count) {
        throw Error($"the call returns {elems.Count} values, not {targets.Count}", s);
      }
      string temp = NewTemp();
      sb.Append($"{pad}{CType(value.Type)} {temp} = {value.Code};\n");
      for (int index = 0; index < elems.Count; index++) {
        resolved.Add(new Expr($"{temp}.e{index}", elems[index].Type, true));
      }
    } else {
      if (values.Count != targets.Count) {
        throw Error($"{targets.Count} targets but {values.Count} values", s);
      }
      for (int index = 0; index < values.Count; index++) {
        string? targetType = TargetType(targets[index], scope);
        var value = ExprOf(values[index], scope, targetType);
        if (IsMulti(value.Type)) {
          throw Error("a call with several return values must be the only value", values[index]);
        }
        if (targets[index].Kind == "Discard") {
          if (value.Type != "null") {
            sb.Append($"{pad}(void)({value.Code});\n");
          }
          resolved.Add(null);
          continue;
        }
        string tempType = targetType ?? value.Type;
        string temp = NewTemp();
        string converted = Convert(value, tempType, values[index]);
        sb.Append($"{pad}{CType(tempType)} {temp} = {converted};\n");
        if (tempType == "string") {
          string fresh = FreshCode(value, converted);
          if (values[index].Kind == "Name" && scope.Find(values[index].Text!) is VarInfo { OwnFlag: string sourceFlag }) {
            fresh = NewTemp();
            sb.Append($"{pad}bool {fresh} = {sourceFlag};\n{pad}{sourceFlag} = false;\n");
          }
          stringFresh[temp] = fresh;
        }
        resolved.Add(new Expr(temp, tempType));
      }
    }

    for (int index = 0; index < targets.Count; index++) {
      AssignTarget(targets[index], resolved[index], sb, scope, pad);
    }
    stringFresh.Clear();
  }

  // the temporaries of a multiple assignment that hold a string: C for "the string is new"
  readonly Dictionary<string, string> stringFresh = new();

  string TargetFresh(Expr value) =>
    stringFresh.TryGetValue(value.Code, out var fresh) ? fresh : value.Fresh || value.Type != "string" ? "true" : "false";

  string? TargetType(Node target, Scope scope) {
    if (target.Kind == "Discard") {
      return null;
    }
    if (target.Kind == "Decl") {
      return ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind)));
    }
    if (PropertyTarget(target, scope) is var (_, property)) {
      return property.Type;   // set through its W: (AssignTarget)
    }
    return LValue(target, scope).Type;
  }

  void AssignTarget(Node target, Expr? value, StringBuilder sb, Scope scope, string pad) {
    if (target.Kind == "Discard" || value == null) {
      return;
    }
    if (PropertyTarget(target, scope) is var (propertyObj, property)) {
      sb.Append(pad).Append(PropertySet(propertyObj, property, CValue(value.Value, target), scope, target).Code).Append(";\n");   // the setter
      return;
    }
    if (target.Kind == "Decl") {
      string type = ResolveType(target.Children.First(child => TypeNodeKinds.Contains(child.Kind)));
      if (scope.Vars.ContainsKey(target.Text!)) {
        throw Error($"'{target.Text}' is already declared in this scope", target);
      }
      CheckNotShadowing(target.Text!, scope, target);
      if (frames.Peek().Hoisted.TryGetValue(target.Text!, out var hoisted)) {
        scope.Vars[target.Text!] = hoisted;
        MarkAssigned(hoisted);
        sb.Append($"{pad}{hoisted.CName} = {Convert(value.Value, type, target)};\n");
        if (hoisted.OwnFlag != null) {
          sb.Append($"{pad}{hoisted.OwnFlag} = {TargetFresh(value.Value)};\n");
        }
        return;
      }
      string cname = "v_" + target.Text;
      sb.Append($"{pad}{CType(type)} {cname} = {Convert(value.Value, type, target)};\n");
      var declared = StringVar(new VarInfo(type, cname, false));
      if (declared.OwnFlag != null) {
        sb.Append($"{pad}bool {declared.OwnFlag} = {TargetFresh(value.Value)};\n{pad}(void){declared.OwnFlag};\n");
      }
      scope.Vars[target.Text!] = declared;
      return;
    }
    var lvalue = LValue(target, scope);
    if (lvalue.IsConst) {
      throw Error($"'{target.Text}' is const", target);
    }
    MarkAssigned(lvalue.Variable);
    if (lvalue.Type == "string") {
      sb.Append($"{pad}{StrAssign(lvalue, TargetFresh(value.Value), Convert(value.Value, lvalue.Type, target))};\n");
      return;
    }
    sb.Append($"{pad}{lvalue.Code} = {Convert(value.Value, lvalue.Type, target)};\n");
  }
}
