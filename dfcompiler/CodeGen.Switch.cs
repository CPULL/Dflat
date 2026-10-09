using System.Globalization;
using System.Numerics;
using System.Text;
using dfparser;

namespace dfcompiler;

sealed class EnumInfo {
  public EnumInfo(string name) {
    Name = name;
  }

  public string Name { get; }
  public string ValueType { get; set; } = "i32";
  public List<(string Name, BigInteger Value)> Entries { get; } = new();

  public BigInteger? Find(string entryName) {
    foreach (var entry in Entries) {
      if (entry.Name == entryName) {
        return entry.Value;
      }
    }
    return null;
  }
}

sealed class SwitchContext {
  public SwitchContext(int id, string subjectType, Frame frame) {
    Id = id;
    SubjectType = subjectType;
    Frame = frame;
  }

  public int Id { get; }
  public Frame Frame { get; }                                    // the scope holding the switch
  public string SubjectType { get; }
  public Dictionary<string, int> Keys { get; } = new();          // constant case value → case index
  public int? DefaultIndex { get; set; }
  public int CurrentCase { get; set; } = -1;
  public Dictionary<int, HashSet<int>> Jumps { get; } = new();   // case → cases it jumps to

  public string CaseLabel(int index) => $"sw{Id}_case{index}";
  public string EndLabel => $"sw{Id}_end";
}

// Enums, switch statements and switch expressions.
public sealed partial class CodeGen {
  readonly Dictionary<string, EnumInfo> enums = new();
  readonly StringBuilder enumCode = new();
  readonly Stack<SwitchContext> switches = new();
  string? currentEnum;   // enum whose method is being compiled: entries usable without the enum name
  string? caseEnum;      // enum of the switch subject while compiling case values

  // ---------------------------------------------------------------- enums

  void DeclareEnum(Node node) {
    string name = node.Text!;
    if (enums.ContainsKey(name)) {
      throw Error($"enum '{name}' already defined", node);
    }
    enums[name] = new EnumInfo(name);
  }

  // enum [ValueType] Name { A, B: 10, C: A, fun ... }
  void RegisterEnum(Node node) {
    var info = enums[node.Text!];
    var valueTypeNode = node.Children.FirstOrDefault(child => child.Kind == "ValueType");
    if (valueTypeNode != null) {
      var typeNode = valueTypeNode.Children[0];
      string valueType;
      try {
        valueType = ResolveType(typeNode);
      } catch (CompileException) {
        throw Error($"enum value type '{DescribeType(typeNode)}' is not supported yet (struct values need classes)", node);
      }
      if (!IsInt(valueType)) {
        throw Error($"enum values must be integers for now, not {valueType}", node);
      }
      info.ValueType = valueType;
    }

    var (minValue, maxValue) = IntRange(info.ValueType);
    BigInteger next = 0;
    foreach (var entry in node.Children.Where(child => child.Kind == "Entry")) {
      if (info.Find(entry.Text!) != null) {
        throw Error($"duplicate entry '{entry.Text}' in enum '{info.Name}'", entry);
      }
      BigInteger value = entry.Children.Count == 0 ? next : EnumEntryValue(entry.Children[0], info);
      if (value < minValue || value > maxValue) {
        throw Error($"{value} doesn't fit in {info.ValueType}", entry);
      }
      info.Entries.Add((entry.Text!, value));
      next = value + 1;
    }
    if (info.Entries.Count == 0) {
      throw Error($"enum '{info.Name}' needs at least one entry", node);
    }
    foreach (var method in node.Children.Where(child => child.Kind == "Fun")) {
      RegisterFun(method, info.Name);
    }

    // entry name for toStr(); aliases print the first entry with that value
    string cType = CType(info.Name);
    string fallback = SignedTypes.Contains(info.ValueType)
      ? "df_str_from_i64((int64_t)value)"
      : "df_str_from_u64((uint64_t)value)";
    enumCode.Append($"static df_string df_enum_str_{info.Name}({cType} value) {{\n  switch (value) {{\n");
    var seen = new HashSet<BigInteger>();
    foreach (var entry in info.Entries) {
      if (seen.Add(entry.Value)) {
        enumCode.Append($"    case {IntConstCode(entry.Value, info.ValueType)}: return {CString(CodePoints(entry.Name))};\n");
      }
    }
    enumCode.Append($"  }}\n  return {fallback};\n}}\n\n");
  }

