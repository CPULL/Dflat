using System.Numerics;
using dfparser;

namespace dfcompiler;

// Which variables may still be unassigned at this point of the code (spec 4, definite assignment).
sealed class AssignState {
  public HashSet<VarInfo> Unassigned { get; private set; } = new();
  public bool Unreachable { get; set; }

  public AssignState Clone() {
    return new AssignState { Unassigned = new HashSet<VarInfo>(Unassigned), Unreachable = Unreachable };
  }

  // after branches: a variable is assigned only if every branch that continues assigned it
  public static AssignState Merge(IEnumerable<AssignState> states) {
    var reachable = states.Where(state => !state.Unreachable).ToList();
    var merged = new AssignState { Unreachable = reachable.Count == 0 };
    foreach (var state in reachable) {
      merged.Unassigned.UnionWith(state.Unassigned);
    }
    return merged;
  }
}

// Unsupported features, overloading, named arguments, read-before-assignment warnings.
public sealed partial class CodeGen {
  static readonly Dictionary<string, string> NodeFeatures = new() {
    ["Class"] = "Classes",
    ["Trait"] = "Classes",
    ["Constructor"] = "Classes",
    ["Placeholder"] = "Classes",
    ["Is"] = "Classes",
    ["Specific"] = "Classes",
    ["Import"] = "Imports",
    ["FunVar"] = "Lambdas",
    ["List"] = "Collections",
    ["Map"] = "Collections",
    ["GenericRef"] = "Generics",
    ["GenericParams"] = "Generics",
    ["Where"] = "Generics",
    ["ValueArg"] = "Generics",
    ["WritableAddressOf"] = "Pointers",
    ["Volatile"] = "Pointers",
    ["Copy"] = "Ownership"
  };

  static readonly Dictionary<string, string> TypeFeatures = new() {
    ["map"] = "Collections",
    ["dfoot"] = "Collections",
    ["var"] = "var",
    ["i128"] = "i128",
    ["i256"] = "i256",
    ["u128"] = "u128",
    ["u256"] = "u256",
    ["r16"] = "r16",
    ["r128"] = "r128",
    ["dec"] = "dec"
  };

  static readonly Dictionary<string, string> ParamFeatures = new();

  static CompileException NotSupported(string feature, Node at) {
    return Error($"feature not supported: {feature}", at);
  }

  // unsupported top-level declarations, reported in source order before anything else
  static void CheckTopLevelFeatures(Node program) {
    foreach (var statement in program.Children) {
      if (statement.Kind is "Import" or "FunVar") {
        throw NotSupported(NodeFeatures[statement.Kind], statement);
      }
    }
  }

  // ---------------------------------------------------------------- read before assignment

  AssignState assign = new();
  readonly HashSet<VarInfo> warnedUnassigned = new();
  int atEndDepth;

  void ReadCheck(VarInfo? variable, string name, Node at) {
    if (variable == null || atEndDepth > 0 || assign.Unreachable) {
      return;
    }
    if (assign.Unassigned.Contains(variable) && warnedUnassigned.Add(variable)) {
      Warn($"'{name}' is read before it is assigned", at, 1);
    }
  }

  void MarkAssigned(VarInfo? variable) {
    if (variable != null) {
      assign.Unassigned.Remove(variable);
    }
  }

  // no shadowing: a local with the same name as one in an enclosing block is an error
  static void CheckNotShadowing(string name, Scope scope, Node at) {
    if (scope.Find(name) != null) {
      throw Error($"'{name}' is already declared in this block or an enclosing one", at);
    }
  }

  // ---------------------------------------------------------------- calls: named arguments and overloading

