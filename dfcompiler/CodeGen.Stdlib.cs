using dfparser;

namespace dfcompiler;

// One standard library function (spec 18). Params are Dflat type names; "any" takes any value as text.
// A variadic function repeats Params[0] (MinArgs or more) and folds the C function over the arguments.
sealed record StdFun(string[] Params, string Ret, string CName, bool Variadic = false, int MinArgs = 0);

// Standard library: Console, Time, Math, Math32, Random. Built into the compiler; the C side is in Runtime.cs.
public sealed partial class CodeGen {
  static readonly string[] IntTypes = { "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64" };

  static readonly Dictionary<string, string> RealUnary = new() {
    ["Sqrt"] = "sqrt", ["Cbrt"] = "cbrt", ["Sin"] = "sin", ["Cos"] = "cos", ["Tan"] = "tan",
    ["Asin"] = "asin", ["Acos"] = "acos", ["Atan"] = "atan", ["Sinh"] = "sinh", ["Cosh"] = "cosh",
    ["Tanh"] = "tanh", ["Exp"] = "exp", ["Ln"] = "log", ["Log10"] = "log10", ["Floor"] = "floor",
    ["Ceil"] = "ceil", ["Round"] = "round", ["Trunc"] = "trunc", ["Abs"] = "fabs"
  };

  static readonly Dictionary<string, string> RealBinary = new() {
    ["Atan2"] = "atan2", ["Pow"] = "pow", ["Fmod"] = "fmod"
  };

  static readonly Dictionary<string, List<StdFun>> Stdlib = BuildStdlib();

  static Dictionary<string, List<StdFun>> BuildStdlib() {
    var table = new Dictionary<string, List<StdFun>>();
    void Add(string path, StdFun fun) {
      if (!table.TryGetValue(path, out var overloads)) {
        overloads = new List<StdFun>();
        table[path] = overloads;
      }
      overloads.Add(fun);
    }
    StdFun Fixed(string ret, string cname, params string[] ps) => new(ps, ret, cname);

    Add("Root.Console.Log", Fixed("void", "df_console_log", "any"));
    Add("Root.Console.LogError", Fixed("void", "df_console_log_error", "any"));
    Add("Root.Console.Print", Fixed("void", "df_console_print", "any"));
    Add("Root.Console.Read", Fixed("char", "df_console_get_char"));
    Add("Root.Console.ReadLine", Fixed("string", "df_console_get_input"));

    Add("Root.Time.Now", Fixed("date", "df_time_now"));
    Add("Root.Time.Clock", Fixed("clock", "df_time_clock"));

    Add("Root.Random.Seed", Fixed("void", "df_rand_seed", "u64"));
    Add("Root.Random.Int", Fixed("i64", "df_rand_int", "i64", "i64"));
    Add("Root.Random.RealRange", Fixed("r64", "df_rand_real_range", "r64", "r64"));
    Add("Root.Random.Real", Fixed("r64", "df_rand_real"));

    foreach (var (ns, rt) in new[] { ("Math", "r64"), ("Math32", "r32") }) {
      string f = rt == "r32" ? "f" : "";
      string root = "Root." + ns + ".";
      foreach (var (name, c) in RealUnary) {
        Add(root + name, Fixed(rt, c + f, rt));
      }
      foreach (var (name, c) in RealBinary) {
        Add(root + name, Fixed(rt, c + f, rt, rt));
      }
      Add(root + "Log", Fixed(rt, "df_logbase_" + rt, rt, rt));
      Add(root + "Sign", Fixed(rt, "df_sign_" + rt, rt));
      Add(root + "Clamp", Fixed(rt, "df_clamp_" + rt, rt, rt, rt));
      Add(root + "Lerp", Fixed(rt, "df_lerp_" + rt, rt, rt, rt));
      Add(root + "InverseLerp", Fixed(rt, "df_invlerp_" + rt, rt, rt, rt));
      Add(root + "Min", new StdFun(new[] { rt }, rt, "df_min_" + rt, true, 2));
      Add(root + "Max", new StdFun(new[] { rt }, rt, "df_max_" + rt, true, 2));
      Add(root + "Hypot", new StdFun(new[] { rt }, rt, "hypot" + f, true, 1));
    }

    // integer versions (Math only): Abs and Sign for signed types, Min, Max and Clamp for all
    foreach (string t in IntTypes) {
      Add("Root.Math.Min", new StdFun(new[] { t }, t, "df_min_" + t, true, 2));
      Add("Root.Math.Max", new StdFun(new[] { t }, t, "df_max_" + t, true, 2));
      Add("Root.Math.Clamp", Fixed(t, "df_clamp_" + t, t, t, t));
      if (SignedTypes.Contains(t)) {
        Add("Root.Math.Abs", Fixed(t, "df_abs_" + t, t));
        Add("Root.Math.Sign", Fixed(t, "df_sign_" + t, t));
      }
    }
    return table;
  }

