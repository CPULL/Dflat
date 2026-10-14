using dfparser;

namespace dfcompiler;

// String, char, date and time functions (spec 3.3, 4.1, 4.2): calls on a value, "text".Trim(), c.IsDigit(), d.addDays(3).
public sealed partial class CodeGen {
  readonly HashSet<string> needs = new();          // runtime parts used: "text", "date"
  readonly HashSet<string> unicodeNeeds = new();   // Unicode tables used (UnicodeTables)

  void NeedChunk(string key) {
    if (key.StartsWith("u:")) {
      unicodeNeeds.Add(key[2..]);
      return;
    }
    needs.Add(key);
    if (key == "date") {
      needs.Add("text");
    }
  }

  // marks a runtime part as used and returns the code
  string Need(string key, string code) {
    NeedChunk(key);
    return code;
  }

  // the C source of the optional runtime parts, after the base runtime
  string OptionalRuntime() {
    var tables = new HashSet<string>(unicodeNeeds);
    if (needs.Contains("text")) {
      tables.UnionWith(new[] { "trim", "toupper", "tolower" });
    }
    var sb = new System.Text.StringBuilder();
    if (tables.Count > 0) {
      sb.Append(UnicodeTables.Generate(tables));
    }
    if (needs.Contains("text")) {
      sb.Append(RuntimeText.Source);
    }
    if (needs.Contains("date")) {
      sb.Append(RuntimeDate.Source);
    }
    return sb.ToString();
  }

  // ---------------------------------------------------------------- function tables

  // properties, not fields: the library tables are built in static initializers of several files
  static string[] TextNeed => new[] { "text" };
  static string[] DateNeed => new[] { "date" };
  static string[] NoParams => Array.Empty<string>();

  // type → function name → overloads (the receiver is not in Params)
  static readonly Dictionary<string, Dictionary<string, List<StdFun>>> Methods = BuildMethods();

  static StdFun Fun(string ret, string cname, string[] ps, string tail = "", string[]? needs = null, string? silent = null) {
    return new StdFun(ps, ret, cname, false, 0, tail, silent, needs);
  }