  // the argument node for every parameter (or its default), or null with the reason
  static (Node?[]? Slots, string? Problem) MapArgs(FunSig f, List<Node> args) {
    var slots = new Node?[f.Params.Count];
    foreach (var arg in args.Where(arg => arg.Kind == "Named")) {
      int index = f.Params.FindIndex(param => param.Name == arg.Text);
      if (index < 0) {
        return (null, $"'{f.Name}' has no parameter '{arg.Text}'");
      }
      if (f.Params[index].IsVariadic) {
        return (null, $"the variadic parameter '{arg.Text}...' cannot be named in a call: give its items after the other arguments (spec 13.4)");
      }
      if (slots[index] != null) {
        return (null, $"parameter '{arg.Text}' is given twice");
      }
      slots[index] = arg.Children[0];
    }
    // T name...: the positional arguments past the other parameters make the array
    int variadicIndex = f.Params.Count > 0 && f.Params[^1].IsVariadic ? f.Params.Count - 1 : -1;
    var extra = new List<Node>();
    int next = 0;
    foreach (var arg in args.Where(arg => arg.Kind != "Named")) {
      while (next < slots.Length && slots[next] != null) {
        next++;
      }
      if (variadicIndex >= 0 && next >= variadicIndex && slots[variadicIndex] == null) {
        extra.Add(arg);
        continue;
      }
      if (next == slots.Length) {
        return (null, $"too many arguments for '{f.Name}'");
      }
      slots[next] = arg;
      next++;
    }
    if (variadicIndex >= 0 && slots[variadicIndex] == null) {
      var list = Synthetic("List", extra.Count > 0 ? extra[0] : f.Node);
      foreach (var arg in extra) {
        list.Add(arg);
      }
      slots[variadicIndex] = list;
    }
    for (int index = 0; index < slots.Length; index++) {
      if (slots[index] == null) {
        if (f.Params[index].Default == null) {
          return (null, $"missing argument '{f.Params[index].Name}' for '{f.Name}'");
        }
      }
    }
    return (slots, null);
  }

  // cost of passing this argument to this parameter type (spec 9.2), null when it can't be passed
  int? ArgCost(Node arg, string paramType, Scope scope) {
    string target = BaseOf(paramType);
    var valueNode = arg.Kind is "Paren" or "Boxed" ? arg.Children[0] : arg;
    int counted = BoxArgCost(valueNode, paramType, scope);
    if (counted == -1) {
      counted = CountedArgCost(valueNode, paramType, scope);
    }
    if (counted != -1) {
      return counted < 0 ? null : counted;   // a counted parameter, or a counted argument (spec 12.7)
    }
    if (IsTuple(paramType) && valueNode.Kind == "Tuple") {
      // (1, 2) given to a (i32, i32) parameter: the literal takes the types of the parameter
      try {
        return ExprOf(valueNode, scope, paramType).Type == paramType ? 0 : null;
      } catch (CompileException) {
        return null;
      }
    }
    if (IsSetValue(paramType)) {
      if (valueNode.Kind == "Tuple" && SetMembers(paramType).Any(member => IsTuple(member) && TryFits(valueNode, scope, member))) {
        return 0;   // a tuple literal fits a tuple member of the set (spec 11.3.6.1)
      }
      // a value of the set, a member value (a literal by the rule of 11.3.6.1): it fits; anything else does not
      try {
        var given = ExprOf(valueNode, scope, paramType);
        return given.Type == paramType || SetMembers(paramType).Contains(given.Type) ? 0 : null;
      } catch (CompileException) {
        return null;
      }
    }
    if (IsUntypedLiteral(valueNode)) {
      bool isInteger = (valueNode.Kind == "Neg" ? valueNode.Children[0] : valueNode).Kind == "Int";
      if (isInteger) {
        // the native-size signed type wins, even over an exact smaller type
        if (target == NativeInt) {
          return 0;
        }
        if (IsInt(target)) {
          var value = IntLiteralValue(valueNode) ?? 0;
          var (min, max) = IntRange(target);
          return value >= min && value <= max ? 1 : null;
        }
        return RealTypes.Contains(target) ? 2 : target == "lapse" ? 3 : null;
      }
      return target == "r64" ? 0 : target == "r32" ? 1 : target == "lapse" ? 3 : null;
    }
    if (valueNode.Kind == "WritableAddressOf") {
      valueNode = valueNode.Children[0];   // &x: the type of x
    }
    if (valueNode.Kind is "List" or "MapLit" or "EmptyNew") {
      // a literal has the type of what receives it (spec 6.3): typed as this parameter, or it does not fit (point 53)
      try {
        var literal = ExprOf(valueNode, scope, paramType);
        // a bare [...] is an arr (6.3): when several collections fit, the arr wins
        return literal.Type == paramType ? (IsArr(paramType) || valueNode.Kind is "MapLit" or "EmptyNew" ? 0 : 1) : Assignable(paramType, literal.Type) ? 100 : null;
      } catch (CompileException) {
        return null;
      }
    }
    var type = ExprOf(valueNode, scope).Type;
    if (type == paramType) {
      return 0;
    }
    if (IsClass(type) && IsClass(target)) {
      return IsSubtype(type, target) ? 10 + ClassDistance(type, target) : null;   // the nearest parent wins
    }
    if (IsNumeric(type) && IsNumeric(target)) {
      // exact type, then the smallest same-family widening; other families are not candidates
      if (Family(type) != Family(target) || Bits(type) > Bits(target)) {
        return null;
      }
      return Bits(target) - Bits(type) + (IsNullable(paramType) ? 1 : 0);
    }
    return Assignable(paramType, type) ? 100 : null;
  }

