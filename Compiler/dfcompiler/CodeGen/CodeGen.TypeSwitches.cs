using System.Text;
using dfparser;

namespace dfcompiler;

// switch value is ? { Type name: ..., Type: ..., default: ... } (spec 11.3.7, 10.5), and the type switch expression on an object: chosen when
// the program runs, by the tag of a set value or by the class of an object; by the type when it is known. A null object goes to default.
public sealed partial class CodeGen {
  // how a case of an object switch is related to the type of the object
  enum TypeCase { Never, Maybe, Always }

  TypeCase ObjectCase(string type, string target, bool bound) {
    if (!IsClass(target)) {
      return TypeCase.Never;
    }
    if (IsSubtype(type, target)) {
      return TypeCase.Always;
    }
    if (IsSubtype(target, type)) {
      return TypeCase.Maybe;
    }
    // a subclass can have the trait, and the class of a trait object is not known
    return (IsTraitType(target) || IsTraitType(type)) && bound ? TypeCase.Maybe : TypeCase.Never;
  }

  // the types of the labels of a case; a set stands for its members
  List<string> CaseTypes(Node labels, out bool isDefault) {
    isDefault = false;
    var types = new List<string>();
    foreach (var label in labels.Children) {
      if (label.Kind == "Default") {
        isDefault = true;
      } else if (label is { Kind: "Type", Children.Count: 0 } && IsSetName(label.Text!)) {
        types.AddRange(SetMembers(label.Text!));
      } else {
        types.Add(ResolveType(label));
      }
    }
    return types;
  }

  // the cases of a switch on an object of this type, in order: the types to test; Always: every object that comes here takes it
  List<(Node Arm, List<string> Tests, bool Always, bool IsDefault)> ObjectCases(Node n, string type) {
    var cases = new List<(Node Arm, List<string> Tests, bool Always, bool IsDefault)>();
    var earlier = new List<string>();
    bool covered = false;
    bool sawDefault = false;
    foreach (var arm in n.Children.Skip(1)) {
      var types = CaseTypes(arm.Children[0], out bool isDefault);
      if (arm.Text != null && (isDefault || types.Count != 1)) {
        throw Error($"'{arm.Text}' binds the object as one type: write one type before it", arm);
      }
      if (isDefault) {
        if (sawDefault) {
          throw Error("a type switch can have only one 'default'", arm);
        }
        sawDefault = true;
      }
      var tests = types.Where(t => ObjectCase(type, t, arm.Text != null) != TypeCase.Never).ToList();
      bool dead = tests.Count > 0 && (covered || tests.All(t => earlier.Any(e => IsSubtype(t, e))));
      if ((dead || types.Count > 0 && sawDefault && !isDefault) && currentFun?.TemplateParams == null) {
        Warn("a case of the type switch can never match: an earlier case already takes its types", arm, 2);
      }
      if (tests.Count > 0 && !covered) {
        bool always = tests.Any(t => ObjectCase(type, t, arm.Text != null) == TypeCase.Always);
        cases.Add((arm, tests, always, isDefault));
        covered |= always;
        earlier.AddRange(tests);
      } else if (isDefault) {
        cases.Add((arm, new List<string>(), false, true));
      }
    }
    if (!covered && !sawDefault) {
      throw Error($"no case of the type switch takes every {DisplayName(type)}: add a case for it or a 'default'", n);
    }
    return cases;
  }

  string ObjectTest(string code, string type, List<string> tests, Node at) =>
    "(" + string.Join(" || ", tests.Select(t => IsTest(code, classes.TryGetValue(t, out var cls) ? cls : traits[t], type, at))) + ")";

