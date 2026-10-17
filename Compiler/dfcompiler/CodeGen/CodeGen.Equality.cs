using System.Text;
using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// Equality of objects (spec 11.1.3). == resolves in this order: equals, an operator == overload, identity.
// equals(T other) is generic: T is the type of the right operand, so the compiler generates one copy of the
// function for each type it is used with. "T is type" is decided at compile time (the other branch is not emitted).
// The copy to call is found at run time from the class of the left operand, through a table with one entry per class.
public sealed partial class CodeGen {
  string? currentGenericT;                                      // the T of the equals copy being compiled
  readonly Dictionary<(ClassInfo, string), FunSig> equalsInstances = new();
  readonly List<FunSig> pendingInstances = new();               // copies that still have to be compiled
  readonly List<string> equalsTables = new();                   // the types T that have a table
  readonly HashSet<Node> warnedBranches = new();

  static string SanitizeName(string type) => Regex.Replace(type, @"[^A-Za-z0-9_]", "_");

  static bool IsGenericEquals(Node member) {
    var parameters = member.Children.FirstOrDefault(child => child.Kind == "Params");
    return member.Text == "equals" && parameters is { Children.Count: 1 }
      && parameters.Children[0].Kind == "Param" && parameters.Children[0].Children[0] is { Kind: "Type", Text: "T", Children.Count: 0 };
  }

  // equals(T other) of a class: no normal registration, only the node is kept
  void RegisterEquals(ClassInfo cls, Node member) {
    var (_, _, isStatic, isConst, isOverride) = ReadMods(member);
    if (isStatic || isConst) {
      throw Error("equals cannot be static", member);
    }
    if (!isOverride) {
      throw Error("equals is defined by Root.Class: write 'override'", member);
    }
    var returns = member.Children.FirstOrDefault(child => child.Kind == "Returns");
    if (returns is not { Children.Count: 1 } || returns.Children[0] is not { Kind: "Type", Text: "bool" }) {
      throw Error("equals returns bool", member);
    }
    if (member.Children.Any(child => child.Kind == "NoBody")) {
      throw Error("equals needs a body", member);
    }
    if (cls.EqualsNode != null) {
      throw Error($"{cls.Name} already defines equals", member);
    }
    if (!AlwaysReturns(member.Children.First(child => child.Kind == "Block").Children)) {
      throw Error("every path of equals must return a value", member);
    }
    cls.EqualsNode = member;
  }

  // does every path of these statements end in return or throw? (an 'if' counts only with an 'else')
  static bool AlwaysReturns(List<Node> statements) {
    if (statements.Count == 0) {
      return false;
    }
    var last = statements[^1];
    switch (last.Kind) {
      case "Return":
      case "Throw":
        return true;
      case "Block":
        return AlwaysReturns(last.Children);
      case "If": {
        if (last.Children.Count < 3) {
          return false;
        }
        var otherwise = last.Children[2].Children[0];
        return AlwaysReturns(last.Children[1].Children)
          && (otherwise.Kind == "If" ? AlwaysReturns(new List<Node> { otherwise }) : AlwaysReturns(otherwise.Children));
      }
      default:
        return false;
    }
  }

  // == written in a class: fun bool ==(V other)
  void CheckOperatorEquals(FunSig f, Node member) {
    if (f.Ret != "bool" || f.Params.Count != 1) {
      throw Error("the == operator is 'fun bool ==(V other)'", member);
    }
    f.Visibility = Vis.Public;
  }

  void WarnEqualityTwice(ClassInfo cls) {
    if (cls.EqualsNode != null && funs.ContainsKey(cls.Name + ".==")) {
      Warn($"{cls.Name} defines equals and the operator ==: the operator is never used", cls.Node, 2);
    }
  }

  ClassInfo? FindEqualsClass(ClassInfo cls) {
    for (var c = cls; c != null; c = c.Parent) {
      if (c.EqualsNode != null) {
        return c;
      }
    }
    return null;
  }

  FunSig EqualsInstance(ClassInfo definer, string type) {
    if (equalsInstances.TryGetValue((definer, type), out var existing)) {
      return existing;
    }
    var f = new FunSig($"{definer.Name}.equals_{SanitizeName(type)}", "bool",
      new List<ParamSig> { new("other", type, null) }, definer.EqualsNode!, null) {
      Class = definer,
      GenericT = type,
      Visibility = Vis.Public
    };
    equalsInstances[(definer, type)] = f;
    pendingInstances.Add(f);
    return f;
  }

  string EqualsTable(string type) {
    if (!equalsTables.Contains(type)) {
      equalsTables.Add(type);
      foreach (var cls in classOrder.Where(cls => cls.EqualsNode != null)) {
        EqualsInstance(cls, type);
      }
    }
    return "df_eqt_" + SanitizeName(type);
  }

