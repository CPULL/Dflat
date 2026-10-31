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
    if (key is "date" or "numfmt" or "strfmt") {
      needs.Add("text");
    }
    if (key == "strfmt") {
      unicodeNeeds.Add("space");
    }
    if (key == "hmap") {
      needs.Add("hash");   // the hash functions of the keys (RuntimeHash.cs)
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
    if (needs.Contains("sort")) {
      sb.Append(RuntimeSort.Source);
    }
    if (needs.Contains("map")) {
      sb.Append(RuntimeMap.Source);
    }
    if (needs.Contains("hash")) {
      sb.Append(RuntimeHash.Source);
    }
    if (needs.Contains("hmap")) {
      sb.Append(RuntimeHmap.Source);
    }
    if (needs.Contains("strfmt")) {
      sb.Append(RuntimeStringFormat.Source);
    }
    if (needs.Contains("date")) {
      sb.Append(RuntimeDate.Source);
    }
    if (needs.Contains("numfmt")) {
      sb.Append(RuntimeNumber.Source);
    }
    if (needs.Contains("io")) {
      sb.Append(RuntimeIO.Source);
    }
    if (needs.Contains("exec")) {
      sb.Append(RuntimeExec.Source);
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
    Add("string", "trim", Fun("string", "df_str_trim", NoParams, needs: TextNeed));
    Add("string", "trim", Fun("string", "df_str_trim_set", new[] { "string" }, needs: TextNeed));
    Add("string", "trimLeft", Fun("string", "df_str_trim_left", NoParams, needs: TextNeed));
    Add("string", "trimLeft", Fun("string", "df_str_trim_left_set", new[] { "string" }, needs: TextNeed));
    Add("string", "trimRight", Fun("string", "df_str_trim_right", NoParams, needs: TextNeed));
    Add("string", "trimRight", Fun("string", "df_str_trim_right_set", new[] { "string" }, needs: TextNeed));
    Add("string", "substr", Fun("string", "df_str_substr", new[] { "u32", "u32" }, needs: TextNeed));
    Add("string", "replace", Fun("string", "df_str_replace", new[] { "string", "string" }, "true", TextNeed));
    Add("string", "replace", Fun("string", "df_str_replace", new[] { "string", "string", "bool" }, needs: TextNeed));
    foreach (var (name, cname) in new[] {
      ("indexOf", "df_str_indexof"), ("lastIndexOf", "df_str_lastindexof")
    }) {
      Add("string", name, Fun("i64", cname, new[] { "string" }, "true", TextNeed));
      Add("string", name, Fun("i64", cname, new[] { "string", "bool" }, needs: TextNeed));
      Add("string", name, Fun("i64", cname + "_c", new[] { "char" }, "true", TextNeed));
      Add("string", name, Fun("i64", cname + "_c", new[] { "char", "bool" }, needs: TextNeed));
    }
    foreach (var (name, cname) in new[] {
      ("contains", "df_str_contains"), ("startsWith", "df_str_startswith"), ("endsWith", "df_str_endswith")
    }) {
      Add("string", name, Fun("bool", cname, new[] { "string" }, "true", TextNeed));
      Add("string", name, Fun("bool", cname, new[] { "string", "bool" }, needs: TextNeed));
    }
    Add("string", "split", Fun("arr<string>", "df_str_split", new[] { "string" }, needs: TextNeed));
    Add("string", "toLower", Fun("string", "df_str_tolower", NoParams, needs: TextNeed));
    Add("string", "toUpper", Fun("string", "df_str_toupper", NoParams, needs: TextNeed));
    Add("string", "padLeft", Fun("string", "df_str_pad_left", new[] { "u32" }, "32u", TextNeed));
    Add("string", "padLeft", Fun("string", "df_str_pad_left", new[] { "u32", "char" }, needs: TextNeed));
    Add("string", "padRight", Fun("string", "df_str_pad_right", new[] { "u32" }, "32u", TextNeed));
    Add("string", "padRight", Fun("string", "df_str_pad_right", new[] { "u32", "char" }, needs: TextNeed));
    Add("string", "insert", Fun("string", "df_str_insert", new[] { "u32", "string" }, needs: TextNeed));
    Add("string", "remove", Fun("string", "df_str_remove", new[] { "u32", "u32" }, needs: TextNeed));
    Add("string", "replaceAt", Fun("string", "df_str_replace_at", new[] { "u32", "u32", "string" }, needs: TextNeed));
    Add("string", "append", Fun("string", "df_str_append_new", new[] { "string" }, needs: TextNeed));
    Add("string", "prepend", Fun("string", "df_str_prepend", new[] { "string" }, needs: TextNeed));
    Add("string", "toCStr", Fun("arr<u8>", "df_str_tocstr", NoParams, "0", TextNeed));
    Add("string", "toCStr", Fun("arr<u8>", "df_str_tocstr", new[] { "StringEncoding" }, needs: TextNeed));

    // chars (spec 4.1)
    foreach (var (name, cname, table2) in new[] {
      ("isLetter", "df_cp_is_letter", "letter"), ("isDigit", "df_cp_is_digit", "digit"),
      ("isLetterOrDigit", "df_cp_is_letterordigit", "letterordigit"), ("isWhiteSpace", "df_cp_is_space", "space"),
      ("isUpper", "df_cp_is_upper", "upper"), ("isLower", "df_cp_is_lower", "lower"),
      ("isHexDigit", "df_cp_is_hexdigit", "hexdigit"), ("isPunctuation", "df_cp_is_punct", "punct"),
      ("isSymbol", "df_cp_is_symbol", "symbol"), ("isControl", "df_cp_is_control", "control"),
      ("isAscii", "df_cp_is_ascii", "ascii")
    }) {
      Add("char", name, Fun("bool", cname, NoParams, needs: new[] { "u:" + table2 }));
    }
    Add("char", "toUpper", Fun("char", "df_cp_toupper", NoParams, needs: new[] { "u:toupper" }));
    Add("char", "toLower", Fun("char", "df_cp_tolower", NoParams, needs: new[] { "u:tolower" }));

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
      ["trim"] = new[] { "c" }, ["trimLeft"] = new[] { "c" }, ["trimRight"] = new[] { "c" },
      ["substr"] = new[] { "start", "len" }, ["replace"] = new[] { "toFind", "replacement", "caseSensitive" },
      ["indexOf"] = new[] { "val", "caseSensitive" }, ["lastIndexOf"] = new[] { "val", "caseSensitive" },
      ["contains"] = new[] { "val", "caseSensitive" }, ["startsWith"] = new[] { "val", "caseSensitive" },
      ["endsWith"] = new[] { "val", "caseSensitive" }, ["split"] = new[] { "separator" },
      ["padLeft"] = new[] { "totalWidth", "paddingChar" }, ["padRight"] = new[] { "totalWidth", "paddingChar" },
      ["toCStr"] = new[] { "encoding" }, ["insert"] = new[] { "pos", "text" }, ["remove"] = new[] { "pos", "len" },
      ["replaceAt"] = new[] { "pos", "len", "text" }, ["append"] = new[] { "text" }, ["prepend"] = new[] { "text" }
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
    if (path == null) {
      return true;
    }
    string[] parts = path.Split('.');
    string first = parts[0];
    // Flags.None.set(Flags.CanRead): an entry of a bitfield is a value
    if (parts.Length == 2 && enums.TryGetValue(first, out var bitfield) && bitfield.Bitfield && scope.Find(first) == null) {
      return true;
    }
    // a local variable, a field of the class being compiled, or a top-level const
    return scope.Find(first) != null || (currentClass != null && FindField(currentClass, first) != null) || globalConsts.ContainsKey(first)
      || (traits.ContainsKey(first) && scope.Find("this") != null);
  }

  Expr? TryReceiverCall(Expr receiver, string name, List<Node> args, Scope scope, Node at) {
    if (IsBitfield(receiver.Type)) {
      return BitfieldMethod(receiver, name, args, scope, at);
    }
    if (IsArr(receiver.Type)) {
      return ArrayMethod(receiver, name, args, scope, at);
    }
    if (IsVec(receiver.Type) || IsList(receiver.Type)) {
      CheckCountedItemsMember(ObjInfo(receiver.Type), name, at);   // spec 12.7
      if (name == "fill" && CountedFill(receiver, args, scope, at) is Expr filled) {
        return filled;
      }
    }
    if (IsVec(receiver.Type) && IsClass(VecElem(receiver.Type)) && name is "fillAtCapacity" or "resize" && args.Count > 0 && args[^1].Kind != "Null") {
      throw Error($"{name} puts the same object in many items: a class instance is not allowed, null is", at);
    }
    if (IsListIterator(receiver.Type) && name.StartsWith("node_") && at.Line >= LibraryLine) {
      return ListIteratorMember(receiver, name, at);
    }
    if (IsList(receiver.Type) && ListMember(receiver, name, args, scope, at) is Expr listCall) {
      return listCall;
    }
    if (IsMap(receiver.Type) && MapMember(receiver, name, args, scope, at) is Expr mapCall) {
      return mapCall;
    }
    if (IsSet(receiver.Type) && name.StartsWith("tree_") && at.Line >= LibraryLine) {
      return SetTreeMember(receiver, name, args, scope, at);   // the C of the methods of the classes set and hset: only their own code calls them
    }
    if (IsVec(receiver.Type) && VecMember(receiver, name, args, scope, at) is Expr vecCall) {
      return vecCall;
    }
    if (IsVec(receiver.Type) && name == "pop") {
      return VecPop(receiver, args, scope, at);
    }
    if (IsVec(receiver.Type) && VecArrayMembers.Contains(name)) {
      return VecArrayMember(receiver, name, args, scope, at);   // on the live items
    }
    if (IsClass(receiver.Type)) {
      return ObjectCall(receiver, name, args, scope, at);
    }
    if (Methods.TryGetValue(receiver.Type, out var byName) && byName.TryGetValue(name, out var overloads)) {
      return StdCall($"{receiver.Type}.{name}", overloads, args, scope, at, StrRead(receiver));
    }
    return null;
  }

  static readonly (string Name, string Declaration)[] IoEnums = {
    ("DeleteOptions", "bitfield8 DeleteOptions { Recursive: 0, ForceDelete: 1 }"),
    ("OverwriteOptions", "enum OverwriteOptions { None, Overwrite }"),
    ("FileSystemObjectsType", "enum FileSystemObjectsType { Files, Dirs, Both }"),
    ("FileWriteMode", "enum FileWriteMode { CreateOnly, ReplaceOnly, CreateReplace, AppendOnly, CreateAppend }"),
    ("StreamReadWriteAccessMode", "enum StreamReadWriteAccessMode { None, Exclusive, Shared }"),
    ("StreamOpenMode", "enum StreamOpenMode { OpenExisting, CreateOrOpen, CreateIfMissing, CreateOrTruncate, TruncateExisting, CreateOrAppend, AppendExisting }"),
    ("StreamSeekOrigin", "enum StreamSeekOrigin { Begin, CurrentPosition, End }")
  };

  // set, clear, setAs, toggle, isSet, isAnySet (spec 3.1.1). The ones that change a value return the new value.
  Expr? BitfieldMethod(Expr receiver, string name, List<Node> args, Scope scope, Node at) {
    int expectedArgs = name switch {
      "set" or "clear" or "toggle" or "isSet" or "isAnySet" => 1,
      "setAs" => 2,
      _ => -1
    };
    if (expectedArgs < 0) {
      return null;
    }
    if (args.Count != expectedArgs) {
      throw Error($"{name} takes {(expectedArgs == 1 ? "the flags" : "the flags and a bool")}", at);
    }
    var flags = ExprOf(args[0], scope, receiver.Type);
    if (flags.Type != receiver.Type) {
      throw Error($"{name} needs flags of {receiver.Type}, not {flags.Type}", at);
    }
    string cType = CType(receiver.Type);
    string on = args.Count == 2 ? BoolOf(ExprOf(args[1], scope), at) : "";
    return new Expr(WithTemp(receiver, value => WithTemp(flags, flagsCode => name switch {
      "set" => $"(({cType})({value} | {flagsCode}))",
      "clear" => $"(({cType})({value} & ~{flagsCode}))",
      "toggle" => $"(({cType})({value} ^ {flagsCode}))",
      "setAs" => $"(({on}) ? ({cType})({value} | {flagsCode}) : ({cType})({value} & ~{flagsCode}))",
      "isSet" => $"(({value} & {flagsCode}) == {flagsCode})",
      _ => $"(({value} & {flagsCode}) != 0)"
    })), name.StartsWith("is") ? "bool" : receiver.Type);
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
    if (!declared.Contains("SortDirection") && Mentions(program, node => node.Text == "SortDirection")) {
      source.Append("enum SortDirection { ASC, DESC }" + Environment.NewLine);
    }
    if (!declared.Contains("SilentException") && Mentions(program, node => node.Text == "SilentException")) {
      source.Append("trait SilentException { }\n");
    }
    // Root.IO.File (spec 17.6.3) is a class written in Dflat (IoPrelude); it needs the string encodings and some Root.IO enums
    var userTypes = program.Children.Where(child => child.Kind is "Class" or "Trait" or "Enum").Select(child => child.Text).ToHashSet();
    // Stream and FileStream (spec 6.8) use File for its exception check and add File.open
    bool usesStreams = !userTypes.Contains("FileStream")
      && Mentions(program, node => node.Text is "FileStream" or "Stream" or "open");
    bool usesFile = !userTypes.Contains("File")
      && (usesStreams || Mentions(program, node => node.Text == "File" || (node.Kind == "Member" && node.Text is string member && (member == "File" || IoPathFunctions.Contains(member)))));
    var fileEnums = new HashSet<string> { "DeleteOptions", "OverwriteOptions", "FileSystemObjectsType", "FileWriteMode" };
    if (usesStreams) {
      fileEnums.UnionWith(new[] { "StreamReadWriteAccessMode", "StreamOpenMode", "StreamSeekOrigin" });
    }
    if (!declared.Contains("StringEncoding")
        && (usesFile || Mentions(program, node => node.Text == "StringEncoding" || node.Text == "String.StringEncoding" || (node.Kind == "Member" && node.Text == "toCStr")))) {
      source.Append("enum StringEncoding { UTF8, UTF16, UTF32, UTF16BE, UTF32BE, ASCII, ISO88591 }" + Environment.NewLine);
    }
    // Root.IO enums (spec 17.6.4): the program writes fs.StreamOpenMode.OpenExisting, Root.IO.StreamOpenMode or the bare name
    foreach (var (name, declaration) in IoEnums) {
      if (!declared.Contains(name)
          && ((usesFile && fileEnums.Contains(name)) || Mentions(program, node => node.Text == name || node.Text?.EndsWith("." + name) == true))) {
        source.AppendLine(declaration);
      }
    }
    if (usesFile) {
      source.AppendLine(IoPrelude
        .Replace("//OPEN-STATIC", usesStreams ? IoOpenStatic : "")
        .Replace("//OPEN-OBJECT", usesStreams ? IoOpenObject : ""));
    }
    if (usesStreams) {
      source.AppendLine(IoStreamPrelude);
    }
    // Root.ExecProcess (stdlib 7): RunResult and the run and exec functions are a class written in Dflat (ExecPrelude)
    if (!userTypes.Contains("ExecProcess") && !userTypes.Contains("RunResult")
        && Mentions(program, node => node.Text is "ExecProcess" or "RunResult" || node.Text?.Contains("ExecProcess.") == true || node.Text?.EndsWith(".RunResult") == true)) {
      source.AppendLine(ExecPrelude);
    }
    // vec<T> (spec 6.8) is a generic class written in Dflat, with the collection traits (CodeGen.Vec.cs)
    if (!userTypes.Contains("vec") && !userTypes.Contains("list") && !userTypes.Contains("map") && Mentions(program, node => node.Text is "vec" or "list" or "map" or "bmap" or "hmap" or "hset" or "View" or "getIterator" or "IndexIterator" or "IndexCollection" or "MapCollection" or "SetCollection" or "Iterator"
          || (node.Kind == "Type" && node.Text is "set" or "bset")
          || (node.Kind == "Index" && node.Children.Count > 1 && node.Children[1].Kind == "Range"))) {
      // its lines are numbered from LibraryLine so that no warning of this code is shown (Warn) and the errors can be told apart
      // UNS_NONE is the largest uns of the target ("not found"); VEC_DOUBLE_LIMIT is the block size up to which a vec doubles (spec 6.8.2)
      string prelude = (VecPrelude + HmapPrelude).Replace("UNS_NONE", NativeUns == "u32" ? "4294967295" : "18446744073709551615")
        .Replace("VEC_DOUBLE_LIMIT", VecDoubleGrowLimit.ToString());
      var vecLibrary = new Parser(new Lexer(prelude, LibraryLine).Tokenize()).ParseProgram();
      program.Children.InsertRange(0, vecLibrary.Children);
      vecPrelude = true;
    }
    if (source.Length == 0) {
      return;
    }
    var library = new Parser(new Lexer(source.ToString()).Tokenize()).ParseProgram();
    program.Children.InsertRange(0, library.Children);
  }
}