  // obj is ? { Dog x: ..., Cat x: ..., default: ... } as a value: by the class of the object
  Expr ObjectTypeSwitch(Node n, Expr subject, Scope scope, string? expected) {
    string type = subject.Type;
    subject = TempObject(subject);
    string held = NewTemp();
    string result = NewTemp();
    string? resultType = expected;
    bool fresh = true;
    var chain = new StringBuilder();
    string fallback = "{ }";
    foreach (var (arm, tests, _, isDefault) in ObjectCases(n, type)) {
      var armScope = scope;
      string bind = "";
      if (arm.Text != null) {
        CheckNotShadowing(arm.Text, scope, arm);
        armScope = new Scope(scope);
        string cname = $"v_{arm.Text}_{++tempCounter}";
        armScope.Vars[arm.Text] = new VarInfo(tests[0], cname, false);
        bind = $"{CType(tests[0])} {cname} = ({CType(tests[0])}){held}; ";
      }
      var value = ExprOf(arm.Children[1], armScope, resultType);
      resultType ??= value.Type;
      fresh &= value.Fresh || value.Type == "null";
      string armCode;
      if (resultType == "void") {
        armCode = $"{{ {bind}{value.Code}; }}";
      } else {
        string code = Convert(value, resultType, arm);
        if (resultType == "string" && !(value.Fresh || value.Type != "string")) {
          code = $"df_str_dup({code})";
        }
        armCode = $"{{ {bind}{result} = {code}; }}";
      }
      if (tests.Count > 0) {
        chain.Append($"if ({ObjectTest(held, type, tests, arm)}) {armCode} else ");
      }
      if (isDefault) {
        fallback = armCode;
      }
    }
    if (resultType == "void") {
      return new Expr($"({{ {CType(type)} {held} = {subject.Code}; {chain}{fallback} }})", "void");
    }
    return new Expr($"({{ {CType(type)} {held} = {subject.Code}; {CType(resultType!)} {result} = {ZeroValue(resultType!)}; {chain}{fallback} {result}; }})",
      resultType!, resultType == "string" || IsClass(resultType!) && fresh);
  }

  readonly Stack<(Node Switch, int Outer)> typeSwitches = new();   // the type switches being compiled, and the ordinary ones open around each

  // a jump in a case of a type switch can leave it, never go to another case of it: the value would have another type (spec 11.3.7)
  void CheckTypeCaseJump(Node target, Node at) {
    if (typeSwitches.Count == 0) {
      return;
    }
    var (inside, outer) = typeSwitches.Peek();
    if (switches.Count > outer) {
      return;   // the nearest switch is an ordinary one
    }
    var labels = inside.Children.Skip(1).SelectMany(arm => arm.Children[0].Children);
    if (labels.Any(label => target.Kind == "Default" ? label.Kind == "Default" : target.Kind == "Target" && label.Kind == "Type" && label.Text == target.Text)) {
      throw Error("jump cannot go to another case of a type switch; jump #label# leaves it (spec 11.3.7)", at);
    }
  }

  // switch value is ? { }
  void EmitTypeSwitch(Node s, StringBuilder sb, Scope scope, int indent) {
    typeSwitches.Push((s, switches.Count));
    try {
      EmitTypeSwitchCases(s, sb, scope, indent);
    } finally {
      typeSwitches.Pop();
    }
  }

