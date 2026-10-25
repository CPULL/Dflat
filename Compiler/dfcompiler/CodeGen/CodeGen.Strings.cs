using System.Text;
using dfparser;

namespace dfcompiler;

// Ownership of strings (spec 4, 4.4, 12).
//
// A string variable (a local or a parameter) has a C flag, o_<name>, that says whether it owns the string it holds: a new string
// makes it the owner, a variable, a field or an item makes it a borrower. The owner frees the string when the variable is assigned
// again or when its scope ends (an implicit atEnd, so every exit frees it); a borrower never frees.
// Fields, items and tuple items always own their string: a borrowed value is copied when it is stored there. A function always
// returns a string its caller owns: an owned local moves out, anything else is copied.
// A new string that is only read (an operand, an argument) is a temporary: df_tmp keeps it and the end of the statement frees it
// (df_tmp_drop down to the mark the function took when it started).
public sealed partial class CodeGen {
  // the expression gives a new string (the receiver owns it): a call, a +, an interpolation, a copy; a literal is never freed, so it
  // counts as new. A variable, a field and an item are borrowed.
  static bool StrNodeFresh(Node n, Expr e) => n.Kind switch {
    "String" or "Interp" or "Copy" => true,
    "Call" => n.Children[0] is { Kind: "Member", Text: "toStr" } ? e.Fresh : true,
    "Binary" => n.Text == "+" || (n.Text == "??" && e.Fresh),
    "Paren" or "Gift" => e.Fresh,
    _ => false
  };

  // code that is always a literal of the program or the empty string: nothing to free
  static bool IsStaticStr(string code) =>
    code.StartsWith("((df_string)&df_lit") || code == "((df_string)0)" || code == "df_str_lit(\"\", 0)";

  // a string that is only read here: a new one is freed at the end of the statement
  string StrRead(Expr e) {
    if (e.Type != "string" || !e.Fresh || IsStaticStr(e.Code)) {
      return e.Code;
    }
    fn!.UsesTmp = true;
    return $"df_tmp({e.Code})";
  }

  Expr StrReadExpr(Expr e) => e.Type == "string" && e.Fresh ? new Expr(StrRead(e), e.Type) : e;

  // the text of any value, only read here (a conversion makes a new string)
  string StrReadOf(Expr e, Node at) {
    string code = StrOf(e, at);
    bool fresh = e.Type == "string" ? e.Fresh : true;
    return StrRead(new Expr(code, "string", fresh));
  }

  // the value for a place that always owns its string (a field, an item, a tuple item, a returned value)
  static string StrOwned(Expr e, string code) =>
    e.Fresh || IsStaticStr(code) ? code : $"df_str_dup({code})";

  // the flag of a string variable
  static string OwnFlagOf(string cname) => "o_" + cname.Replace("(", "").Replace(")", "").Replace("*", "");

  static VarInfo StringVar(VarInfo variable) {
    if (variable.Type == "string" && !variable.CName.StartsWith("(*")) {
      variable.OwnFlag = OwnFlagOf(variable.CName);
    }
    return variable;
  }

  // C for "the value is new": a value of another type converted to a string is new too
  static string FreshCode(Expr value, string converted) =>
    value.Type != "string" || value.Fresh || IsStaticStr(converted) ? "true" : "false";

  // v = value for a string variable, field, tuple item or reference parameter; fresh is C code ("true", "false" or a flag)
  string StrAssign(LValueInfo target, string fresh, string converted) {
    if (target.Variable?.OwnFlag is string flag) {
      return $"df_str_set(&{target.Code}, &{flag}, {converted}, {fresh})";
    }
    if (target.IsField) {
      return $"df_str_put(&{target.Code}, {converted}, {fresh})";
    }
    // a tuple item, a static field or a reference parameter: it gets a string of its own, the old one is left alone
    return fresh == "true" ? $"{target.Code} = {converted}" : $"{target.Code} = df_str_own({converted}, {fresh})";
  }

  // the value of an item of a collection (spec 4): its own copy of the string, in its smallest form
  static string StrItemAs(Expr value, string elemType, string converted) =>
    elemType != "string" || IsStaticStr(converted) ? converted : $"df_str_item({converted}, {FreshCode(value, converted)})";

  // ->x on a string: the string leaves the variable or the field with its ownership (a borrowed one is copied). Null when the operand
  // is not a string.
  Expr? StrGift(Node operand, Scope scope, Node at) {
    if (PlaceType(operand, scope) != "string" || operand.Kind == "Index") {
      return null;
    }
    var place = LValue(operand, scope);
    if (place.IsConst || place.Variable is { ReadOnlyString: true }) {
      throw Error($"'{operand.Text}' does not own its string: it cannot be moved with ->", at);
    }
    MarkAssigned(place.Variable);
    string taken = NewTemp();
    string flag = place.Variable?.OwnFlag is string own ? own : null!;
    string code = flag != null
      ? $"({{ df_string {taken} = {place.Code}; if (!{flag}) {{ {taken} = df_str_dup({taken}); }} {place.Code} = NULL; {flag} = false; {taken}; }})"
      : place.IsField
        ? $"({{ df_string {taken} = {place.Code}; {place.Code} = NULL; {taken}; }})"
        : $"df_str_dup({place.Code})";
    return new Expr(code, "string", true);
  }

