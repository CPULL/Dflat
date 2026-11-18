using System.Text.RegularExpressions;
using dfparser;

namespace dfcompiler;

// A C library of the program (spec 22): its .dflib (found in the project folder by its Name:) and the binaries it names for each target
sealed class CLibrary {
  public required string Name { get; init; }
  public required string Namespace { get; init; }
  public string? Version { get; init; }
  public required string Path { get; init; }
  public required int Line { get; init; }   // the import lib that brought it in
  public string? Kind { get; set; }         // static or dynamic, as written in the import (null: not written)
  public List<(string Target, string Kind, List<string> Files, int Line)> Binaries { get; } = new();
}

// import lib <name> (spec 22): the .dflib is Dflat source with header lines first. The header lines are read here and taken out of the text
// (Namespace: becomes the namespace line of the file, the others become empty lines, so the lines keep their numbers); the rest is parsed in
// .dflib mode: native declarations, named results and -> mappings. Then the natives are hidden or exposed and the mappings are lowered to
// plain functions (LowerDflib)
sealed partial class SourceLoader {
  static readonly Regex HeaderLine = new(@"^\s*(Name|Namespace|Description|Version|Convention)\s*:\s*(.*?)\s*(//.*)?$");
  static readonly Regex LibNameLine = new(@"^\s*LibName\s*,\s*([^,]+?)\s*,\s*(static|dynamic)\s*:\s*(.+?)\s*(//.*)?$");
  static readonly HashSet<string> Conventions = new() { "cdecl", "stdcall", "fastcall", "vectorcall" };

  // the C libraries imported by the program, and their files by namespace (import, qualified names)
  public List<CLibrary> CLibraries { get; } = new();
  public string Target { get; set; } = "";   // the --target name (win-x64): it picks the target blocks of a .dflib (spec 22)
  public Dictionary<string, string> OtherTargetNames { get; } = new();   // a name defined only in blocks of other targets -> those targets
  readonly Dictionary<string, SourceFile> libraryFiles = new();
  string projectFolder = "";

  // import lib zlib: the .dflib becomes one more file of the program; the importing file imports its namespace
  void ImportLibrary(Node import, bool multiFile) {
    string name = import.Text!;
    string? version = import.Children.FirstOrDefault(child => child.Kind == "LibVersion")?.Text;
    string? kind = import.Children.FirstOrDefault(child => child.Kind == "LibKind")?.Text;
    var candidates = new List<(string Path, Dictionary<string, string> Header)>();
    foreach (string path in Directory.GetFiles(projectFolder, "*.dflib").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
      var header = ReadHeader(File.ReadAllLines(path));
      if (header.TryGetValue("Name", out var found) && found == name && (version == null || header.GetValueOrDefault("Version") == version)) {
        candidates.Add((path, header));
      }
    }
    if (candidates.Count == 0) {
      throw new CompileException(version == null
        ? $"no .dflib with Name: {name} in the project folder (spec 22)"
        : $"no .dflib with Name: {name} and Version: {version} in the project folder (spec 22)", import.Line);
    }
    if (candidates.Count > 1) {
      throw new CompileException($"the library {name} has several versions ({string.Join(", ", candidates.Select(c => c.Header.GetValueOrDefault("Version") ?? "no version"))}): "
        + $"write the one to use, import lib {name} <version> (spec 22)", import.Line);
    }
    var (dflibPath, _) = candidates[0];
    var file = ParseDflib(dflibPath, import.Line, out var library);
    library.Kind = kind;
    if (!CLibraries.Any(known => known.Path == library.Path)) {
      CLibraries.Add(library);
    }
    // the namespace of the library is imported in this file (spec 22): the node becomes an import of it
    import.Kind = "Import";
    import.Text = library.Namespace;
    import.Children.Clear();
  }

  static Dictionary<string, string> ReadHeader(IEnumerable<string> lines) {
    var header = new Dictionary<string, string>();
    foreach (string line in lines) {
      var match = HeaderLine.Match(line);
      if (match.Success) {
        header.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
      }
    }
    return header;
  }

