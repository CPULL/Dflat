using dfparser;

namespace dfcompiler;

// string? (spec 7.1): a string where null is not "". In C a df_opt_string { has, val } of the runtime.
// A string? variable always owns its string (like a field): a borrowed value is copied into it, the old string ends when it is
// assigned again, and the string ends with the scope. A string? parameter borrows (read-only). A function gives its caller a
// string? the caller owns; a new string? that is only read (an operand, an argument) ends with the statement (df_opt_str_tmp).
// A string? field owns its string the same way; the end of its object frees it, a clone copies it.
public sealed partial class CodeGen {
  // a new string? that is only read here: its string ends at the end of the statement
  Expr OptStrRead(Expr e) {
    if (e.Type != "string?" || !e.Fresh || fn == null) {
      return e;
    }
    fn.UsesTmp = true;
    return new Expr($"df_opt_str_tmp({e.Code})", e.Type);
  }

  // the value for a string? variable or a returned string?: a string? its receiver owns
  string OptStrOwned(Expr value, Node at) {
    switch (value.Type) {
      case "null":
        return "((df_opt_string){0})";
      case "string?":
        return value.Fresh ? value.Code : $"df_opt_str_dup({value.Code})";
      case "string": {
        string converted = Convert(value, "string", at);
        return $"((df_opt_string){{ true, {StrOwned(value, converted)} }})";
      }
      default:
        throw Error($"cannot convert {value.Type} to string?", at);
    }
  }

  // the value of a string? parameter: a borrow, which the call does not keep
  string OptStrBorrowed(Expr value, Node at) {
    switch (value.Type) {
      case "null":
        return "((df_opt_string){0})";
      case "string?":
        return OptStrRead(value).Code;
      case "string": {
        string converted = Convert(value, "string", at);
        bool fresh = FreshCode(value, converted) == "true" && !IsStaticStr(converted);
        return $"((df_opt_string){{ true, {StrRead(new Expr(converted, "string", fresh))} }})";
      }
      default:
        throw Error($"cannot convert {value.Type} to string?", at);
    }
  }

  // a string? declared with its type written (string? x): its string ends with the scope
  static bool IsOptStringDecl(Node decl) =>
    decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { Kind: "Nullable" } nullable
    && nullable.Children.FirstOrDefault() is { Kind: "Type", Text: "string", Children.Count: 0 };

  // x = value and x ??= value on a string? variable
  string OptStrAssign(LValueInfo target, Node targetNode, string op, Node valueNode, Scope scope, Node at) {
    if (target.Variable is { IsParam: true } or { ReadOnlyString: true }) {
      throw Error($"'{targetNode.Text}' is a read-only string? (a parameter or a loop variable, spec 7.1)", at);
    }
    MarkAssigned(target.Variable);
    var value = ExprOf(valueNode, scope, "string?");
    string set = $"df_opt_str_set(&{target.Code}, {OptStrOwned(value, valueNode)})";
    return op switch {
      "=" => set,
      "??=" => $"({target.Code}.has ? (void)0 : {set})",
      _ => throw Error($"'{op}' is not defined on string?: unwrap it with '??' or 'enforce' first (spec 7.1)", at)
    };
  }

  // return x from a function that returns string?: a local moves out, anything else is copied
  string OptStrReturned(Expr value, Node expression, Scope scope, Node at) {
    if (value.Type == "string?" && expression.Kind == "Name" && scope.Find(expression.Text!) is VarInfo { IsParam: false, Type: "string?" } local) {
      string taken = NewTemp();
      return $"({{ df_opt_string {taken} = {local.CName}; {local.CName} = (df_opt_string){{0}}; {taken}; }})";
    }
    return OptStrOwned(value, at);
  }

  // a ?? b with a string? on the left: a new string (or string? when b may be null) that the receiver owns
  Expr OptStrCoalesce(Expr left, Node n, Scope scope) {
    var right = ExprOf(n.Children[1], scope, "string");
    if (right.Type is "string?" or "null") {
      string rightOwned = OptStrOwned(right, n.Children[1]);
      return new Expr(WithTemp(left, temp => $"({temp}.has ? df_opt_str_dup({temp}) : {rightOwned})"), "string?", true);
    }
    string fallback = StrOwnedAs(right, "string", Convert(right, "string", n));
    return new Expr(WithTemp(left, temp => $"({temp}.has ? df_str_dup({temp}.val) : {fallback})"), "string", true);
  }
}