  // the value of an item or a tuple item of type string: a string of its own
  static string StrOwnedAs(Expr value, string targetType, string converted) =>
    targetType != "string" || FreshCode(value, converted) == "true" ? converted : $"df_str_dup({converted})";

  // the end of a string variable (its implicit atEnd)
  static Node StrDropNode(string name, Node at) {
    var statement = Synthetic("StrDrop", at, name);
    return Synthetic("AtEnd", at, "drop").Add(Synthetic("Block", at).Add(statement));
  }

  void EmitStrDrop(Node s, StringBuilder sb, Scope scope, string pad) {
    if (scope.Find(s.Text!) is VarInfo { OwnFlag: string flag } variable) {
      sb.Append($"{pad}df_str_drop(&{variable.CName}, &{flag});\n");
    }
  }

  // a declaration of a string with its type written (string x, const string x): its string ends with the scope
  static bool IsStringDecl(Node decl) =>
    decl.Children.FirstOrDefault(child => TypeNodeKinds.Contains(child.Kind)) is { Kind: "Type" or "ResolvedType", Text: "string", Children.Count: 0 };

  // return x for a function that returns a string: the caller owns what it gets
  Expr StrReturned(Expr value, Node expression, Scope scope) {
    if (value.Type != "string") {
      return value;
    }
    if (expression.Kind == "Name" && scope.Find(expression.Text!) is VarInfo { OwnFlag: string flag } variable) {
      string taken = NewTemp();
      return new Expr($"({{ df_string {taken} = {variable.CName}; if (!{flag}) {{ {taken} = df_str_dup({taken}); }} {flag} = false; {taken}; }})", "string", true);
    }
    return new Expr(StrOwned(value, value.Code), "string", true);
  }

  // ---------------------------------------------------------------- changes in place (spec 4.1)

  // the methods of string that give a changed copy; in place when the result is not used or goes back to the same string
  static readonly HashSet<string> StrChanging = new() {
    "trim", "trimLeft", "trimRight", "substr", "replace", "toLower", "toUpper", "padLeft", "padRight",
    "insert", "remove", "replaceAt", "append", "prepend"
  };

  // s.trim().toUpper(): the string the chain starts from (the receiver of the first changing method), or null when the outer call
  // is not a changing method of string
  static Node? ChangedRoot(Node call) {
    if (call.Kind != "Call" || call.Children[0] is not { Kind: "Member" } member || !StrChanging.Contains(member.Text!)) {
      return null;
    }
    var receiver = member.Children[0];
    return ChangedRoot(receiver) ?? receiver;
  }

  // the type of a place without compiling it (null when it is not a variable, a field or an array item)
  string? PlaceType(Node target, Scope scope) {
    try {
      if (target.Kind is "Name" or "Member") {
        return LValue(target, scope).Type;
      }
      if (target.Kind == "Index" && target.Children[0] is { Kind: "Name" } array && scope.Find(array.Text!) is VarInfo held && IsArr(held.Type)) {
        return ArrElem(held.Type);
      }
    } catch (CompileException) {
    }
    return null;
  }

  // the C address of a string place and of its ownership flag; null when the place cannot be changed in place here
  (string Slot, string Flag)? StrPlace(Node target, Scope scope, Node at) {
    if (PlaceType(target, scope) != "string") {
      return null;
    }
    if (target.Kind == "Index") {
      return (WritableAddress(target, scope, "string", at), "&df_str_owner");
    }
    var place = LValue(target, scope);
    if (place.IsConst) {
      throw Error($"'{target.Text}' is constant: it cannot be changed in place (spec 4.1)", at);
    }
    if (place.Variable is { ReadOnlyString: true }) {
      throw Error($"'{target.Text}' is a read-only borrow (a parameter or a loop variable): its string cannot be changed in place (spec 4.1)", at);
    }
    MarkAssigned(place.Variable);
    return ($"&{place.Code}", place.Variable?.OwnFlag is string flag ? $"&{flag}" : "&df_str_owner");
  }

  // the same place written twice: x and x, a.b and a.b, or a variable declared as a borrow of the other (string b = x)
  static bool SamePlace(Node a, Node b, Scope scope) {
    if (a.Kind == "Name" && b.Kind == "Name") {
      if (a.Text == b.Text) {
        return true;
      }
      var first = scope.Find(a.Text!);
      var second = scope.Find(b.Text!);
      return first != null && second != null && (first.AliasOf == second || second.AliasOf == first);
    }
    return a.Kind == "Member" && b.Kind == "Member" && a.Text == b.Text && SamePlace(a.Children[0], b.Children[0], scope);
  }

