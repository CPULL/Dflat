using System.Text;
using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// The interface of a compiled Dflat library (.dfi, spec 15.8): what a program needs to use the binary of the library, for the compiler only.
// It starts with the DFI block (the compiler that built it, the library, its namespace, the runtime chunks and the stdlib parts it needs),
// then the public declarations in Dflat, each with the C symbol it maps to (-> as in a .dflib, spec 22) and "error" when the function
// returns an error part, then "taskunsafe" when it uses (at any depth) top-level variables or static fields that the code of a task cannot
// see (spec 20.12). A class gives its record, then its C layout for the target it was built for (layout-<target>: { ... }), then its
// public members. Example:
//
//   DFI {
//    compiler: 0.0.173alpha
//    library: counters
//    namespace: libs.fun.counters
//    runtime: text,io
//    stdlib: File
//   }
//   namespace libs.fun.counters
//   class Sequence -> df_cls_libs__fun__counters__Sequence {
//     layout-win-x64: { const df_class* cls; int32_t m_next; }
//     public fun i32 take() -> f_Sequence_take
//   }
//   public global Sequence Numbers -> gl_Numbers lock 0
//   public fun i32 nextNumber() -> f_nextNumber error
public sealed partial class CodeGen {
  // the library whose interface this compilation writes (null: none); the text it wrote (point 5 of step 2 makes this the library build)
  public static string? InterfaceLibrary { get; set; }
  public static string? InterfaceNamespace { get; set; }
  public static string TargetName { get; set; } = "win-x64";
  public static string? InterfaceText { get; private set; }

  // a library build (spec 15.8, 19): the name of the library; its C has no main, its public symbols are exported with its name
  public static string? LibraryBuild { get; set; }

  // the C symbol a program uses for a public function of the library being built: an exported wrapper of the function
  string ExportName(FunSig f) => LibraryBuild == null ? f.CName : $"f_{LibraryBuild}_{f.CName["f_".Length..]}";

  // the public items of a library: the functions outside the classes, the classes (public, or named by a public declaration) with
  // their public methods, the public globals and the public variables
  (List<FunSig> TopFuns, List<ClassInfo> Classes, List<FunSig> Methods, List<GlobalInfo> Globals, List<(string Name, string Type, string Library)> Vars)
      InterfaceItems(string library) {
    bool Mine(int line) => SourceMap.LibraryOf(line) == library;
    var topFuns = funs.Values.SelectMany(list => list)
      .Where(f => f.Class == null && f.Struct == null && Mine(f.Node.Line) && ModsOf(f.Node).Any(mod => mod is "public" or "unrestricted") && f.LambdaCtx == null && !f.IsStaticInit
        && f.Name is not ("__libstart" or "__libend"))
      .ToList();
    var publicGlobals = globalOrder.Where(global => global.Library == library && global.IsPublic).ToList();
    var publicVars = libraryVarOrder.Where(variable => variable.Library == library && ModsOf(VarDecl(variable.Name)).Contains("public")).ToList();
    var named = new HashSet<string>();
    void Names(string type) {
      foreach (Match match in Regex.Matches(type, @"[A-Za-z_][A-Za-z0-9_]*")) {
        named.Add(match.Value);
      }
    }
    foreach (var f in topFuns) {
      Names(f.Ret);
      f.Params.ForEach(p => Names(p.Type));
    }
    publicGlobals.ForEach(global => Names(global.Type));
    publicVars.ForEach(variable => Names(variable.Type));
    var classes = classOrder.Where(cls => !cls.IsTrait && Mine(cls.Node.Line) && !IsStdlibClass(cls)
      && (ModsOf(cls.Node).Contains("public") || named.Contains(cls.Name))).ToList();
    var methods = funs.Values.SelectMany(list => list)
      .Where(f => f.Class != null && classes.Contains(f.Class) && f.Visibility == Vis.Public && !f.IsInit && !f.IsDtor && !f.IsStaticInit && f.LambdaCtx == null && !f.IsAbstract)
      .ToList();
    return (topFuns, classes, methods, publicGlobals, publicVars);
  }