  Expr StdCall(string path, List<StdFun> overloads, List<Node> args, Scope scope, Node at) {
    if (args.Any(arg => arg.Kind == "Named")) {
      throw Error("named arguments are not supported here", at);
    }
    bool CountFits(StdFun f) => f.Variadic ? args.Count >= f.MinArgs : args.Count == f.Params.Length;
    var sameCount = overloads.Where(CountFits).ToList();
    if (sameCount.Count == 0) {
      var first = overloads[0];
      string expected = first.Variadic ? $"at least {first.MinArgs}" : first.Params.Length.ToString();
      throw Error($"{path} takes {expected} argument(s), not {args.Count}", at);
    }

    StdFun chosen;
    if (sameCount.Count == 1) {
      chosen = sameCount[0];
    } else {
      var costs = new List<(StdFun Fun, int Cost)>();
      foreach (var f in sameCount) {
        int total = 0;
        bool fits = true;
        for (int index = 0; index < args.Count && fits; index++) {
          string paramType = f.Variadic ? f.Params[0] : f.Params[index];
          int? cost = ArgCost(args[index], paramType, scope);
          if (cost == null) {
            fits = false;
          } else {
            total += cost.Value;
          }
        }
        if (fits) {
          costs.Add((f, total));
        }
      }
      if (costs.Count == 0) {
        throw Error($"no overload of {path} matches these arguments", at);
      }
      int best = costs.Min(entry => entry.Cost);
      var winners = costs.Where(entry => entry.Cost == best).ToList();
      if (winners.Count != 1) {
        throw Error($"ambiguous call to {path}; use a cast or a suffix on the literal", at);
      }
      chosen = winners[0].Fun;
    }

    var codes = new List<string>();
    for (int index = 0; index < args.Count; index++) {
      string paramType = chosen.Variadic ? chosen.Params[0] : chosen.Params[index];
      codes.Add(paramType == "any"
        ? StrOf(ExprOf(args[index], scope), args[index])
        : Convert(ExprOf(args[index], scope, paramType), paramType, args[index]));
    }
    string call;
    if (chosen.Variadic) {
      call = codes[0];
      for (int index = 1; index < codes.Count; index++) {
        call = $"{chosen.CName}({call}, {codes[index]})";
      }
    } else {
      call = $"{chosen.CName}({string.Join(", ", codes)})";
    }
    return new Expr(call, chosen.Ret);
  }
}

// Operators on date, clock and lapse (spec 3.2, 3.3).
public sealed partial class CodeGen {
  Expr TimeOp(string op, Expr l, Expr r, Node at) {
    if (op is "==" or "!=") {
      string equal = EqualsCode(l, r, at);
      return new Expr(op == "==" ? equal : $"(!{equal})", "bool");
    }
    if (l.Type == "null") {
      l = new Expr(NullCode(r.Type), r.Type);
    }
    if (r.Type == "null") {
      r = new Expr(NullCode(l.Type), l.Type);
    }
    if (l.Type != r.Type) {
      throw Error($"cannot apply '{op}' to {l.Type} and {r.Type}", at);
    }
    if (op is "<" or "<=" or ">" or ">=") {
      if (l.Type == "lapse") {
        throw Error("lapses of different units cannot be ordered yet", at);
      }
      return new Expr($"({l.Code} {op} {r.Code})", "bool");   // a null date or clock sorts first
    }
    if (op == "-" && l.Type == "date") {
      return new Expr($"df_date_diff({l.Code}, {r.Code})", "lapse");
    }
    if (op == "-" && l.Type == "clock") {
      return new Expr($"df_clock_diff({l.Code}, {r.Code})", "lapse");
    }
    throw Error($"operator '{op}' doesn't apply to {l.Type} and {r.Type}", at);
  }
}
