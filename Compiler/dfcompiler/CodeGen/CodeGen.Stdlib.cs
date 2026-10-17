using dfparser;

namespace dfcompiler;

// One standard library function (spec 18). Params are Dflat type names; "any" takes any value as text.
// A variadic function repeats Params[0] (MinArgs or more) and folds the C function over the arguments.
// Tail: C arguments added after the given ones (the defaults of omitted parameters).
// Silent: a silent standard exception the C function can leave a note of (spec 15.4).
// Needs: runtime parts the C function uses ("text", "date", "u:<unicode table>").
// Names: parameter names for named arguments (caseSensitive: false).
sealed record StdFun(string[] Params, string Ret, string CName, bool Variadic = false, int MinArgs = 0,
  string Tail = "", string? Silent = null, string[]? Needs = null, string[]? Names = null);

// Standard library: Program, Console, Time, Math, Math32, Random, the string and char functions and the date and time
// members. Built into the compiler; the C side is in Runtime.cs, RuntimeText.cs, RuntimeDate.cs and UnicodeTables.cs.
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

    Add("Root.GetArgs", Fixed("arr<string>", "df_get_args"));

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
    AddDateStdlib(Add);
    AddNumberStdlib(Add);
    return table;
  }

  // receiver: the C code of the value a method is called on (it becomes the first C argument)
  Expr StdCall(string path, List<StdFun> overloads, List<Node> args, Scope scope, Node at, string? receiver = null) {
    if (args.Any(arg => arg.Kind == "Named")) {
      // each overload orders the arguments its own way; the ones that cannot take these names drop out
      var viable = new List<(StdFun Fun, List<Node> Args)>();
      foreach (var overload in overloads) {
        var ordered = NamedOrder(overload, args);
        if (ordered != null) {
          viable.Add((overload, ordered));
        }
      }
      if (viable.Count == 0) {
        throw Error($"no overload of {path} takes these argument names", at);
      }
      var picked = viable.Count == 1 ? viable[0] : PickByCost(path, viable, scope, at);
      overloads = new List<StdFun> { picked.Fun };
      args = picked.Args;
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
    if (receiver != null) {
      codes.Add(receiver);
    }
    for (int index = 0; index < args.Count; index++) {
      string paramType = chosen.Variadic ? chosen.Params[0] : chosen.Params[index];
      if (chosen.CName.StartsWith("df_parse_") && (chosen.CName.EndsWith("_f") || chosen.CName.EndsWith("_fmt"))
          && index == args.Count - 1 && args[index].Kind == "String") {
        CheckParseFormat(FromCodePoints(Unescape(args[index].Text![1..^1], args[index])), args[index]);
      }
      codes.Add(paramType == "any"
        ? StrOf(ExprOf(args[index], scope), args[index])
        : Convert(ExprOf(args[index], scope, paramType), paramType, args[index]));
    }
    if (chosen.Tail != "") {
      codes.Add(chosen.Tail);
    }
    foreach (string need in chosen.Needs ?? Array.Empty<string>()) {
      NeedChunk(need);
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
    if (chosen.Silent != null) {
      call = SilentCall(call, chosen.Ret, chosen.Silent, at);
    }
    return new Expr(call, chosen.Ret);
  }

  // the arguments in the order of the parameters, or null when the names do not fit
  static List<Node>? NamedOrder(StdFun f, List<Node> args) {
    if (f.Names == null || f.Variadic) {
      return null;
    }
    var slots = new Node?[f.Params.Length];
    int next = 0;
    foreach (var arg in args.Where(arg => arg.Kind != "Named")) {
      if (next >= slots.Length) {
        return null;
      }
      slots[next++] = arg;
    }
    foreach (var arg in args.Where(arg => arg.Kind == "Named")) {
      int index = Array.IndexOf(f.Names, arg.Text);
      if (index < 0 || index >= slots.Length || slots[index] != null) {
        return null;
      }
      slots[index] = arg.Children[0];
    }
    return slots.Any(slot => slot == null) ? null : slots.Select(slot => slot!).ToList();
  }

  (StdFun Fun, List<Node> Args) PickByCost(string path, List<(StdFun Fun, List<Node> Args)> candidates, Scope scope, Node at) {
    var costs = new List<((StdFun Fun, List<Node> Args) Candidate, int Cost)>();
    foreach (var candidate in candidates) {
      int total = 0;
      bool fits = true;
      for (int index = 0; index < candidate.Args.Count && fits; index++) {
        int? cost = ArgCost(candidate.Args[index], candidate.Fun.Params[index], scope);
        if (cost == null) {
          fits = false;
        } else {
          total += cost.Value;
        }
      }
      if (fits) {
        costs.Add((candidate, total));
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
    return winners[0].Candidate;
  }

  // A call that may leave a note of a silent exception: raised only when a catch for it can be reached
  // from here (same function); otherwise the call just returns its fallback value (spec 15.4).
  string SilentCall(string call, string type, string exceptionName, Node at) {
    // several names, separated by commas: the call may leave a note of any of them; only the ones a catch can reach are raised
    var names = exceptionName.Split(',').ToList();
    var caught = names.Where(name => SilentCaught(ExceptionId(name) ?? throw Error($"unknown exception {name}", at))).ToList();
    string temp = NewTemp();
    if (caught.Count == 0) {
      return $"({{ {CType(type)} {temp} = {call}; df_silent_desc = NULL; {temp}; }})";
    }
    string note = NewTemp();
    string test = caught.Count == names.Count ? note : $"({string.Join(" || ", caught.Select(name => $"{note} == &df_desc_{name}"))})";
    return $"({{ {CType(type)} {temp} = {call}; const df_descriptor* {note} = df_silent_desc; df_silent_desc = NULL; "
      + $"if ({test}) {{ df_raise({note}, {fn!.NameConst}); {RaiseJump} }} {temp}; }})";
  }

  // is there a catch above this statement that handles this exception id (its own type, or catch all)?
  bool SilentCaught(int id) {
    if (handlerOverride != null || frames.Count == 0) {
      return false;
    }
    for (var frame = frames.Peek(); frame != null; frame = frame.Parent) {
      foreach (var (index, node) in frame.Catches) {
        if (index > frame.CurrentIndex) {
          var (caught, _) = CatchTarget(node);
          if (caught == null || caught == id || caught == -2) {
            return true;
          }
        }
      }
    }
    return false;
  }
}