  void EmitTypeSwitchCases(Node s, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    var subjectNode = s.Children[0];
    var subject = ExprOf(subjectNode, scope);
    string type = subject.Type;
    if (type is "void" or "null" || IsMulti(type)) {
      throw Error("a type switch needs a value", subjectNode);
    }
    if (subject.Fresh || subjectNode.Kind == "Call") {
      // a new value: a variable of the block holds it until the end of the switch
      string name = $"__switch{++tempCounter}";
      var inner = Synthetic("TypeSwitchStmt", s).Add(Synthetic("Name", subjectNode, name));
      inner.Children.AddRange(s.Children.Skip(1));
      var block = Synthetic("Block", s).Add(Synthetic("Decl", s, name).Add(Synthetic("Type", s, "var")).Add(Synthetic("Init", s).Add(subjectNode))).Add(inner);
      sb.Append($"{pad}{{\n");
      EmitBlock(block, sb, scope, indent + 1);
      sb.Append($"{pad}}}\n");
      return;
    }
    if (TasksMode && type is "Task" or "Thread") {
      EmitTaskSwitch(s, sb, scope, indent);
      return;
    }
    if (!IsSetValue(type) && !IsClass(type)) {
      EmitKnownSwitch(s, subjectNode, type, sb, scope, indent);
      return;
    }
    bool held = subjectNode.Kind is not ("Name" or "Paren");
    if (held) {
      string temp = NewTemp();
      sb.Append($"{pad}{{\n{Pad(indent + 1)}{CType(type)} {temp} = {subject.Code};\n");
      prebuilt[subjectNode] = subject with { Code = temp };
      indent++;
      pad = Pad(indent);
    }
    try {
      EmitSwitchChain(s, subjectNode, type, sb, scope, indent);
    } finally {
      if (held) {
        prebuilt.Remove(subjectNode);
      }
    }
    if (held) {
      sb.Append($"{Pad(indent - 1)}}}\n");
    }
  }

  void EmitSwitchChain(Node s, Node subjectNode, string type, StringBuilder sb, Scope scope, int indent) {
    string pad = Pad(indent);
    bool set = IsSetValue(type);
    var cases = set ? SetCases(s, type) : ObjectCases(s, type);
    Node? fallback = null;
    var before = assign.Clone();
    var states = new List<AssignState>();
    sb.Append(pad);
    foreach (var (arm, tests, _, isDefault) in cases) {
      if (isDefault) {
        fallback = arm;
      }
      if (tests.Count == 0) {
        continue;
      }
      var armScope = scope;
      Node? usage = null;
      string condition;
      if (arm.Text != null) {
        var check = Synthetic("Is", arm, arm.Text).Add(subjectNode).Add(Synthetic(set ? "ResolvedType" : "Type", arm, tests[0]));
        bool saved = quietIs;
        quietIs = true;
        try {
          (condition, armScope, usage) = IsBinding(check, scope);
        } finally {
          quietIs = saved;
        }
      } else if (set) {
        string code = ExprOf(subjectNode, scope).Code;
        var members = SetValueMembers(type, s);
        condition = "(" + string.Join(" || ", tests.Select(t => $"({code}).tag == {members.IndexOf(t) + 1}u")) + ")";
      } else {
        condition = ObjectTest(ExprOf(subjectNode, scope).Code, type, tests, arm);
      }
      assign = before.Clone();
      sb.Append($"if ({condition}) {{\n");
      EmitBlock(WithUsage(CaseBody(arm), usage), sb, armScope, indent + 1);
      sb.Append($"{pad}}} else ");
      states.Add(assign);
    }
    assign = before.Clone();
    sb.Append("{\n");
    if (fallback != null) {
      EmitBlock(CaseBody(fallback), sb, scope, indent + 1);
    } else if (set) {
      sb.Append($"{pad}  df_raise(&df_desc_UnassignedTypeException, {fn!.NameConst}); {RaiseJump}\n");   // never assigned (spec 11.3.6.1)
      assign.Unreachable = true;
    }
    sb.Append($"{pad}}}\n");
    states.Add(assign);
    assign = AssignState.Merge(states);
  }

  static Node CaseBody(Node arm) => arm.Children[1].Kind == "Block" ? arm.Children[1] : Synthetic("Block", arm).Add(arm.Children[1]);