  // an integer literal, or another entry (alias)
  BigInteger EnumEntryValue(Node valueNode, EnumInfo info) {
    var literal = IntLiteralValue(valueNode);
    if (literal != null) {
      return literal.Value;
    }
    if (valueNode.Kind == "Name") {
      return info.Find(valueNode.Text!) ?? throw Error($"unknown entry '{valueNode.Text}' in enum '{info.Name}'", valueNode);
    }
    throw Error("enum values must be integer literals or another entry for now", valueNode);
  }

  Expr? EnumEntryExpr(string enumName, string entryName) {
    var info = enums[enumName];
    var value = info.Find(entryName);
    return value == null ? null : new Expr(IntConstCode(value.Value, info.ValueType), enumName);
  }

  // entries without the enum name: in case values, and inside the enum's own methods
  Expr? BareEnumEntry(string name) {
    if (caseEnum != null && EnumEntryExpr(caseEnum, name) is Expr fromCase) {
      return fromCase;
    }
    if (currentEnum != null && EnumEntryExpr(currentEnum, name) is Expr fromMethod) {
      return fromMethod;
    }
    return null;
  }

  // value of an integer literal node (decimal, hex, binary, negated, in parentheses)
  static BigInteger? IntLiteralValue(Node node) {
    if (node.Kind == "Paren") {
      return IntLiteralValue(node.Children[0]);
    }
    if (node.Kind == "Neg") {
      var inner = IntLiteralValue(node.Children[0]);
      return inner == null ? null : -inner.Value;
    }
    if (node.Kind != "Int") {
      return null;
    }
    string raw = node.Text!.Replace("_", "").ToLowerInvariant();
    if (raw.StartsWith("0x")) {
      return BigInteger.Parse("0" + raw[2..].TrimEnd('u'), NumberStyles.HexNumber);
    }
    if (raw.StartsWith("0b") && raw.Length > 2 && raw[2] is '0' or '1') {
      BigInteger value = 0;
      foreach (char digit in raw[2..].TrimEnd('u')) {
        value = value * 2 + (digit - '0');
      }
      return value;
    }
    int end = raw.Length;
    while (end > 0 && char.IsLetter(raw[end - 1])) {
      end--;
    }
    return BigInteger.Parse(raw[..end], CultureInfo.InvariantCulture);
  }

  // ---------------------------------------------------------------- case values

  string? SubjectEnum(string subjectType) {
    string baseType = BaseOf(subjectType);
    return IsEnum(baseType) ? baseType : null;
  }

  // A constant case value as a comparable key, or null when the value is not a constant.
  // Used for duplicate cases and for 'jump <case value>'.
  string? ConstKey(Node value, string subjectType) {
    switch (value.Kind) {
      case "Paren":
        return ConstKey(value.Children[0], subjectType);
      case "Int":
        return "n:" + IntLiteralValue(value);
      case "Neg":
        if (value.Children[0].Kind == "Real") {
          return "r:" + (-ParseReal(value.Children[0].Text!)).ToString("R", CultureInfo.InvariantCulture);
        }
        var negated = IntLiteralValue(value);
        return negated == null ? null : "n:" + negated;
      case "Real":
        return "r:" + ParseReal(value.Text!).ToString("R", CultureInfo.InvariantCulture);
      case "Char":
        return "n:" + Unescape(value.Text![1..^1], value)[0];
      case "String":
        return "s:" + string.Join(",", Unescape(value.Text![1..^1], value));
      case "Bool":
        return "b:" + value.Text;
      case "Null":
        return "null";
      case "Name": {
        string? subjectEnum = SubjectEnum(subjectType);
        var entry = subjectEnum == null ? null : enums[subjectEnum].Find(value.Text!);
        return entry == null ? null : "n:" + entry;
      }
      case "Member": {
        string[] parts = (Path(value) ?? "").Split('.');
        if (parts.Length == 2 && IsEnum(parts[0])) {
          var entry = enums[parts[0]].Find(parts[1]);
          return entry == null ? null : "n:" + entry;
        }
        return null;
      }
      default:
        return null;
    }
  }

  static double ParseReal(string text) {
    return double.Parse(text.Replace("_", "").TrimEnd('f', 'F'), CultureInfo.InvariantCulture);
  }

  // A case value: entries of the subject's enum may be written without the enum name
  Expr CaseValueExpr(Node value, Scope scope, string subjectType) {
    string? saved = caseEnum;
    caseEnum = SubjectEnum(subjectType);
    try {
      return ExprOf(value, scope, BaseOf(subjectType));
    } finally {
      caseEnum = saved;
    }
  }