  SourceFile ParseDflib(string path, int importLine, out CLibrary library) {
    string full = System.IO.Path.GetFullPath(path);
    var lines = File.ReadAllLines(full);
    var header = new Dictionary<string, string>();
    var binaries = new List<(string Target, string Kind, List<string> Files, int Line)>();
    int index = parsed.TryGetValue(full, out var known) ? known.Index : SourceMap.Add(full);
    int lineBase = SourceMap.Base(index);
    for (int i = 0; i < lines.Length; i++) {
      var match = HeaderLine.Match(lines[i]);
      if (match.Success) {
        string key = match.Groups[1].Value;
        if (!header.TryAdd(key, match.Groups[2].Value)) {
          throw new CompileException($"{key}: is written twice in the .dflib", lineBase + i + 1);
        }
        lines[i] = key == "Namespace" ? "namespace " + match.Groups[2].Value : "";
        continue;
      }
      var lib = LibNameLine.Match(lines[i]);
      if (lib.Success) {
        var files = lib.Groups[3].Value.Split(',').Select(f => f.Trim()).Where(f => f != "").ToList();
        binaries.Add((lib.Groups[1].Value, lib.Groups[2].Value, files, lineBase + i + 1));
        lines[i] = "";
      }
    }
    if (!header.TryGetValue("Name", out var name) || name == "" || name.Any(char.IsWhiteSpace)) {
      throw new CompileException("a .dflib starts with Name: <name> (no spaces) (spec 22)", lineBase + 1);
    }
    if (!header.TryGetValue("Namespace", out var ns) || ns == "") {
      throw new CompileException("a .dflib gives the namespace of its members: Namespace: <namespace> (spec 22)", lineBase + 1);
    }
    string convention = header.GetValueOrDefault("Convention") ?? "cdecl";
    if (!Conventions.Contains(convention)) {
      throw new CompileException($"unknown calling convention '{convention}': cdecl, stdcall, fastcall or vectorcall (spec 22)", lineBase + 1);
    }
    library = new CLibrary { Name = name, Namespace = ns.ToLowerInvariant(), Version = header.GetValueOrDefault("Version"), Path = full, Line = importLine };
    library.Binaries.AddRange(binaries);
    if (known != null) {
      return known;
    }
    var preprocessor = new Preprocessor(variables, projectVariables);
    string text;
    try {
      text = preprocessor.Process(string.Join("\n", lines));
    } catch (DfException ex) {
      throw new DfException(ex.Message, lineBase + ex.Line, ex.Col);
    }
    var lexer = new Lexer(text, lineBase + 1);
    var parser = new Parser(lexer.Tokenize()) { DflibMode = true };
    var tree = parser.ParseProgram();
    Warnings.AddRange(lexer.Warnings.Concat(parser.Warnings).Select(SourceMap.Fix));
    ResolveTargetBlocks(tree, Target, OtherTargetNames);
    LowerVariables(tree);
    LowerDflib(tree, convention);
    var file = new SourceFile(full, tree, index) { Namespace = library.Namespace };
    parsed[full] = file;
    libraryFiles[library.Namespace] = file;
    return file;
  }