  // the argument goes to a parameter of this type, through the automatic conversions too (spec 3.5.1)
  bool AutoFits(Node arg, string paramType, Scope scope) {
    if (ArgCost(arg, paramType, scope) != null) {
      return true;
    }
    if (arg.Kind is "List" or "MapLit" or "EmptyNew" or "Lambda" or "Gift" or "WritableAddressOf" or "Null" || IsUntypedLiteral(arg)) {
      return false;
    }
    try {
      return AutoAssignable(paramType, ExprOf(arg, scope).Type);
    } catch (CompileException) {
      return false;
    }
  }

  // the versions that the arguments reach only through the automatic conversions of numbers (spec 3.5.1)
  List<FunSig> AutoReached(List<FunSig> candidates, List<Node> args, Scope scope) =>
    candidates.Where(candidate => MapArgs(candidate, args).Item1 is Node[] slots && args.All(arg => {
      var argNode = arg.Kind == "Named" ? arg.Children[0] : arg;
      int paramIndex = Array.IndexOf(slots, argNode);
      return AutoFits(argNode, paramIndex >= 0 ? candidate.Params[paramIndex].Type : ArrElem(candidate.Params[^1].Type), scope);
    })).ToList();

  FunSig ResolveOverload(List<FunSig> candidates, List<Node> args, Scope scope, Node at) {
    if (candidates.Count == 1) {
      return candidates[0];
    }
    var applicable = new List<(FunSig Fun, int[] Costs)>();
    foreach (var candidate in candidates) {
      var (slots, _) = MapArgs(candidate, args);
      if (slots == null) {
        continue;
      }
      var costs = new int[args.Count];
      bool fits = true;
      for (int argIndex = 0; argIndex < args.Count && fits; argIndex++) {
        var arg = args[argIndex];
        var argNode = arg.Kind == "Named" ? arg.Children[0] : arg;
        int paramIndex = Array.IndexOf(slots, argNode);
        string paramType = paramIndex >= 0 ? candidate.Params[paramIndex].Type : ArrElem(candidate.Params[^1].Type);
        int? cost = ArgCost(argNode, paramType, scope);
        if (cost == null && paramIndex < 0 && LendsWholeVariadic(ExprOf(argNode, scope).Type, candidate.Params[^1].Type)) {
          cost = 0;   // an arr, a vec or a view lent whole to T name... (spec 13.4)
        }
        if (cost == null) {
          fits = false;
        } else {
          costs[argIndex] = cost.Value;
        }
      }
      if (fits) {
        applicable.Add((candidate, costs));
      }
    }
    if (applicable.Count == 0) {
      var reached = AutoReached(candidates, args, scope);
      if (reached.Count > 1 && reached.Any(f => !f.Params.Any(param => param.IsVariadic))) {
        reached.RemoveAll(f => f.Params.Any(param => param.IsVariadic));
      }
      if (reached.Count == 1) {
        return reached[0];
      }
      if (reached.Count > 1) {
        throw Error($"ambiguous call: through automatic conversions it matches {string.Join(", ", reached.Select(FunHeader))}; write the cast (spec 3.5.1)", at);
      }
      if (candidates[0].Class is ClassInfo owner && IsSet(owner.Name) && args.Count == 1 && args[0].Kind != "Named") {
        // add and its overloads for collections (CodeGen.Sets.cs): say what the set takes
        string given = DisplayName(ExprOf(args[0], scope).Type);
        throw Error($"the items of this set are {DisplayName(MapKey(owner.Name))}, not {given}", at);
      }
      throw Error($"no '{candidates[0].Name}' matches these arguments", at);
    }
    if (applicable.Any(entry => !entry.Fun.Params.Any(param => param.IsVariadic))) {
      applicable.RemoveAll(entry => entry.Fun.Params.Any(param => param.IsVariadic));   // the non-variadic overload wins (spec 13.4)
    }
    bool Dominates((FunSig Fun, int[] Costs) better, (FunSig Fun, int[] Costs) worse) {
      bool strictly = false;
      for (int index = 0; index < better.Costs.Length; index++) {
        if (better.Costs[index] > worse.Costs[index]) {
          return false;
        }
        strictly |= better.Costs[index] < worse.Costs[index];
      }
      return strictly;
    }
    var best = applicable.Where(entry => !applicable.Any(other => Dominates(other, entry))).ToList();
    if (best.Count != 1) {
      string options = string.Join(", ", best.Select(entry => FunHeader(entry.Fun)));
      if (best.All(entry => entry.Fun.Class is { IsTrait: true }) && best.Select(entry => entry.Fun.Class).Distinct().Count() == best.Count) {
        // the same function in two traits of the class (two copies of a generic trait too): the trait is named (spec 11.2)
        string bare = best[0].Fun.Name[(best[0].Fun.Name.LastIndexOf('.') + 1)..];
        throw Error($"'{bare}' is in {string.Join(" and ", best.Select(entry => DisplayName(entry.Fun.Class!.Name)))}: name the trait, x.Trait.{bare}(...) "
          + $"(an alias for a copy of a generic trait, spec 11.2), or replace it in the class", at);
      }
      throw Error($"ambiguous call: it matches {options}", at);
    }
    return best[0].Fun;
  }