  // a library build: the exported symbols (the records, globals and variables of the interface) and the exported wrappers of its
  // public functions, which call the functions of the library (their own names stay inside the library)
  readonly HashSet<string> exportedSymbols = new();

  string LibraryExports() {
    var (topFuns, classes, methods, publicGlobals, publicVars) = InterfaceItems(LibraryBuild!);
    classes.ForEach(cls => exportedSymbols.Add(cls.Descriptor));
    classes.Where(cls => cls.IsException).ToList().ForEach(cls => exportedSymbols.Add(cls.ExcDescriptor));   // a program catches them (spec 15.8)
    publicGlobals.ForEach(global => exportedSymbols.Add(global.CName));
    publicVars.ForEach(variable => exportedSymbols.Add(globalConsts[variable.Name].CName));
    var sb = new StringBuilder("/* the public functions of the library, exported with its name (spec 15.8) */\n");
    foreach (var f in topFuns.Concat(methods)) {
      string head = Signature(f)["static ".Length..].Replace($" {f.CName}(", $" {ExportName(f)}(");
      var args = f.Params.Select(ParamName).ToList();
      if (f.Class != null && !f.IsStatic && !f.IsCtor) {
        args.Insert(0, "self");
      }
      string call = $"{f.CName}({string.Join(", ", args)})";
      if (f.Class != null && !f.IsStatic && !f.IsCtor && f.Slot >= 0 && f.Root.GroupSize > 0) {
        // a method that a subclass replaces: through the record of the object, as a call inside the library (spec 15.8)
        var types = new List<string> { CType(f.Class.Name) }.Concat(f.Params.Select(ParamCType));
        call = $"(({RetCType(f)}(*)({string.Join(", ", types)}))(self->cls->vtable[{f.Slot}]))({string.Join(", ", args)})";
      }
      sb.Append($"{head} {{ {(RetCType(f) == "void" ? call + ";" : "return " + call + ";")} }}\n");
    }
    return sb.ToString();
  }

  // a library build: the program gives the ids of its classes and exceptions (spec 15.8); this function writes them in the records and the
  // descriptors, from next on, and returns the first id it did not use. The program calls it before any library starts
  string LibraryIdsFunction() {
    var sb = new StringBuilder($"uint64_t df_libids_{LibraryBuild}(uint64_t next) {{\n");
    foreach (var cls in classOrder.Where(cls => !IsStdlibClass(cls) && !cls.IsImported)) {
      sb.Append($"  {cls.Descriptor}.id = next++;\n");
      if (cls.IsException) {
        sb.Append($"  {cls.ExcDescriptor}.id = next++;\n");
      }
    }
    sb.Append("  return next;\n}\n");
    return sb.ToString();
  }

  // the statics this unit starts and ends: in a library build, not the ones of the stdlib (the program owns them)
  IEnumerable<FieldInfo> OwnStatics => staticOrder.Where(item => LibraryBuild == null || !IsStdlibClass(item.Owner));

  // a name of the library (a global, a variable): with the name of the library, so it never meets a name of the program
  static string LibraryPrefixed(string prefix, string name, int line) =>
    SourceMap.LibraryOf(line) is string library ? $"{prefix}{library}_{name}" : $"{prefix}{name}";

  string ExportLinkage(string code) =>
    LibraryBuild == null ? code : RuntimeLinkage.Apply(code, RuntimeLinkageMode.Exported, exportedSymbols.Contains);