  void CheckSubject(Expr subject, Node at) {
    if (subject.Type is "void" or "null" || IsMulti(subject.Type)) {
      throw Error("the switch subject needs a single typed value", at);
    }
  }

  // ---------------------------------------------------------------- switch statement

  // switch val { 1: a(), 2, 3: { ... } \n Red: jump 3 \n default: jump #out# }
  // Each case is a C label; the tests run in order and jump to the first matching case.
  void EmitSwitch(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var subject = ExprOf(s.Children[0], scope);
    CheckSubject(subject, s);
    tempCounter++;
    var context = new SwitchContext(tempCounter, subject.Type, frames.Peek());
    string subjectTemp = $"sw{context.Id}_value";
    var subjectExpr = new Expr(subjectTemp, subject.Type);
    var cases = s.Children.Skip(1).ToList();

    var tests = new StringBuilder();
    for (int index = 0; index < cases.Count; index++) {
      var caseNode = cases[index];
      var conditions = new List<string>();
      foreach (var value in caseNode.Children.Take(caseNode.Children.Count - 1)) {
        if (value.Kind == "Default") {
          if (context.DefaultIndex != null) {
            throw Error("a switch can have only one 'default'", value);
          }
          context.DefaultIndex = index;
          continue;
        }
        string? key = ConstKey(value, subject.Type);
        if (key != null && !context.Keys.TryAdd(key, index)) {
          throw Error("duplicate case value", value);
        }
        conditions.Add(EqualsCode(subjectExpr, CaseValueExpr(value, scope, subject.Type), value));
      }
      if (conditions.Count > 0) {
        tests.Append($"{pad}  if ({string.Join(" || ", conditions)}) goto {context.CaseLabel(index)};\n");
      }
    }
    string fallThrough = context.DefaultIndex == null ? context.EndLabel : context.CaseLabel(context.DefaultIndex.Value);

    sb.Append($"{pad}{{\n");
    sb.Append($"{pad}  {CType(subject.Type)} {subjectTemp} = {subject.Code};\n");
    sb.Append(tests);
    sb.Append($"{pad}  goto {fallThrough};\n");
    switches.Push(context);
    for (int index = 0; index < cases.Count; index++) {
      context.CurrentCase = index;
      var body = cases[index].Children.Last();
      sb.Append($"{pad}  {context.CaseLabel(index)}: {{\n");
      EmitStatementAsBlock(body, sb, scope, indent + 2);
      sb.Append($"{pad}  }}\n{pad}  goto {context.EndLabel};\n");
    }
    switches.Pop();
    sb.Append($"{pad}  {context.EndLabel}: ;\n{pad}}}\n");

    if (HasJumpLoop(context.Jumps)) {
      Warn("jump loop between switch cases", s, 1);
    }
  }

  // jump #label# | jump name (a case inside a switch, a label outside) | jump default | jump <case value>
  void EmitJump(Node s, StringBuilder sb, int indent) {
    string pad = Pad(indent);
    var target = s.Children[0];
    if (target.Kind == "Label") {
      EmitJumpToLabel(target.Text!, sb, indent, s);
      return;
    }
    if (switches.Count == 0) {
      if (target.Kind == "Target") {
        EmitJumpToLabel(target.Text!, sb, indent, s);
        return;
      }
      throw Error("jumps to case values are only valid inside a switch", s);
    }
    var context = switches.Peek();
    int caseIndex;
    if (target.Kind == "Default") {
      caseIndex = context.DefaultIndex ?? throw Error("this switch has no 'default'", s);
    } else {
      var valueNode = target.Kind == "Target" ? Synthetic("Name", target, target.Text) : target.Children[0];
      string? key = ConstKey(valueNode, context.SubjectType);
      if (key == null) {
        throw target.Kind == "Target"
          ? Error($"no case '{target.Text}' in this switch; use 'jump #{target.Text}#' for a label", s)
          : Error("a jump to a case needs a constant value", s);
      }
      if (!context.Keys.TryGetValue(key, out caseIndex)) {
        throw Error("no case with this value in the switch", s);
      }
    }
    if (context.CurrentCase >= 0) {
      if (!context.Jumps.TryGetValue(context.CurrentCase, out var targets)) {
        targets = new HashSet<int>();
        context.Jumps[context.CurrentCase] = targets;
      }
      targets.Add(caseIndex);
    }
    EmitUnwindTo(context.Frame, sb, indent, s);
    sb.Append($"{pad}goto {context.CaseLabel(caseIndex)};\n");
  }