  // the cases of a set value: every member needs a case or default
  List<(Node Arm, List<string> Tests, bool Always, bool IsDefault)> SetCases(Node n, string set) {
    var members = SetValueMembers(set, n);
    var covered = new HashSet<string>();
    bool sawDefault = false;
    var cases = new List<(Node Arm, List<string> Tests, bool Always, bool IsDefault)>();
    foreach (var arm in n.Children.Skip(1)) {
      var types = new List<string>();
      bool isDefault = false;
      foreach (var label in arm.Children[0].Children) {
        if (label.Kind == "Default") {
          isDefault = true;
        } else if (label is { Kind: "Type", Children.Count: 0 } && IsSetName(label.Text!)) {
          types.AddRange(SetMembers(label.Text!).Where(members.Contains));
        } else {
          types.Add(members[SetMemberTag(set, label, label) - 1]);
        }
      }
      if (isDefault && sawDefault) {
        throw Error("a type switch can have only one 'default'", arm);
      }
      sawDefault |= isDefault;
      if (!isDefault && types.All(covered.Contains)) {
        Warn("a case of the type switch can never match: an earlier case already takes its types", arm, 2);
      }
      if (arm.Text != null && (isDefault || types.Count != 1)) {
        throw Error($"'{arm.Text}' binds the value of one member: write one type before it", arm);
      }
      cases.Add((arm, types.Where(covered.Add).ToList(), false, isDefault));
    }
    if (!sawDefault && covered.Count < members.Count) {
      throw Error($"the type switch does not cover {string.Join(", ", members.Where(m => !covered.Contains(m)).Select(DisplayName))} of the set {DisplayName(set)}: add a case or a 'default'", n);
    }
    return cases;
  }

  // the type is known (a parameter with a set type, a T of a generic, any plain value): only the matching case is compiled
  void EmitKnownSwitch(Node s, Node subjectNode, string type, StringBuilder sb, Scope scope, int indent) {
    Node? chosen = null;
    Node? fallback = null;
    foreach (var arm in s.Children.Skip(1)) {
      foreach (var label in arm.Children[0].Children) {
        if (label.Kind == "Default") {
          if (fallback != null) {
            throw Error("a type switch can have only one 'default'", label);
          }
          fallback = arm;
        } else if (chosen == null && CaseMatches(type, label)) {
          chosen = arm;
        }
      }
    }
    chosen ??= fallback ?? throw Error($"no case of the type switch matches {DisplayName(type)}: add a case for it or a 'default'", s);
    var body = CaseBody(chosen);
    if (chosen.Text != null) {
      if (chosen.Children[0].Children is not [{ Kind: not "Default" }]) {
        throw Error($"'{chosen.Text}' binds the value as one type: write one type before it", chosen);
      }
      var named = Synthetic("Block", chosen).Add(Synthetic("Decl", chosen, chosen.Text).Add(Synthetic("Type", chosen, "var")).Add(Synthetic("Init", chosen).Add(subjectNode)));
      named.Children.AddRange(body.Children);
      body = named;
    }
    string pad = Pad(indent);
    sb.Append($"{pad}{{\n");
    EmitBlock(body, sb, scope, indent + 1);
    sb.Append($"{pad}}}\n");
  }

  // switch t is ? { Task<MyTask> m: ..., default: ... } (spec 20.9): the same checks as if t is Task<MyTask> m { } else ...
  void EmitTaskSwitch(Node s, StringBuilder sb, Scope scope, int indent) {
    Node? chain = null;
    foreach (var arm in s.Children.Skip(1).Reverse()) {
      if (arm.Children[0].Children.Any(label => label.Kind == "Default")) {
        if (chain != null) {
          throw Error("the default of a type switch on a task is its last case (spec 20.9)", arm);
        }
        chain = CaseBody(arm);
        continue;
      }
      if (chain == null) {
        throw Error("a type switch on a task needs a 'default' (spec 20.9)", s);
      }
      if (arm.Children[0].Children.Count != 1) {
        throw Error("a case of a type switch on a task is one type (spec 20.9)", arm);
      }
      chain = Synthetic("If", arm).Add(Synthetic("Is", arm, arm.Text ?? $"__task{++tempCounter}").Add(s.Children[0]).Add(arm.Children[0].Children[0]))
        .Add(CaseBody(arm)).Add(Synthetic("Else", arm).Add(chain));
    }
    EmitStatementAsBlock(chain!, sb, scope, indent);
  }

  bool quietIs;   // the checks of a type switch: an upward one is not a mistake
}