  string BuildInterface(string library, string ns, string cCode) {
    bool Mine(int line) => SourceMap.LibraryOf(line) == library;
    var sb = new StringBuilder();
    var runtime = needs.OrderBy(need => need, StringComparer.Ordinal).Concat(unicodeNeeds.OrderBy(need => need, StringComparer.Ordinal).Select(table => "unicode-" + table));
    var stdlib = classOrder.Where(IsStdlibClass).Select(cls => DisplayName(cls.Name)).Concat(stdlibEnums).Distinct().OrderBy(name => name, StringComparer.Ordinal);
    sb.Append("DFI {\n");
    sb.Append($" compiler: {Program.Version}\n");
    sb.Append($" library: {library}\n");
    sb.Append($" namespace: {ns}\n");
    sb.Append($" runtime: {string.Join(",", runtime)}\n");
    sb.Append($" stdlib: {string.Join(",", stdlib)}\n");
    sb.Append("}\n");
    // each declaration goes under its own namespace (the root namespace of the library, or a sub-namespace of its folders)
    var entries = new List<(string Ns, string Text)>();
    string NsOf(int line) => SourceMap.NamespaceOf(line) ?? ns;

    var (topFuns, classes, allMethods, publicGlobals, publicVars) = InterfaceItems(library);
    // the public enums and bitfields, with the value of each entry (the bit position of a flag)
    foreach (var (enumName, node) in enumNodes.Where(pair => Mine(pair.Value.Line) && ModsOf(pair.Value).Contains("public"))) {
      var info = enums[enumName];
      var written = info.Bitfield
        ? info.Entries.Where(entry => entry.Name is not ("NONE" or "ALL")).Select(entry => $"{entry.Name}: {BitPosition(entry.Value)}")
        : info.Entries.Select(entry => $"{entry.Name}: {entry.Value}");
      string kind = info.Bitfield ? "bitfield" + Bits(info.ValueType) : "enum";
      entries.Add((NsOf(node.Line), $"public {kind} {SourceMap.PrintName(enumName)} {{ {string.Join(", ", written)} }}\n"));
    }
    foreach (var cls in classes) {
      var cb = new StringBuilder();
      string parents = string.Join(", ", new[] { cls.Parent?.Name ?? (cls.IsException ? "Exception" : null) }.Concat(cls.Traits.Select(trait => trait.Name)).Where(name => name != null).Select(name => DisplayName(name!)));
      cb.Append($"class {DisplayName(cls.Name)}{(parents == "" ? "" : " : " + parents)} -> {cls.Descriptor} {{\n");
      cb.Append($"  layout-{TargetName}: {{ {Layout(cls, cCode)} }}\n");
      foreach (var field in cls.Fields.Where(field => field.Visibility == Vis.Public && !field.Hidden)) {
        cb.Append($"  public {(field.IsStatic ? "static " : "")}{(field.IsConst ? "const " : "")}{DisplayName(field.Type)} {field.Name}\n");
      }
      foreach (var method in allMethods.Where(f => f.Class == cls)) {
        cb.Append("  ").Append(FunLine(method)).Append('\n');
      }
      cb.Append("}\n");
      entries.Add((NsOf(cls.Node.Line), cb.ToString()));
    }
    foreach (var global in publicGlobals) {
      entries.Add((NsOf(global.Decl.Line), $"public global {DisplayName(global.Type)} {SourceMap.PrintName(global.Name)} -> {global.CName} lock {global.Rank}\n"));
    }
    foreach (var (name, type, _) in publicVars) {
      entries.Add((NsOf(VarDecl(name).Line), $"public {DisplayName(type)} {SourceMap.PrintName(name)} -> {globalConsts[name].CName}\n"));
    }
    foreach (var f in topFuns) {
      entries.Add((NsOf(f.Node.Line), FunLine(f) + "\n"));
    }
    if (LibraryBuild != null) {
      // the start and the end of a built library: its statics, variables and globals with its atLibStart, then its atEnd and their end
      entries.Add((ns, $"ids -> df_libids_{library}\n"));   // the ids of its classes and exceptions: given by the program at start
      entries.Add((ns, $"atLibStart -> df_libstart_{library}\n"));
      entries.Add((ns, $"atEnd -> df_libend_{library}\n"));
    } else {
      if (libraryStarts.TryGetValue(library, out var start) && funs.TryGetValue(start, out var starts)) {
        entries.Add((ns, $"atLibStart -> {starts[0].CName}{(PairReturn(starts[0]) ? " error" : "")}\n"));
      }
      if (libraryEnds.TryGetValue(library, out var end) && funs.TryGetValue(end, out var ends)) {
        entries.Add((ns, $"atEnd -> {ends[0].CName}{(PairReturn(ends[0]) ? " error" : "")}\n"));
      }
    }
    // the root namespace first, then the others in the order they come
    foreach (string group in new[] { ns }.Concat(entries.Select(entry => entry.Ns)).Distinct()) {
      sb.Append($"namespace {group}\n");
      foreach (var entry in entries.Where(entry => entry.Ns == group)) {
        sb.Append(entry.Text);
      }
    }
    return sb.ToString();
  }