  static Dictionary<string, Dictionary<string, List<StdFun>>> BuildMethods() {
    var table = new Dictionary<string, Dictionary<string, List<StdFun>>>();
    void Add(string type, string name, StdFun fun) {
      if (!table.TryGetValue(type, out var byName)) {
        byName = new Dictionary<string, List<StdFun>>();
        table[type] = byName;
      }
      if (!byName.TryGetValue(name, out var overloads)) {
        overloads = new List<StdFun>();
        byName[name] = overloads;
      }
      overloads.Add(fun);
    }

    // strings (spec 4.2)
    Add("string", "Trim", Fun("string", "df_str_trim", NoParams, needs: TextNeed));
    Add("string", "Trim", Fun("string", "df_str_trim_set", new[] { "string" }, needs: TextNeed));
    Add("string", "TrimLeft", Fun("string", "df_str_trim_left", NoParams, needs: TextNeed));
    Add("string", "TrimLeft", Fun("string", "df_str_trim_left_set", new[] { "string" }, needs: TextNeed));
    Add("string", "TrimRight", Fun("string", "df_str_trim_right", NoParams, needs: TextNeed));
    Add("string", "TrimRight", Fun("string", "df_str_trim_right_set", new[] { "string" }, needs: TextNeed));
    Add("string", "Substr", Fun("string", "df_str_substr", new[] { "u32", "u32" }, needs: TextNeed));
    Add("string", "Replace", Fun("string", "df_str_replace", new[] { "string", "string" }, "true", TextNeed));
    Add("string", "Replace", Fun("string", "df_str_replace", new[] { "string", "string", "bool" }, needs: TextNeed));
    foreach (var (name, cname) in new[] {
      ("IndexOf", "df_str_indexof"), ("LastIndexOf", "df_str_lastindexof")
    }) {
      Add("string", name, Fun("i64", cname, new[] { "string" }, "true", TextNeed));
      Add("string", name, Fun("i64", cname, new[] { "string", "bool" }, needs: TextNeed));
      Add("string", name, Fun("i64", cname + "_c", new[] { "char" }, "true", TextNeed));
      Add("string", name, Fun("i64", cname + "_c", new[] { "char", "bool" }, needs: TextNeed));
    }
    foreach (var (name, cname) in new[] {
      ("Contains", "df_str_contains"), ("StartsWith", "df_str_startswith"), ("EndsWith", "df_str_endswith")
    }) {
      Add("string", name, Fun("bool", cname, new[] { "string" }, "true", TextNeed));
      Add("string", name, Fun("bool", cname, new[] { "string", "bool" }, needs: TextNeed));
    }
    Add("string", "Split", Fun("arr<string>", "df_str_split", new[] { "string" }, needs: TextNeed));
    Add("string", "ToLower", Fun("string", "df_str_tolower", NoParams, needs: TextNeed));
    Add("string", "ToUpper", Fun("string", "df_str_toupper", NoParams, needs: TextNeed));
    Add("string", "PadLeft", Fun("string", "df_str_pad_left", new[] { "u32" }, "32u", TextNeed));
    Add("string", "PadLeft", Fun("string", "df_str_pad_left", new[] { "u32", "char" }, needs: TextNeed));
    Add("string", "PadRight", Fun("string", "df_str_pad_right", new[] { "u32" }, "32u", TextNeed));
    Add("string", "PadRight", Fun("string", "df_str_pad_right", new[] { "u32", "char" }, needs: TextNeed));
    Add("string", "ToCStr", Fun("arr<u8>", "df_str_tocstr", NoParams, "0", TextNeed));
    Add("string", "ToCStr", Fun("arr<u8>", "df_str_tocstr", new[] { "StringEncoding" }, needs: TextNeed));

    // chars (spec 4.1)
    foreach (var (name, cname, table2) in new[] {
      ("IsLetter", "df_cp_is_letter", "letter"), ("IsDigit", "df_cp_is_digit", "digit"),
      ("IsLetterOrDigit", "df_cp_is_letterordigit", "letterordigit"), ("IsWhiteSpace", "df_cp_is_space", "space"),
      ("IsUpper", "df_cp_is_upper", "upper"), ("IsLower", "df_cp_is_lower", "lower"),
      ("IsHexDigit", "df_cp_is_hexdigit", "hexdigit"), ("IsPunctuation", "df_cp_is_punct", "punct"),
      ("IsSymbol", "df_cp_is_symbol", "symbol"), ("IsControl", "df_cp_is_control", "control"),
      ("IsAscii", "df_cp_is_ascii", "ascii")
    }) {
      Add("char", name, Fun("bool", cname, NoParams, needs: new[] { "u:" + table2 }));
    }
    Add("char", "ToUpper", Fun("char", "df_cp_toupper", NoParams, needs: new[] { "u:toupper" }));
    Add("char", "ToLower", Fun("char", "df_cp_tolower", NoParams, needs: new[] { "u:tolower" }));

    // dates and times (spec 3.3.1, 3.3.2)
    Add("date", "addYears", Fun("date", "df_date_add_years", new[] { "i64" }, needs: DateNeed));
    Add("date", "addMonths", Fun("date", "df_date_add_months", new[] { "i64" }, needs: DateNeed));
    foreach (var (name, unit) in new[] {
      ("Days", "86400000LL"), ("Hours", "3600000LL"), ("Minutes", "60000LL"), ("Seconds", "1000LL"), ("Milliseconds", "1LL")
    }) {
      Add("date", "add" + name, Fun("date", "df_date_add", new[] { "i64" }, unit, DateNeed));
      if (name != "Days") {
        Add("time", "add" + name, Fun("time", "df_time_add", new[] { "i64" }, unit, DateNeed));
      }
    }
    Add("date", "startOfDay", Fun("date", "df_date_startofday", NoParams, needs: DateNeed));

    // parameter names of the spec (4.2), for named arguments
    var names = new Dictionary<string, string[]> {
      ["Trim"] = new[] { "c" }, ["TrimLeft"] = new[] { "c" }, ["TrimRight"] = new[] { "c" },
      ["Substr"] = new[] { "start", "len" }, ["Replace"] = new[] { "toFind", "replacement", "caseSensitive" },
      ["IndexOf"] = new[] { "val", "caseSensitive" }, ["LastIndexOf"] = new[] { "val", "caseSensitive" },
      ["Contains"] = new[] { "val", "caseSensitive" }, ["StartsWith"] = new[] { "val", "caseSensitive" },
      ["EndsWith"] = new[] { "val", "caseSensitive" }, ["Split"] = new[] { "separator" },
      ["PadLeft"] = new[] { "totalWidth", "paddingChar" }, ["PadRight"] = new[] { "totalWidth", "paddingChar" },
      ["ToCStr"] = new[] { "encoding" }
    };
    foreach (var entry in table["string"].Where(entry => names.ContainsKey(entry.Key))) {
      for (int index = 0; index < entry.Value.Count; index++) {
        var fun = entry.Value[index];
        entry.Value[index] = fun with { Names = names[entry.Key].Take(fun.Params.Length).ToArray() };
      }
    }
    return table;
  }

