using System.Text;
using dfparser;

namespace dfcompiler;

// Set values (spec 11.3.6.1): a set used as the type of a variable, a field, a result or an item is a closed union. The value is the pair
// (tag, value). In C it is df_setv { bits, tag }: the value of the member is copied into bits, tag is 1 + the position of the member in
// the set (the hash of the type name of 6.2.1 is what the hash and the order will use). Tag 0 is the generic null of a value never
// assigned, so a zeroed df_setv (a new field, item or variable) is that null: it prints null, is is false on it, a type switch goes to
// default or raises UnassignedTypeException. Built so far: native members that fit in 64 bits (numbers up to 64 bits, bool, char, enums,
// bitfields); a value is written from a member (a literal follows the rule of 11.3.6.1) and read only through is and is ? { }.
public sealed partial class CodeGen {
  static readonly HashSet<string> SetValueNatives = new() {
    "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "r32", "r64", "bool", "char"
  };

  // the natives that are nullable by default (spec 7.1): their own null, in 64 bits
  static readonly HashSet<string> SetValueNullableNatives = new() { "date", "time", "clock", "lapse", "addr" };

  // a member that can hold null: T? of a native, or a native nullable by default
  static bool IsNullableMember(string type) => type.EndsWith('?') || SetValueNullableNatives.Contains(type);

  // a set used as the type of a value (a set name is never the type of a value otherwise)
  bool IsSetValue(string type) => IsSetName(type);

  // the members of the set, in the order of the set: the position is the tag
  List<string> SetValueMembers(string set, Node at) {
    var members = SetMembers(set);
    foreach (string member in members.Where(member => !IsStage1Member(member))) {
      throw NotSupported($"Set values with a {DisplayName(member)} member (built: numbers up to 64 bits, bool, char, enums, bitfields, their nullable forms, date, time, clock, lapse, addr; strings, objects and tuples come later)", at);
    }
    return members;
  }

  bool IsEnumType(string type) => enums.ContainsKey(type);

  // numbers up to 64 bits, bool, char, enums, bitfields, their nullable forms, date, time, clock, lapse, addr
  bool IsStage1Member(string type) {
    string plain = type.EndsWith('?') ? type[..^1] : type;
    return SetValueNatives.Contains(plain) || IsEnumType(plain) || SetValueNullableNatives.Contains(type);
  }

  string SetValueCType() {
    if (definedTypes.Add("df_setv")) {
      typeDefs.Append("typedef struct {\n  uint64_t bits[2];\n  uint32_t tag;\n} df_setv;\n\n");   // spec 11.3.6.1: up to 16 bytes inline (an i64? is 16)
    }
    return "df_setv";
  }

  static bool IsRealMember(string type) => type is "r32" or "r64";

  // a member value into a set value: its bits and its tag
  Expr WrapSetValue(Expr value, string set, Node at) {
    var members = SetValueMembers(set, at);
    int tag = members.IndexOf(value.Type) + 1;
    if (tag == 0) {
      throw Error($"{DisplayName(value.Type)} is not a member of the set {DisplayName(set)} (spec 11.3.6.1)", at);
    }
    string held = NewTemp();
    string result = NewTemp();
    return new Expr($"({{ {CType(value.Type)} {held} = {value.Code}; {SetValueCType()} {result} = {{ {{ 0, 0 }}, {tag}u }}; memcpy({result}.bits, &{held}, sizeof {held}); {result}; }})", set);
  }

  // the value of a member read from a set value whose tag is that member
  string UnwrapSetValue(string code, string member) {
    string held = NewTemp();
    string result = NewTemp();
    return $"({{ {SetValueCType()} {held} = {code}; {CType(member)} {result}; memcpy(&{result}, {held}.bits, sizeof {result}); {result}; }})";
  }