  // the bit position of a flag of a bitfield (its value is 1 << position)
  static int BitPosition(System.Numerics.BigInteger value) {
    int position = 0;
    while (value > 1) {
      value >>= 1;
      position++;
    }
    return position;
  }

  // the declaration node of a library variable (its modifiers say whether it is public)
  Node VarDecl(string name) => libraryVarDecls[name];

  // the fields of the C struct of a class as this compilation made it: "const df_class* cls; int32_t m_next;"
  static string Layout(ClassInfo cls, string cCode) {
    var match = Regex.Match(cCode, $@"\nstruct {Regex.Escape(cls.StructName)} {{\n(.*?)\n}};", RegexOptions.Singleline);
    if (!match.Success) {
      throw new InvalidOperationException($"no C struct for {cls.Name}");
    }
    return string.Join(" ", match.Groups[1].Value.Split('\n').Select(line => Regex.Replace(line.Trim(), @"\s*/\*.*?\*/", "")).Where(line => line != ""));
  }

  // public [static] [unrestricted] fun <results> <name>(<params>) -> <symbol> [error]
  string FunLine(FunSig f) {
    var sb = new StringBuilder();
    var mods = ModsOf(f.Node);
    sb.Append(mods.Contains("unrestricted") ? "unrestricted " : "public ");
    if (f.IsStatic) {
      sb.Append("static ");
    }
    string name = SourceMap.PrintName(f.Name[(f.Name.LastIndexOf('.') + 1)..]);
    if (f.IsCtor) {
      sb.Append(DisplayName(f.Class!.Name));
    } else {
      sb.Append("fun ");
      if (IsMulti(f.Ret)) {
        sb.Append(string.Join(", ", TupleElems(f.Ret).Select((elem, index) => Marker(f, index) + DisplayName(elem.Type) + (elem.Name == null ? "" : " " + elem.Name)))).Append(' ');
      } else if (f.Ret != "void") {
        sb.Append(Marker(f, 0)).Append(DisplayName(f.Ret)).Append(' ');
      }
      sb.Append(name);
    }
    sb.Append('(').Append(string.Join(", ", f.Params.Select(ParamText))).Append(')');
    sb.Append(" -> ").Append(ExportName(f));
    if (PairReturn(f)) {
      sb.Append(" error");
    }
    if (TasksMode && !f.IsCtor && (taskUnsafeCache ??= TaskUnsafeFuns()).Contains(f)) {
      sb.Append(" taskunsafe");   // it uses what the code of a task cannot see (spec 20.12)
    }
    return sb.ToString();
  }

  // ->T for a result the caller owns, &T for one lent writable (spec 13.1.1)
  static string Marker(FunSig f, int index) => f.Gives.Contains(index) ? "->" : f.Writes.Contains(index) ? "&" : "";

  string ParamText(ParamSig p) {
    string marker = p.IsGift || p.IsHandleGift ? "->" : p.Ref == 2 ? "&" : "";
    string text = p.IsVariadic ? $"{DisplayName(ArrElem(p.Type))} {p.Name}..." : $"{marker}{DisplayName(p.Type)} {p.Name}";
    if (p.Default != null) {
      text += " = " + (ConstantText(p.Default) ?? throw NotSupported("a default value that is not a constant, in the interface of a compiled library", p.Default));
    }
    return text;
  }

  // a default value written in the interface: literals and enum entries
  static string? ConstantText(Node n) => n.Kind switch {
    "Int" or "Real" or "String" or "Char" or "Bool" => n.Text,
    "Null" => "null",
    "Neg" when ConstantText(n.Children[0]) is string inner => "-" + inner,
    "Name" => n.Text,
    "Member" when n.Children.Count == 1 && ConstantText(n.Children[0]) is string owner => owner + "." + n.Text,
    _ => null
  };
}