  // constructors and static functions of date and time (spec 3.3.1, 3.3.2), added to the library table
  static void AddDateStdlib(Action<string, StdFun> add) {
    add("date", Fun("date", "df_date_from_str", new[] { "string" }, needs: DateNeed, silent: "InvalidDateTimeFormatException"));
    add("date", Fun("date", "df_date_from_i64", new[] { "i64" }, needs: DateNeed));
    add("date.fromUnixTime", Fun("date", "df_date_from_i64", new[] { "i64" }, needs: DateNeed));
    add("date.parse", Fun("date", "df_date_parse", new[] { "string" }, "INT64_MIN", DateNeed) with { Names = new[] { "text" } });
    add("date.parse", Fun("date", "df_date_parse_fmt", new[] { "string", "string" }, needs: DateNeed) with { Names = new[] { "text", "format" } });
    add("date.parse", Fun("date", "df_date_parse", new[] { "string", "date" }, needs: DateNeed) with { Names = new[] { "text", "default" } });
    add("date.parse", Fun("date", "df_date_parse_f", new[] { "string", "date", "string" }, needs: DateNeed) with { Names = new[] { "text", "default", "format" } });
    add("time", Fun("time", "df_time_from_str", new[] { "string" }, needs: DateNeed, silent: "InvalidDateTimeFormatException"));
    add("time", Fun("time", "df_time_from_i32", new[] { "i32" }, needs: DateNeed, silent: "OutOfRangeCastException"));
    add("time.parse", Fun("time", "df_time_parse", new[] { "string" }, "-1", DateNeed) with { Names = new[] { "text" } });
    add("time.parse", Fun("time", "df_time_parse_fmt", new[] { "string", "string" }, needs: DateNeed) with { Names = new[] { "text", "format" } });
    add("time.parse", Fun("time", "df_time_parse", new[] { "string", "time" }, needs: DateNeed) with { Names = new[] { "text", "default" } });
    add("time.parse", Fun("time", "df_time_parse_f", new[] { "string", "time", "string" }, needs: DateNeed) with { Names = new[] { "text", "default", "format" } });
  }

  // ---------------------------------------------------------------- calls on values

  // false for paths like Root.Console, date or Status: a type or a namespace, not a value
  bool IsValueReceiver(Node receiver, Scope scope) {
    string? path = Path(receiver);
    return path == null || scope.Find(path.Split('.')[0]) != null;
  }

  Expr? TryReceiverCall(Expr receiver, string name, List<Node> args, Scope scope, Node at) {
    if (IsArr(receiver.Type)) {
      return ArrayMethod(receiver, name, args, scope, at);
    }
    if (Methods.TryGetValue(receiver.Type, out var byName) && byName.TryGetValue(name, out var overloads)) {
      return StdCall($"{receiver.Type}.{name}", overloads, args, scope, at, receiver.Code);
    }
    return null;
  }

  // enums that belong to the library are declared when the program mentions them
  void AddPrelude(Node program) {
    bool Mentions(Node node, Func<Node, bool> test) {
      return test(node) || node.Children.Any(child => Mentions(child, test));
    }
    var declared = program.Children.Where(child => child.Kind == "Enum").Select(child => child.Text).ToHashSet();
    var source = new System.Text.StringBuilder();
    if (!declared.Contains("DayOfWeek")
        && Mentions(program, node => node.Text == "DayOfWeek" || (node.Kind == "Member" && node.Text == "dayOfWeek"))) {
      source.Append("enum DayOfWeek { Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday }\n");
    }
    if (!declared.Contains("StringEncoding")
        && Mentions(program, node => node.Text == "StringEncoding" || (node.Kind == "Member" && node.Text == "ToCStr"))) {
      source.Append("enum StringEncoding { UTF8, UTF16, UTF32, UTF16BE, UTF32BE, ASCII, ISO88591 }\n");
    }
    if (source.Length == 0) {
      return;
    }
    var library = new Parser(new Lexer(source.ToString()).Tokenize()).ParseProgram();
    program.Children.InsertRange(0, library.Children);
  }
}