  // Nums k = 5: a literal takes its default type when the set lists it, otherwise the only member of the same kind (spec 11.3.6.1)
  Expr? SetValueLiteral(Node n, Scope scope, string set) {
    var literal = n;
    bool negative = false;
    if (literal.Kind == "Neg" && literal.Children.Count == 1) {
      literal = literal.Children[0];
      negative = true;
    }
    if (literal.Kind is not ("Int" or "Real")) {
      return null;
    }
    var members = SetValueMembers(set, n);
    var natural = ExprOf(n, scope);
    if (members.Contains(natural.Type)) {
      return WrapSetValue(natural, set, n);
    }
    if (members.Contains(natural.Type + "?") && !members.Any(member => member == natural.Type)) {
      return WrapSetValue(new Expr(Convert(natural, natural.Type + "?", n), natural.Type + "?"), set, n);   // i64 5 into an i64? member
    }
    bool suffixed = literal.Kind == "Int" && !literal.Text!.StartsWith("0x") && !literal.Text.StartsWith("0b") && char.IsLetter(literal.Text[^1])
      || literal.Kind == "Real" && char.IsLetter(literal.Text![^1]);
    if (suffixed) {
      throw Error($"{DisplayName(natural.Type)} is not a member of the set {DisplayName(set)} (spec 11.3.6.1)", n);
    }
    var sameKind = members.Where(member => {
      string plain = member.EndsWith('?') ? member[..^1] : member;
      return literal.Kind == "Real" ? IsRealMember(plain) : SetValueNatives.Contains(plain) && plain is not ("bool" or "char") && !IsRealMember(plain);
    }).ToList();
    if (sameKind.Count != 1) {
      throw Error(sameKind.Count == 0
        ? $"no member of the set {DisplayName(set)} takes this literal (spec 11.3.6.1)"
        : $"more than one member of the set {DisplayName(set)} fits this literal ({string.Join(", ", sameKind.Select(DisplayName))}): write it with its suffix or a cast (spec 11.3.6.1)", n);
    }
    _ = negative;
    return WrapSetValue(ExprOf(n, scope, sameKind[0]), set, n);
  }

  // the member a literal written for a set takes (11.3.6.1), as a text for the duplicate keys of a map literal
  string? SetLiteralMember(Node n, Scope scope, string set) {
    var literal = n.Kind == "Neg" && n.Children.Count == 1 ? n.Children[0] : n;
    if (literal.Kind is not ("Int" or "Real")) {
      return null;
    }
    var natural = ExprOf(n, scope);
    var members = SetValueMembers(set, n);
    return members.Contains(natural.Type) ? natural.Type : "default";
  }

  // k1 == k2: the same tag and the same value (a real compares as a real: 0.0 and -0.0 are equal)
  string SetValueEquals(Expr l, Expr r, Node at) {
    if (l.Type == "null" || r.Type == "null") {
      // k == null: the null of the member it holds; the generic null of a value never assigned is not one (spec 11.3.6.1)
      var value = l.Type == "null" ? r : l;
      var nullable = SetValueMembers(value.Type, at).Select((member, index) => (member, index)).Where(pair => IsNullableMember(pair.member)).ToList();
      if (nullable.Count == 0) {
        return $"({{ (void)({value.Code}); false; }})";
      }
      string subject = NewTemp();
      var test = new StringBuilder($"({{ {SetValueCType()} {subject} = {value.Code}; bool isnull_{subject} = false; switch ({subject}.tag) {{ ");
      foreach (var (member, index) in nullable) {
        test.Append($"case {index + 1}u: isnull_{subject} = {EqualsCode(new Expr(UnwrapSetValue(subject, member), member), new Expr("0", "null"), at)}; break; ");
      }
      test.Append($"default: break; }} isnull_{subject}; }})");
      return test.ToString();
    }
    if (l.Type != r.Type) {
      throw Error($"a value of the set {DisplayName(l.Type)} is compared only with another one of the same set (spec 11.3.6.1)", at);
    }
    var members = SetValueMembers(l.Type, at);
    string a = NewTemp();
    string b = NewTemp();
    var sb = new StringBuilder($"({{ {SetValueCType()} {a} = {l.Code}; {SetValueCType()} {b} = {r.Code}; bool eq_{a} = {a}.tag == {b}.tag; if (eq_{a}) {{ switch ({a}.tag) {{ ");
    for (int index = 0; index < members.Count; index++) {
      string member = members[index];
      sb.Append($"case {index + 1}u: eq_{a} = {EqualsCode(new Expr(UnwrapSetValue(a, member), member), new Expr(UnwrapSetValue(b, member), member), at)}; break; ");
    }
    sb.Append($"default: break; }} }} eq_{a}; }})");   // two values never assigned (tag 0) are equal
    return sb.ToString();
  }

  // $"{k}": the text of the value of its member
  string SetValueStr(Expr e, Node at) {
    var members = SetValueMembers(e.Type, at);
    string held = NewTemp();
    string text = NewTemp();
    var sb = new StringBuilder($"({{ {SetValueCType()} {held} = {e.Code}; df_string {text} = NULL; switch ({held}.tag) {{ case 0u: {text} = df_str_lit(\"null\", 4); break; ");
    for (int index = 0; index < members.Count; index++) {
      sb.Append($"case {index + 1}u: {text} = {StrOf(new Expr(UnwrapSetValue(held, members[index]), members[index]), at)}; break; ");
    }
    sb.Append($"}} {text}; }})");
    return sb.ToString();
  }