  // the natives: a native with a fun of the same name, or with pointers in its types, is seen only by the mappings (its name becomes
  // __n_<name>, the C symbol stays); the others are mapped 1 to 1, public under their own name. The named results of a mapping become
  // its locals and the values it returns
  static void LowerDflib(Node tree, string convention) {
    var natives = tree.Children.Where(statement => statement.Kind == "Fun" && statement.Children.Any(child => child.Kind == "Native")).ToList();
    var funNames = tree.Children.Where(statement => statement.Kind == "Fun" && !natives.Contains(statement)).Select(statement => statement.Text).ToHashSet();
    var hidden = new HashSet<string>();
    // a callback type, alias [stdcall] fun ... as Name: the library convention when none is written (spec 22)
    foreach (var callbackType in tree.Children.Where(statement => statement.Kind == "Alias")) {
      if (callbackType.Children.FirstOrDefault(child => child.Kind == "Native") is Node conv) {
        conv.Text ??= convention;
        if (!Conventions.Contains(conv.Text)) {
          throw new CompileException($"unknown calling convention '{conv.Text}': cdecl, stdcall, fastcall or vectorcall (spec 22)", callbackType.Line);
        }
      }
    }
    foreach (var native in natives) {
      string cName = native.Text!;
      var marker = native.Children.First(child => child.Kind == "Native");
      marker.Text ??= convention;
      if (!Conventions.Contains(marker.Text)) {
        throw new CompileException($"unknown calling convention '{marker.Text}': cdecl, stdcall, fastcall or vectorcall (spec 22)", native.Line);
      }
      marker.Add(new Node("CSymbol", TokenAt(native), cName));
      if (funNames.Contains(cName) || HasPointers(native)) {
        hidden.Add(cName);
        native.Text = "__n_" + cName;
      } else {
        native.Children.First(child => child.Kind == "Mods").Text = "public extern";   // a 1 to 1 mapping: the program calls it by its name
      }
    }
    foreach (var fun in tree.Children.Where(statement => statement.Kind == "Fun" && statement.Children.Any(child => child.Kind == "Mapping"))) {
      var body = fun.Children.First(child => child.Kind == "Block");
      RenameNatives(body, hidden);
      var outTargets = fun.Children.FirstOrDefault(child => child.Kind == "ResultNames")?.Text?.Split(',') ?? Array.Empty<string>();
      // a variadic parameter given on to a native: the mapping gets a copy for each shape of its calls (spec 22)
      if (fun.Children.FirstOrDefault(child => child.Kind == "Params")?.Children.LastOrDefault() is { Kind: "VariadicParam" } variadic
          && Descendants(body).Any(node => node.Kind == "Args" && node.Children.Any(arg => arg.Kind == "Name" && arg.Text == variadic.Text))) {
        fun.Add(new Node("CVariadicForward", TokenAt(fun), variadic.Text));
      }
      foreach (var outArg in Descendants(body).Where(node => node.Kind == "OutArg")) {
        if (!outTargets.Contains(outArg.Text)) {
          throw new CompileException($"={outArg.Text} fills a named result of the mapping: {outArg.Text} is not one (spec 22)", outArg.Line);
        }
      }
      if (fun.Children.FirstOrDefault(child => child.Kind == "ResultNames") is Node names) {
        var types = fun.Children.First(child => child.Kind == "Returns").Children;
        var resultNames = names.Text!.Split(',');
        for (int i = resultNames.Length - 1; i >= 0; i--) {
          body.Children.Insert(0, new Node("Decl", TokenAt(names), resultNames[i]).Add(CloneTree(types[i])));
        }
        var giveBack = new Node("Return", TokenAt(names));
        foreach (string result in resultNames) {
          giveBack.Add(new Node("Gift", TokenAt(names), "named-result").Add(new Node("Name", TokenAt(names), result)));   // return ->v: the result leaves
        }
        body.Add(giveBack);
        fun.Children.Remove(names);
      }
      fun.Children.RemoveAll(child => child.Kind == "Mapping");
    }
    // only the code of a mapping calls a hidden native (spec 22): elsewhere its name is not known
  }

