using System.Text;
using dfparser;

namespace dfcompiler;

// Nullables: T? values, null propagation, ?? ??= ?. and enforce.
// A T? is a C struct { bool has; T val; }. Strings have no separate null: "" and null are the same.
public sealed partial class CodeGen {
  // typed null for the other side of an operation: x + null → (x's type)? with no value
  Expr TypedNull(string otherType, Node at) {
    if (otherType == "null") {
      throw Error("cannot infer a type from null alone", at);
    }
    string type = BaseOf(otherType) + "?";
    return new Expr($"(({CType(type)}){{0}})", type);
  }

  // f(value) on a T?: null stays null
  Expr LiftUnary(Expr e, Func<Expr, Expr> apply) {
    string resultType = "";
    string code = WithTemp(e, temp => {
      var inner = apply(new Expr(temp + ".val", BaseOf(e.Type)));
      resultType = IsNullable(inner.Type) ? inner.Type : inner.Type + "?";
      string cType = CType(resultType);
      string value = IsNullable(inner.Type) ? inner.Code : $"(({cType}){{ true, {inner.Code} }})";
      return $"({temp}.has ? {value} : (({cType}){{0}}))";
    });
    return new Expr(code, resultType);
  }

  // arithmetic and bitwise operators: null in, null out
  Expr LiftBinary(string op, Expr l, Expr r, Node at) {
    if (l.Type == "null") {
      l = TypedNull(r.Type, at);
    }
    if (r.Type == "null") {
      r = TypedNull(l.Type, at);
    }
    bool leftNullable = IsNullable(l.Type);
    bool rightNullable = IsNullable(r.Type);
    string resultType = "";
    string code = WithTemp(l, leftTemp => WithTemp(r, rightTemp => {
      var leftValue = leftNullable ? new Expr(leftTemp + ".val", BaseOf(l.Type)) : new Expr(leftTemp, l.Type);
      var rightValue = rightNullable ? new Expr(rightTemp + ".val", BaseOf(r.Type)) : new Expr(rightTemp, r.Type);
      var inner = Combine(op, leftValue, rightValue, at);
      resultType = inner.Type + "?";
      string cType = CType(resultType);
      var checks = new List<string>();
      if (leftNullable) {
        checks.Add(leftTemp + ".has");
      }
      if (rightNullable) {
        checks.Add(rightTemp + ".has");
      }
      return $"(({string.Join(" && ", checks)}) ? (({cType}){{ true, {inner.Code} }}) : (({cType}){{0}}))";
    }));
    return new Expr(code, resultType);
  }

  // < <= > >= with nullables: null sorts first
  Expr OrderNullable(string op, Expr l, Expr r, Node at) {
    if (l.Type == "null") {
      l = TypedNull(r.Type, at);
    }
    if (r.Type == "null") {
      r = TypedNull(l.Type, at);
    }
    bool leftNullable = IsNullable(l.Type);
    bool rightNullable = IsNullable(r.Type);
    string code = WithTemp(l, leftTemp => WithTemp(r, rightTemp => {
      var leftValue = leftNullable ? new Expr(leftTemp + ".val", BaseOf(l.Type)) : new Expr(leftTemp, l.Type);
      var rightValue = rightNullable ? new Expr(rightTemp + ".val", BaseOf(r.Type)) : new Expr(rightTemp, r.Type);
      string compare = Combine(op, leftValue, rightValue, at).Code;
      string leftHas = leftNullable ? leftTemp + ".has" : "true";
      string rightHas = rightNullable ? rightTemp + ".has" : "true";
      return op switch {
        "<" => $"(!{leftHas} ? {rightHas} : ({rightHas} && {compare}))",
        "<=" => $"(!{leftHas} || ({rightHas} && {compare}))",
        ">" => $"(!{rightHas} ? {leftHas} : ({leftHas} && {compare}))",
        _ => $"(!{rightHas} || ({leftHas} && {compare}))"
      };
    }));
    return new Expr(code, "bool");
  }