  // the type named after is: a member of the set
  int SetMemberTag(string set, Node typeNode, Node at) {
    string type = ResolveType(typeNode);
    int tag = SetValueMembers(set, at).IndexOf(type) + 1;
    if (tag == 0) {
      throw Error($"{DisplayName(type)} is not a member of the set {DisplayName(set)}: it can never match (spec 11.3.6.1)", typeNode);
    }
    return tag;
  }

  // if k is i32 i { }: the tag of k is the one of i32; i is a copy of the value, only in the block
  (string Condition, Scope BlockScope) SetIsBinding(Node n, Expr subject, Scope scope) {
    string member = ResolveType(n.Children[1]);
    int tag = SetMemberTag(subject.Type, n.Children[1], n);
    CheckNotShadowing(n.Text!, scope, n);
    tempCounter++;
    string cname = $"v_{n.Text}_{tempCounter}";
    fn!.Decls.Append($"  {CType(member)} {cname} = {ZeroValue(member)};\n");
    string held = NewTemp();
    string condition = $"({{ {SetValueCType()} {held} = {subject.Code}; bool ok_{held} = {held}.tag == {tag}u; if (ok_{held}) {{ {cname} = {UnwrapSetValue(held, member)}; }} ok_{held}; }})";
    var inner = new Scope(scope);
    inner.Vars[n.Text!] = new VarInfo(member, cname, false);
    return (condition, inner);
  }

  // k is ? { i32 x: ..., u64 x: ..., default: ... } on a set value: chosen at run time by the tag; every member needs a case or default
  Expr SetTypeSwitch(Node n, Expr subject, Scope scope, string? expected) {
    var members = SetValueMembers(subject.Type, n);
    var covered = new HashSet<int>();
    var arms = new List<(List<int> Tags, Node Arm, bool IsDefault)>();
    foreach (var arm in n.Children.Skip(1)) {
      var tags = new List<int>();
      bool isDefault = false;
      foreach (var label in arm.Children[0].Children) {
        if (label.Kind == "Default") {
          isDefault = true;
          continue;
        }
        var labelTags = label is { Kind: "Type", Children.Count: 0 } && IsSetName(label.Text!)
          ? SetMembers(label.Text!).Select(member => members.IndexOf(member) + 1).Where(tag => tag > 0).ToList()
          : new List<int> { SetMemberTag(subject.Type, label, label) };
        if (labelTags.Count == 0 || labelTags.All(tag => covered.Contains(tag))) {
          Warn("a case of the type switch can never match: an earlier case already takes its types", label, 2);
        }
        tags.AddRange(labelTags.Where(tag => covered.Add(tag)));
      }
      if (arm.Text != null && (isDefault || arm.Children[0].Children.Count != 1 || tags.Count != 1)) {
        throw Error($"'{arm.Text}' binds the value of one member: write one type before it", arm);
      }
      arms.Add((tags, arm, isDefault));
    }
    if (!arms.Any(arm => arm.IsDefault) && covered.Count < members.Count) {
      var missing = members.Where((_, index) => !covered.Contains(index + 1)).Select(DisplayName);
      throw Error($"the type switch does not cover {string.Join(", ", missing)} of the set {DisplayName(subject.Type)}: add a case or a 'default'", n);
    }
    string held = NewTemp();
    string result = NewTemp();
    var cases = new StringBuilder();
    string? type = expected;
    foreach (var (tags, arm, isDefault) in arms) {
      var armScope = scope;
      string bind = "";
      if (arm.Text != null) {
        string member = members[tags[0] - 1];
        CheckNotShadowing(arm.Text, scope, arm);
        armScope = new Scope(scope);
        string cname = $"v_{arm.Text}_{++tempCounter}";
        armScope.Vars[arm.Text] = new VarInfo(member, cname, false);
        bind = $"{CType(member)} {cname} = {UnwrapSetValue(held, member)}; ";
      }
      var value = ExprOf(arm.Children[1], armScope, type);
      type ??= value.Type;
      string labels = isDefault ? "default:" : string.Join(" ", tags.Select(tag => $"case {tag}u:"));
      cases.Append($"{labels} {{ {bind}{result} = {Convert(value, type, arm)}; break; }} ");
    }
    if (!arms.Any(arm => arm.IsDefault)) {
      // a value never assigned and no default: UnassignedTypeException (spec 11.3.6.1, 15.3)
      cases.Append($"case 0u: {{ df_raise(&df_desc_UnassignedTypeException, {fn!.NameConst}); {RaiseJump} }} ");
    }
    return new Expr($"({{ {SetValueCType()} {held} = {subject.Code}; {CType(type!)} {result} = {ZeroValue(type!)}; switch ({held}.tag) {{ {cases}}} {result}; }})", type!);
  }
}