  static bool HasJumpLoop(Dictionary<int, HashSet<int>> jumps) {
    var state = new Dictionary<int, int>(); // 1 = visiting, 2 = done
    bool Visit(int caseIndex) {
      state.TryGetValue(caseIndex, out int current);
      if (current == 1) {
        return true;
      }
      if (current == 2) {
        return false;
      }
      state[caseIndex] = 1;
      if (jumps.TryGetValue(caseIndex, out var targets) && targets.Any(Visit)) {
        return true;
      }
      state[caseIndex] = 2;
      return false;
    }
    return jumps.Keys.Any(Visit);
  }

  // ---------------------------------------------------------------- switch expressions

  // the arms' type: the expected one when every arm fits it, otherwise their common type
  string ArmsType(List<Expr> results, string? expected, Node at) {
    if (expected != null && results.All(result => result.Type == "null"
        ? IsNullable(expected) || expected == "string"
        : expected == "bool" || Assignable(expected, result.Type))) {
      return expected;
    }
    return CommonType(results, at);
  }

  // subject ? { 1: a, 2, 3: b, default: c }
  Expr SwitchExpr(Node n, Scope scope, string? expected) {
    var subject = ExprOf(n.Children[0], scope);
    CheckSubject(subject, n);
    var arms = n.Children.Skip(1).ToList();
    if (arms.Count == 0) {
      throw Error("a switch expression needs at least one arm", n);
    }
    var results = arms.Select(arm => ExprOf(arm.Children[1], scope, expected)).ToList();
    string resultType = ArmsType(results, expected, n);

    string code = WithTemp(subject, subjectCode => {
      var subjectExpr = new Expr(subjectCode, subject.Type);
      var keys = new HashSet<string>();
      int defaultIndex = -1;
      var tests = new List<(string Condition, int Arm)>();
      for (int index = 0; index < arms.Count; index++) {
        var conditions = new List<string>();
        foreach (var value in arms[index].Children[0].Children) {
          if (value.Kind == "Default") {
            if (defaultIndex >= 0) {
              throw Error("a switch expression can have only one 'default'", value);
            }
            defaultIndex = index;
            continue;
          }
          string? key = ConstKey(value, subject.Type);
          if (key != null && !keys.Add(key)) {
            throw Error("duplicate case value", value);
          }
          conditions.Add(EqualsCode(subjectExpr, CaseValueExpr(value, scope, subject.Type), value));
        }
        if (defaultIndex != index) {
          tests.Add((string.Join(" || ", conditions), index));
        }
      }

      int lastIndex = defaultIndex;
      if (lastIndex < 0) {
        if (!IsExhaustive(subject.Type, keys)) {
          throw Error("this switch expression needs a 'default'", n);
        }
        // every value is covered: the last test can be skipped
        lastIndex = tests[^1].Arm;
        tests.RemoveAt(tests.Count - 1);
      }
      var chain = new StringBuilder("(");
      foreach (var test in tests) {
        chain.Append($"({test.Condition}) ? {Convert(results[test.Arm], resultType, arms[test.Arm])} : ");
      }
      chain.Append(Convert(results[lastIndex], resultType, arms[lastIndex])).Append(')');
      return chain.ToString();
    });
    return new Expr(code, resultType);
  }

  bool IsExhaustive(string subjectType, HashSet<string> keys) {
    if (IsNullable(subjectType) && !keys.Contains("null")) {
      return false;
    }
    string baseType = BaseOf(subjectType);
    if (baseType == "bool") {
      return keys.Contains("b:true") && keys.Contains("b:false");
    }
    if (IsEnum(baseType)) {
      return enums[baseType].Entries.All(entry => keys.Contains("n:" + entry.Value));
    }
    return false;
  }

  // condition ? { whenTrue, whenFalse }
  Expr BoolSwitchExpr(Node n, Scope scope, string? expected) {
    string condition = BoolOf(ExprOf(n.Children[0], scope), n);
    var results = new List<Expr> {
      ExprOf(n.Children[1], scope, expected),
      ExprOf(n.Children[2], scope, expected)
    };
    string resultType = ArmsType(results, expected, n);
    string whenTrue = Convert(results[0], resultType, n.Children[1]);
    string whenFalse = Convert(results[1], resultType, n.Children[2]);
    return new Expr($"({condition} ? {whenTrue} : {whenFalse})", resultType);
  }
}