  // ---------------------------------------------------------------- switch case ranges

  // subject in a..b / a..=b / ..b / a..
  string RangeCondition(Expr subject, Node range, Scope scope, string subjectType) {
    var parts = new List<string>();
    var low = range.Children[0];
    var high = range.Children[1];
    if (low.Kind != "Open") {
      parts.Add(Combine(">=", subject, CaseValueExpr(low, scope, subjectType), range).Code);
    }
    if (high.Kind != "Open") {
      parts.Add(Combine(range.Text == "..=" ? "<=" : "<", subject, CaseValueExpr(high, scope, subjectType), range).Code);
    }
    return parts.Count == 0 ? "true" : "(" + string.Join(" && ", parts) + ")";
  }

  // constant integer bounds of a case range (inclusive); null bound = open
  (BigInteger? Low, BigInteger? High)? ConstRange(Node range, string subjectType) {
    BigInteger? Bound(Node node) {
      if (node.Kind == "Open") {
        return null;
      }
      string? key = ConstKey(node, subjectType);
      return key != null && key.StartsWith("n:") ? BigInteger.Parse(key[2..]) : throw new InvalidOperationException();
    }
    try {
      var low = Bound(range.Children[0]);
      var high = Bound(range.Children[1]);
      if (high != null && range.Text == "..") {
        high -= 1;
      }
      return (low, high);
    } catch (InvalidOperationException) {
      return null;
    }
  }

  // every integer (or char) value of the subject type is covered by constants and ranges
  bool CoversAllIntegers(string subjectType, IEnumerable<(BigInteger? Low, BigInteger? High)> intervals) {
    string baseType = BaseOf(subjectType);
    BigInteger min;
    BigInteger max;
    if (IsInt(baseType)) {
      (min, max) = IntRange(baseType);
    } else if (baseType == "char") {
      (min, max) = (0, 0x10FFFF);
    } else {
      return false;
    }
    var sorted = intervals.Select(interval => (Low: interval.Low ?? min, High: interval.High ?? max))
      .Where(interval => interval.Low <= interval.High)
      .OrderBy(interval => interval.Low)
      .ToList();
    BigInteger next = min;
    foreach (var (low, high) in sorted) {
      if (low > next) {
        return false;
      }
      if (high + 1 > next) {
        next = high + 1;
      }
      if (next > max) {
        return true;
      }
    }
    return next > max;
  }
}