  // a ?? b: the right side is evaluated only when a is null
  Expr Coalesce(Node n, Scope scope, string? expected) {
    var left = ExprOf(n.Children[0], scope, NumericContext(expected));
    if (left.Type == "null") {
      return ExprOf(n.Children[1], scope, expected);
    }
    if (left.Type == "string") {
      string fallback = Convert(ExprOf(n.Children[1], scope, "string"), "string", n);
      return new Expr(WithTemp(left, temp => $"({temp}.len != 0 ? {temp} : {fallback})"), "string");
    }
    if (!IsNullable(left.Type)) {
      throw Error($"the left side of '??' is {left.Type}, which is never null", n);
    }
    string leftBase = BaseOf(left.Type);
    var right = ExprOf(n.Children[1], scope, leftBase);
    bool rightMayBeNull = IsNullable(right.Type) || right.Type == "null";
    string rightBase = right.Type == "null" ? leftBase : BaseOf(right.Type);
    string resultBase = Assignable(leftBase, rightBase) ? leftBase
      : Assignable(rightBase, leftBase) ? rightBase
      : throw Error($"'??' needs compatible sides, not {left.Type} and {right.Type}", n);
    string resultType = rightMayBeNull ? resultBase + "?" : resultBase;
    string rightCode = Convert(right, resultType, n);
    string code = WithTemp(left, temp => {
      string leftValue = rightMayBeNull
        ? Convert(new Expr(temp, left.Type), resultType, n)
        : Convert(new Expr(temp + ".val", leftBase), resultType, n);
      return $"({temp}.has ? {leftValue} : {rightCode})";
    });
    return new Expr(code, resultType);
  }

  // a ??= b: assigns only when a is null
  string CoalesceAssignCode(LValueInfo target, Node valueNode, Scope scope, Node at) {
    if (IsNullable(target.Type)) {
      string value = Convert(ExprOf(valueNode, scope, BaseOf(target.Type)), target.Type, at);
      return $"({target.Code}.has ? (void)0 : (void)({target.Code} = {value}))";
    }
    if (target.Type == "string") {
      string value = Convert(ExprOf(valueNode, scope, "string"), "string", at);
      return $"({target.Code}.len != 0 ? (void)0 : (void)({target.Code} = {value}))";
    }
    throw Error($"'??=' needs a nullable target, not {target.Type}", at);
  }

  // x?.member and x?.method(args): null when x is null.
  // outer is the SafeMember node itself or the Call node around it.
  Expr SafeAccess(Node safeNode, Node outer, Scope scope) {
    var target = ExprOf(safeNode.Children[0], scope);
    if (target.Type is "null" or "void" || IsMulti(target.Type)) {
      throw Error("'?.' needs a value", safeNode);
    }
    bool canBeNull = IsNullable(target.Type) || target.Type == "string";
    string valueType = BaseOf(target.Type);

    // evaluate "hidden.member" with hidden bound to the unwrapped value; '?' keeps it out of Dflat names
    tempCounter++;
    string hidden = "?safe" + tempCounter;
    var member = Synthetic("Member", safeNode, safeNode.Text).Add(Synthetic("Name", safeNode, hidden));
    var rebuilt = outer == safeNode ? member : Synthetic("Call", outer).Add(member).Add(outer.Children[1]);

    string resultType = "";
    string code = WithTemp(target, temp => {
      var innerScope = new Scope(scope);
      innerScope.Vars[hidden] = new VarInfo(valueType, IsNullable(target.Type) ? temp + ".val" : temp, true);
      var inner = ExprOf(rebuilt, innerScope);
      if (!canBeNull) {
        resultType = inner.Type;
        return inner.Code;
      }
      if (inner.Type == "void" || IsMulti(inner.Type)) {
        throw Error("'?.' needs a member with a single value", safeNode);
      }
      resultType = inner.Type == "string" || IsNullable(inner.Type) ? inner.Type : inner.Type + "?";
      string has = IsNullable(target.Type) ? $"{temp}.has" : $"{temp}.len != 0";
      string whenNull = Convert(new Expr("0", "null"), resultType, safeNode);
      return $"(({has}) ? {Convert(inner, resultType, safeNode)} : {whenNull})";
    });
    return new Expr(code, resultType);
  }

  // enforce a, b { a and b are plain values here } else { }
  void EmitEnforce(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var block = s.Children.First(child => child.Kind == "Block");
    var elseNode = s.Children.FirstOrDefault(child => child.Kind == "Else");
    var conditions = new List<string>();
    var unwrapped = new Scope(scope);
    foreach (var name in s.Children.Where(child => child.Kind == "Name")) {
      var variable = scope.Find(name.Text!) ?? throw Error($"unknown variable '{name.Text}'", name);
      if (IsNullable(variable.Type)) {
        conditions.Add($"{variable.CName}.has");
        unwrapped.Vars[name.Text!] = new VarInfo(BaseOf(variable.Type), variable.CName + ".val", variable.IsConst);
      } else if (variable.Type == "string") {
        throw Error($"strings cannot be enforced ('{name.Text}')", name);
      } else {
        throw Error($"'{name.Text}' is {variable.Type}, which is never null", name);
      }
    }
    sb.Append($"{pad}if ({string.Join(" && ", conditions)}) {{\n");
    EmitBlock(block, sb, unwrapped, indent + 1);
    sb.Append(pad).Append('}');
    if (elseNode != null) {
      sb.Append(" else {\n");
      EmitBlock(elseNode.Children[0], sb, scope, indent + 1);
      sb.Append(pad).Append('}');
    }
    sb.Append('\n');
  }
}