  // s.trim() alone: the string s takes the result, in place. False when the statement is not that.
  bool EmitInPlaceStatement(Node call, StringBuilder sb, Scope scope, string pad) {
    if (StrInPlaceCall(call, scope) is string direct) {
      sb.Append(pad).Append(direct).Append(";\n");
      return true;
    }
    var root = ChangedRoot(call);
    if (root == null || StrPlace(root, scope, call) is not var (slot, flag)) {
      return false;
    }
    if (DirectEdit(call, root, slot, flag, scope) is string edit) {
      sb.Append($"{pad}{edit};\n");
      return true;
    }
    var value = ExprOf(call, scope);
    sb.Append($"{pad}df_str_take({slot}, {flag}, {value.Code});\n");
    return true;
  }

  // s.insert(p, t), s.remove(p, n), s.replaceAt(p, n, t), s.append(t), s.prepend(t) on the place itself: edited where it is (a block
  // string changes only the blocks touched). Null for a chain or named arguments.
  string? DirectEdit(Node call, Node root, string slot, string flag, Scope scope) {
    var member = call.Children[0];
    var args = call.Children[1].Children;
    if (member.Children[0] != root || args.Any(arg => arg.Kind == "Named")) {
      return null;
    }
    string Pos(int index) => Convert(ExprOf(args[index], scope, "u32"), "u32", args[index]);
    string Text(int index) => StrReadOf(ExprOf(args[index], scope, "string"), args[index]);
    return (member.Text, args.Count) switch {
      ("insert", 2) => $"df_str_splice_in({slot}, {flag}, {Pos(0)}, 0, {Text(1)})",
      ("remove", 2) => $"df_str_splice_in({slot}, {flag}, {Pos(0)}, {Pos(1)}, NULL)",
      ("replaceAt", 3) => $"df_str_splice_in({slot}, {flag}, {Pos(0)}, {Pos(1)}, {Text(2)})",
      ("append", 1) => $"df_str_append({slot}, {flag}, {Text(0)})",
      ("prepend", 1) => $"df_str_splice_in({slot}, {flag}, 0, 0, {Text(0)})",
      _ => null
    };
  }

  // x = x.trim(): x takes the result in place. Null when the assignment is not that.
  string? InPlaceAssign(Node targetNode, Node valueNode, Scope scope, Node at) {
    var root = ChangedRoot(valueNode);
    if (root == null || !SamePlace(root, targetNode, scope) || StrPlace(targetNode, scope, at) is not var (slot, flag)) {
      return null;
    }
    if (root.Kind == targetNode.Kind && root.Text == targetNode.Text && DirectEdit(valueNode, root, slot, flag, scope) is string direct) {
      return direct;
    }
    var value = ExprOf(valueNode, scope);
    return $"df_str_take({slot}, {flag}, {value.Code})";
  }

  // s += x, always in place
  string? StrAppendAssign(Node targetNode, Node valueNode, Scope scope, Node at) {
    if (StrPlace(targetNode, scope, at) is not var (slot, flag)) {
      return null;
    }
    var value = ExprOf(valueNode, scope);
    return $"df_str_append({slot}, {flag}, {StrReadOf(value, at)})";
  }

  // s.clear() and s.shrink(): only on a string place, always in place. Null when the call is not one of them.
  string? StrInPlaceCall(Node call, Scope scope) {
    if (call.Kind != "Call" || call.Children[0] is not { Kind: "Member", Text: "clear" or "shrink" } member) {
      return null;
    }
    var receiver = member.Children[0];
    if (PlaceType(receiver, scope) != "string") {
      return null;
    }
    if (call.Children[1].Children.Count > 0) {
      throw Error($"{member.Text}() takes no arguments", call);
    }
    var (slot, flag) = StrPlace(receiver, scope, call)!.Value;
    return member.Text == "clear" ? $"df_str_clear({slot}, {flag})" : $"df_str_shrink({slot})";
  }

  // text[pos] = c
  string StrSetChar(Node target, Node valueNode, string op, Scope scope, Node at) {
    if (op != "=") {
      throw Error("a character of a string is written with =", at);
    }
    var place = StrPlace(target.Children[0], scope, at) ?? throw Error("text[pos] = c needs a string variable, field or array item", at);
    var index = ExprOf(target.Children[1], scope, "u64");
    var value = ExprOf(valueNode, scope, "char");
    if (value.Type != "char") {
      throw Error($"a character of a string is a char, not {value.Type}", at);
    }
    string call = $"df_str_set_char({place.Slot}, {place.Flag}, {IndexValue(index, target)}, {value.Code}, {fn!.NameConst})";
    return CheckRaise(Need("text", call), "void");
  }

  // the statements of a frame free the temporaries they made
  void EmitTmpRelease(StringBuilder sb, int from, string pad) {
    if (fn != null && sb.Length > from && sb.ToString(from, sb.Length - from).Contains("df_tmp(")) {
      fn.UsesTmp = true;
      sb.Append($"{pad}df_tmp_drop(df_tm);\n");
    }
  }
}