  // the target blocks (spec 22): a block that matches the target replaces the default definitions of the same names, the most specific
  // block first (a pattern without *), then the first one in the file; the blocks of other targets are dropped
  static void ResolveTargetBlocks(Node tree, string target, Dictionary<string, string> otherTargets) {
    static bool Matches(string pattern, string target) =>
      Regex.IsMatch(target, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$");
    var blocks = tree.Children.Where(statement => statement.Kind == "TargetBlock").ToList();
    if (blocks.Count == 0) {
      return;
    }
    var chosen = blocks.Select((block, order) => (Block: block, Order: order,
        Specific: block.Text!.Split(',').Where(pattern => Matches(pattern, target)).Select(pattern => pattern.Contains('*') ? 1 : 2).DefaultIfEmpty(0).Max()))
      .Where(entry => entry.Specific > 0).OrderByDescending(entry => entry.Specific).ThenBy(entry => entry.Order).ToList();
    var taken = new HashSet<string>();
    var replacing = new Dictionary<string, Node>();   // key -> the definition from a block
    foreach (var (block, _, _) in chosen) {
      foreach (var item in block.Children[0].Children) {
        if (DefinitionKey(item) is string key && taken.Add(key)) {
          replacing[key] = item;
        }
      }
    }
    var result = new List<Node>();
    foreach (var statement in tree.Children) {
      if (statement.Kind == "TargetBlock") {
        continue;
      }
      if (DefinitionKey(statement) is string key && replacing.ContainsKey(key)) {
        continue;   // the block of the target replaces it
      }
      result.Add(statement);
    }
    result.AddRange(replacing.Values);
    foreach (var (block, _, _) in chosen) {
      result.AddRange(block.Children[0].Children.Where(item => DefinitionKey(item) == null));
    }
    // a function or variable with no definition for this target is an error only where it is used (spec 22)
    var defined = result.Select(DefinitionKey).Where(key => key != null).ToHashSet();
    foreach (var block in blocks.Where(block => !chosen.Any(entry => entry.Block == block))) {
      foreach (var item in block.Children[0].Children.Where(item => item.Text != null && DefinitionKey(item) is string key && !defined.Contains(key))) {
        otherTargets[item.Text!] = otherTargets.TryGetValue(item.Text!, out var known) ? known + ", " + block.Text : block.Text!;
      }
    }
    tree.Children.Clear();
    tree.Children.AddRange(result);
  }

  // what a definition replaces: a native by its C name, a function or a variable by its name
  static string? DefinitionKey(Node item) => item.Kind switch {
    "Fun" when item.Children.Any(child => child.Kind == "Native") => "native " + item.Text,
    "Fun" or "VarMapping" => "name " + item.Text,
    "Handle" or "Struct" or "Enum" or "Class" or "Alias" => "type " + item.Text,
    "Decl" => "name " + item.Text,
    _ => null
  };

  // a C variable (spec 22): T x -> native T x reads (and with &T, writes) the C variable itself, public as a 1 to 1 native; T x -> code
  // reads it through a function, a mapping whose named result is x
  static void LowerVariables(Node tree) {
    for (int index = 0; index < tree.Children.Count; index++) {
      var mapping = tree.Children[index];
      if (mapping.Kind != "VarMapping") {
        continue;
      }
      var at = TokenAt(mapping);
      var type = mapping.Children.First(child => child.Kind is "Type" or "Nullable" or "Pointer" or "WritablePointer");
      var fun = new Node("Fun", at, mapping.Text);
      var mods = mapping.Children.FirstOrDefault(child => child.Kind == "Mods");
      if (mapping.Children.FirstOrDefault(child => child.Kind == "NativeVar") is Node native) {
        if (TypeText(native.Children[0]) != TypeText(type)) {
          throw new CompileException($"the C variable {native.Text} is mapped 1 to 1: write the same type on both sides of -> (spec 22)", mapping.Line);
        }
        fun.Add(new Node("Mods", at, "public"));
        fun.Add(new Node("Returns", at).Add(CloneTree(type)));
        fun.Add(new Node("Params", at));
        fun.Add(new Node("NoBody", at));
        var marker = new Node("NativeVar", at, native.Text);
        if (mapping.Children.Any(child => child.Kind == "ReadWrite")) {
          marker.Add(new Node("ReadWrite", at));
        }
        fun.Add(marker);
      } else {
        if (mods != null) {
          fun.Add(mods);
        }
        fun.Add(new Node("Returns", at).Add(CloneTree(type)));
        fun.Add(new Node("ResultNames", at, mapping.Text));
        fun.Add(new Node("Params", at));
        fun.Add(new Node("Mapping", at));
        fun.Add(mapping.Children.First(child => child.Kind == "Block"));
        fun.Add(new Node("VarGetter", at));
      }
      tree.Children[index] = fun;
    }
  }

  static string TypeText(Node type) => type.Kind + ":" + type.Text + "<" + string.Join(",", type.Children.Select(TypeText)) + ">";

  static bool HasPointers(Node native) =>
    native.Children.Where(child => child.Kind is "Returns" or "Params").SelectMany(Descendants).Any(node => node.Kind is "Pointer" or "WritablePointer");

  static IEnumerable<Node> Descendants(Node node) => node.Children.Concat(node.Children.SelectMany(Descendants));

  static void RenameNatives(Node node, HashSet<string> hidden) {
    if (node.Kind == "Call" && node.Children.Count > 0 && node.Children[0] is { Kind: "Name" } callee && hidden.Contains(callee.Text!)) {
      callee.Text = "__n_" + callee.Text;
    }
    foreach (var child in node.Children) {
      RenameNatives(child, hidden);
    }
  }

  static Token TokenAt(Node node) => new Token(TokenKind.Ident, "", node.Line, 0);

  static Node CloneTree(Node node) {
    var copy = new Node(node.Kind, TokenAt(node), node.Text);
    foreach (var child in node.Children) {
      copy.Add(CloneTree(child));
    }
    return copy;
  }
}