  // a == b where a is an object
  string ObjectEquals(Expr l, Expr r, Node at) {
    if (r.Type == "null") {
      return $"(({l.Code}) == NULL)";
    }
    var cls = classes.TryGetValue(l.Type, out var found) ? found : null;
    if (cls != null) {
      if (FindEqualsClass(cls) != null && !IsNullable(r.Type)) {
        return EqualsByTable(l, r, cls);
      }
      var op = OperatorFor(cls, r.Type);
      if (op != null) {
        return EqualsByOperator(l, r, cls, op);
      }
    }
    if (!IsClass(r.Type)) {
      throw Error($"cannot compare {l.Type} and {r.Type}", at);
    }
    bool related = IsSubtype(l.Type, r.Type) || IsSubtype(r.Type, l.Type) || IsTraitType(l.Type) || IsTraitType(r.Type);
    if (!related) {
      throw Error($"cannot compare {l.Type} and {r.Type}", at);
    }
    return $"((const void*)({l.Code}) == (const void*)({r.Code}))";
  }

  FunSig? OperatorFor(ClassInfo cls, string rightType) {
    var candidates = LookupMethods(cls, "==").Where(f => !f.IsStatic && f.Params.Count == 1 && Assignable(f.Params[0].Type, rightType)).ToList();
    return candidates.FirstOrDefault(f => f.Params[0].Type == rightType)
      ?? candidates.OrderBy(f => IsClass(rightType) && IsClass(f.Params[0].Type) ? ClassDistance(rightType, f.Params[0].Type) : 0).FirstOrDefault();
  }

  // null is handled here: equals and == never receive null
  string NullGuarded(string left, string right, string rightType, string call) {
    return IsClass(rightType)
      ? $"(({left} == NULL || {right} == NULL) ? ((const void*){left} == (const void*){right}) : {call})"
      : $"({left} == NULL ? false : {call})";
  }

  string EqualsByTable(Expr l, Expr r, ClassInfo cls) {
    string table = EqualsTable(r.Type);
    string left = NewTemp();
    string right = NewTemp();
    string valueType = CType(r.Type);
    string call = $"((bool(*)(void*, {valueType}))({table}[{left}->cls->id - 256]))((void*){left}, {right})";
    string code = $"({{ {CType(cls.Name)} {left} = {l.Code}; {valueType} {right} = {r.Code}; {NullGuarded(left, right, r.Type, call)}; }})";
    return CheckRaise(code, "bool");
  }

  string EqualsByOperator(Expr l, Expr r, ClassInfo cls, FunSig op) {
    string left = NewTemp();
    string right = NewTemp();
    string paramType = op.Params[0].Type;
    string argument = Convert(new Expr(right, r.Type), paramType, op.Node);
    string declarer = CType(op.Class!.Name);
    string call;
    if (op.Root.GroupSize > 0) {
      call = $"((bool(*)({declarer}, {CType(paramType)}))({left}->cls->vtable[{op.Slot}]))(({declarer}){left}, {argument})";
    } else {
      call = $"{op.CName}(({declarer}){left}, {argument})";
    }
    string code = $"({{ {CType(cls.Name)} {left} = {l.Code}; {CType(r.Type)} {right} = {r.Code}; {NullGuarded(left, right, r.Type, call)}; }})";
    return failingFuns.Contains(op) ? CheckRaise(code, "bool") : code;
  }

  // if T is type ... : decided now, while the copy of equals for this T is compiled
  bool TypeBranch(Node s, StringBuilder sb, Scope scope, int indent) {
    var condition = s.Children[0];
    if (currentGenericT == null || condition.Kind != "Is" || condition.Text != null
        || condition.Children[0] is not { Kind: "Name", Text: "T" } || scope.Find("T") != null) {
      return false;
    }
    string target = ResolveType(condition.Children[1]);
    bool matches = currentGenericT == target || (IsClass(currentGenericT) && IsClass(target) && IsSubtype(currentGenericT, target));
    // it matches this T, but an earlier check already did and returned: this branch can never run
    if (matches && assign.Unreachable && warnedBranches.Add(s)) {
      Warn("this check can never run: an earlier check always matches", s, 3);
    }
    string pad = Pad(indent);
    if (s.Kind == "IfThen") {
      if (matches) {
        sb.Append($"{pad}{{\n");
        EmitStatementAsBlock(s.Children[1], sb, scope, indent + 1);
        sb.Append($"{pad}}}\n");
      }
      return true;
    }
    if (matches) {
      sb.Append($"{pad}{{\n");
      EmitBlock(s.Children[1], sb, scope, indent + 1);
      sb.Append($"{pad}}}\n");
    } else if (s.Children.Count > 2) {
      var otherwise = s.Children[2].Children[0];
      if (otherwise.Kind == "If") {
        EmitStmt(otherwise, sb, scope, indent);
      } else {
        sb.Append($"{pad}{{\n");
        EmitBlock(otherwise, sb, scope, indent + 1);
        sb.Append($"{pad}}}\n");
      }
    }
    return true;
  }

  // the tables: one function per class for each type T, found through the class number
  void EmitEqualsTables() {
    var ordered = classes.Values.OrderBy(cls => cls.Index).ToList();
    foreach (string type in equalsTables) {
      string valueType = CType(type);
      var entries = new List<string>();
      foreach (var cls in ordered) {
        var definer = FindEqualsClass(cls);
        entries.Add(definer == null ? "NULL" : $"(bool(*)(void*, {valueType})){equalsInstances[(definer, type)].CName}");
      }
      classCode.Append($"static bool (*const df_eqt_{SanitizeName(type)}[])(void*, {valueType}) = {{ {string.Join(", ", entries)} }};\n");
    }
    classCode.Append('\n');
  }
}
