using dfparser;

namespace dfcompiler;

// A compiled Dflat library used by a program (spec 15.8): the include= folder holds <name>.dfi, its interface, and the binary of each
// target in <folder>/<target>/ (<name>.lib on Windows, lib<name>.a elsewhere). The DFI block says which compiler built it (another
// version is a compile error), its name, its root namespace, the runtime chunks and the stdlib parts the program must hold for it.
// The declarations follow, each under its namespace line; each namespace becomes one file of the program, reached by import.
sealed class CompiledLibrary {
  public required string Name { get; init; }
  public required string Namespace { get; init; }
  public required string Folder { get; init; }
  public required string DfiPath { get; init; }
  public required string Binary { get; init; }
  public List<string> Runtime { get; } = new();   // the runtime chunks (text, io, unicode-<table> ...)
  public List<string> Stdlib { get; } = new();    // the stdlib parts written in Dflat (File, StringEncoding ...)
  public (string Symbol, bool Error)? Start { get; set; }   // atLibStart -> df_libstart_<name>: called at program start
  public (string Symbol, bool Error)? End { get; set; }     // atEnd -> df_libend_<name>: called at program end
  public string? Ids { get; set; }                          // ids -> df_libids_<name>: takes the ids the program gives
}

sealed partial class SourceLoader {
  // the compiled Dflat libraries the program links (spec 15.8)
  public List<CompiledLibrary> CompiledLibraries { get; } = new();

  // a file of the program imports Root.Tasks (spec 20.0)
  public bool TasksImported { get; private set; }

  // the multitasking runtime is on (spec 20.0, 15.8): the program imports Root.Tasks, or a library built with it pulls it in (its
  // interface lists the runtime chunk "tasks"). Every library of such a program must be built with it: the runtime code compiled into a
  // library binary without it is not thread safe (and its state is not per thread)
  public bool TasksMode => TasksImported || CompiledLibraries.Any(library => library.Runtime.Contains("tasks"));

  // the libraries built without the multitasking runtime, in a program that has it: each one is a compile error
  public IEnumerable<CompiledLibrary> LibrariesWithoutTasks => TasksMode ? CompiledLibraries.Where(library => !library.Runtime.Contains("tasks")) : Enumerable.Empty<CompiledLibrary>();

  // the files made from the namespaces of a .dfi, by namespace (lower case)
  readonly Dictionary<string, SourceFile> compiledFiles = new();

  // where the binaries taken out of a .dfc go: the .compilation folder of the program
  string compilationFolder = "";

  void AddCompiledLibrary(string folder, string dfiPath, string projectFile) {
    string full = System.IO.Path.GetFullPath(dfiPath);
    bool packed = full.EndsWith(".dfc", StringComparison.OrdinalIgnoreCase);
    string[] lines;
    List<LibraryFiles.Binary> packedBinaries = new();
    if (packed) {
      // a .dfc: the interface and the binary of each target in one file (spec 15.8)
      string text;
      try {
        (text, packedBinaries) = LibraryFiles.ReadDfc(full);
      } catch (InvalidDataException ex) {
        throw new SourceException($"{projectFile}: error: {ex.Message} (spec 15.8)");
      }
      lines = text.Replace("\r\n", "\n").Split('\n');
    } else {
      lines = File.ReadAllLines(full);
    }
    int index = SourceMap.Add(full);
    int lineBase = SourceMap.Base(index);
    CompileException Bad(string message, int line) => new($"{message} (spec 15.8)", lineBase + line);

    // the DFI block: its first line, then key: value lines up to the closing brace
    if (lines.Length == 0 || lines[0].Trim() != "DFI {") {
      throw Bad("not a library interface: a .dfi starts with the DFI block", 1);
    }
    var header = new Dictionary<string, string>();
    int at = 1;
    for (; at < lines.Length && lines[at].Trim() != "}"; at++) {
      int colon = lines[at].IndexOf(':');
      if (colon < 0) {
        throw Bad("a line of the DFI block is key: value", at + 1);
      }
      header[lines[at][..colon].Trim()] = lines[at][(colon + 1)..].Trim();
    }
    if (at == lines.Length) {
      throw Bad("the DFI block of the interface is not closed", 1);
    }
    foreach (string key in new[] { "compiler", "library", "namespace" }) {
      if (!header.ContainsKey(key) || header[key] == "") {
        throw Bad($"the DFI block has no {key}:", 1);
      }
    }
    string name = header["library"];
    string ns = header["namespace"].ToLowerInvariant();
    string binaryFolder = System.IO.Path.Combine(folder, Target);
    string binary = System.IO.Path.Combine(binaryFolder, Target.StartsWith("win-") ? name + ".lib" : "lib" + name + ".a");
    if (packed) {
      // the binary of the target, taken out into the .compilation folder of the program and linked from there
      var inside = packedBinaries.FirstOrDefault(entry => entry.Target == Target)
        ?? throw new SourceException($"{projectFile}: error: the library {name} has no binary for {Target} in {System.IO.Path.GetFileName(full)}: it holds {(packedBinaries.Count == 0 ? "none" : string.Join(", ", packedBinaries.Select(entry => entry.Target)))} (build the library for this target, spec 15.8)");
      binary = System.IO.Path.Combine(compilationFolder, "libs", name, Target, inside.FileName);
      Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binary)!);
      if (!File.Exists(binary) || !File.ReadAllBytes(binary).AsSpan().SequenceEqual(inside.Data)) {
        File.WriteAllBytes(binary, inside.Data);
      }
    } else if (!File.Exists(binary)) {
      throw new SourceException($"{projectFile}: error: the library {name} has no binary for {Target}: {binary} (build the library for this target, spec 15.8)");
    }
    if (header["compiler"] != Program.Version) {
      throw Bad($"the library {header["library"]} was built by the compiler {header["compiler"]}: this is {Program.Version}, build the library again", 2);
    }
    var library = new CompiledLibrary { Name = name, Namespace = ns, Folder = System.IO.Path.GetFullPath(folder), DfiPath = full, Binary = binary };
    library.Runtime.AddRange(List(header.GetValueOrDefault("runtime")));
    library.Stdlib.AddRange(List(header.GetValueOrDefault("stdlib")));
    CompiledLibraries.Add(library);
    roots.Add(new SourceRoot(library.Folder, ns, "the library " + folder) { LibName = name, Compiled = true });

    // the sections: each namespace line starts one, up to the next; a class block runs from its class line to a line with only }
    var sections = new List<(string Ns, int First, int End)>();
    for (int line = at + 1; line < lines.Length; line++) {
      string text = lines[line].Trim();
      if (text.StartsWith("namespace ")) {
        if (sections.Count > 0) {
          sections[^1] = (sections[^1].Ns, sections[^1].First, line);
        }
        string part = text["namespace ".Length..].Trim().ToLowerInvariant();
        if (part != ns && !part.StartsWith(ns + ".")) {
          throw Bad($"the namespace {part} is not inside the namespace of the library, {ns}", line + 1);
        }
        sections.Add((part, line, lines.Length));
      }
    }
    bool firstNamespace = true;
    foreach (var (part, first, end) in sections) {
      // each namespace has its own index in the source map (its lines keep the numbers of the .dfi): its declarations know their namespace
      int nsIndex = firstNamespace ? index : SourceMap.Add(full);
      firstNamespace = false;
      int nsBase = SourceMap.Base(nsIndex);
      var kept = new string[lines.Length];
      Array.Fill(kept, "");
      kept[first] = lines[first];
      var symbols = new Dictionary<int, (string Symbol, bool Error)>();   // by the line of the declaration
      var taskUnsafe = new HashSet<int>();   // the functions that use what the code of a task cannot see (taskunsafe, spec 20.12)
      var records = new Dictionary<int, (string Record, string? Layout)>();   // the classes: by the line of their header
      var ctorLines = new HashSet<int>();                                       // the constructors, written as static functions
      string? className = null;
      int classLine = 0;
      for (int line = first + 1; line < end; line++) {
        string text = lines[line].Trim();
        if (className != null) {
          if (text == "}") {
            className = null;
            kept[line] = "}";
            continue;
          }
          if (text.StartsWith("layout-")) {
            int colon = text.IndexOf(':');
            if (colon > 0 && text["layout-".Length..colon] == Target) {
              string layout = text[(colon + 1)..].Trim();
              records[classLine] = (records[classLine].Record, layout.StartsWith('{') && layout.EndsWith('}') ? layout[1..^1].Trim() : layout);
            }
            continue;
          }
          // public Point(i32 x, i32 y) -> symbol [error]: a constructor, read as a static function that the compiler turns into it
          var ctor = DfiCtorLine.Match(text);
          if (ctor.Success && ctor.Groups["name"].Value == className) {
            kept[line] = $"public static fun {className} dfiCtor({ctor.Groups["params"].Value})";
            symbols[nsBase + line + 1] = (ctor.Groups["sym"].Value, ctor.Groups["err"].Success);
            ctorLines.Add(nsBase + line + 1);
            continue;
          }
          var method = DfiFunLine.Match(text);
          if (method.Success) {
            kept[line] = method.Groups["decl"].Value;
            symbols[nsBase + line + 1] = (method.Groups["sym"].Value, method.Groups["err"].Success);
            if (method.Groups["unsafe"].Success) {
              taskUnsafe.Add(nsBase + line + 1);
            }
            continue;
          }
          kept[line] = text;   // a public field
          continue;
        }
        // class Labeled : Point -> df_cls_... {
        var classHeader = DfiClassLine.Match(text);
        if (classHeader.Success) {
          className = classHeader.Groups["name"].Value;
          classLine = nsBase + line + 1;
          kept[line] = $"public class {className}{classHeader.Groups["bases"].Value} {{";
          records[classLine] = (classHeader.Groups["record"].Value, null);
          continue;
        }
        if (text.StartsWith("public enum ") || text.StartsWith("public bitfield")) {
          kept[line] = text;
          continue;
        }
        var fun = DfiFunLine.Match(text);
        if (fun.Success) {
          kept[line] = fun.Groups["decl"].Value;
          symbols[nsBase + line + 1] = (fun.Groups["sym"].Value, fun.Groups["err"].Success);
          if (fun.Groups["unsafe"].Success) {
            taskUnsafe.Add(nsBase + line + 1);
          }
        }
        // public global Sequence Numbers -> gl_counters_Numbers lock 0: a global of the library (its lock is the library's)
        var global = DfiGlobalLine.Match(text);
        if (global.Success) {
          kept[line] = "public global " + global.Groups["decl"].Value;
          symbols[nsBase + line + 1] = (global.Groups["sym"].Value, false);
          continue;
        }
        // public string greeting -> g_forms_greeting: a variable of the library
        var variable = DfiVarLine.Match(text);
        if (variable.Success && !text.StartsWith("atLibStart") && !text.StartsWith("atEnd")) {
          kept[line] = "public " + variable.Groups["decl"].Value;
          symbols[nsBase + line + 1] = (variable.Groups["sym"].Value, false);
        }
        // atLibStart -> df_libstart_counters, atEnd -> df_libend_counters: the start and the end of the library (spec 15.8)
        var startEnd = System.Text.RegularExpressions.Regex.Match(text, @"^(atLibStart|atEnd|ids)\s*->\s*([A-Za-z_][A-Za-z0-9_]*)(\s+error)?\s*$");
        if (startEnd.Success) {
          var entry = (startEnd.Groups[2].Value, startEnd.Groups[3].Success);
          if (startEnd.Groups[1].Value == "ids") {
            library.Ids = entry.Item1;   // the ids of its classes and exceptions, given by the program (spec 15.8)
          } else if (startEnd.Groups[1].Value == "atLibStart") {
            library.Start = entry;
          } else {
            library.End = entry;
          }
        }
      }
      Node tree;
      try {
        var lexer = new Lexer(string.Join("\n", kept), nsBase + 1);
        tree = new Parser(lexer.Tokenize()).ParseProgram();
      } catch (DfException ex) {
        throw new CompileException($"the interface cannot be read: {ex.Message} (spec 15.8)", ex.Line);
      }
      void Mark(Node fun) {
        if (symbols.TryGetValue(fun.Line, out var symbol)) {
          fun.Add(new Node("Imported", new Token(TokenKind.Ident, symbol.Symbol, fun.Line, 0), symbol.Symbol));
          if (symbol.Error) {
            fun.Add(new Node("ImportedError", new Token(TokenKind.Ident, "error", fun.Line, 0)));
          }
          if (taskUnsafe.Contains(fun.Line)) {
            fun.Add(new Node("ImportedTaskUnsafe", new Token(TokenKind.Ident, "taskunsafe", fun.Line, 0)));
          }
          if (ctorLines.Contains(fun.Line)) {
            fun.Add(new Node("ImportedCtor", new Token(TokenKind.Ident, "", fun.Line, 0)));
          }
        }
      }
      foreach (var statement in tree.Children) {
        if (statement.Kind is "Fun" or "Decl") {
          Mark(statement);
        } else if (statement.Kind == "Class" && records.TryGetValue(statement.Line, out var record)) {
          if (record.Layout == null) {
            throw new CompileException($"the class {statement.Text} of the library {name} has no layout for {Target} (layout-{Target}:): build the library for this target (spec 15.8)", statement.Line);
          }
          var where = new Token(TokenKind.Ident, "", statement.Line, 0);
          statement.Add(new Node("ImportedRecord", where, record.Record));
          statement.Add(new Node("ImportedLayout", where, record.Layout));
          foreach (var member in statement.Children.First(child => child.Kind == "Block").Children.Where(member => member.Kind == "Fun")) {
            Mark(member);
          }
        }
      }
      if (!compiledFiles.ContainsKey(part)) {
        compiledFiles[part] = new SourceFile(full + "#" + part, tree, nsIndex) { Namespace = part };
      }
    }
  }

  // class Name [: Parent, Trait] -> <record> {
  static readonly System.Text.RegularExpressions.Regex DfiClassLine =
    new(@"^class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<bases>(\s*:\s*[^-{]+?)?)\s*->\s*(?<record>[A-Za-z_][A-Za-z0-9_]*)\s*\{$");

  // public Name(<params>) -> <symbol> [error]: a constructor
  static readonly System.Text.RegularExpressions.Regex DfiCtorLine =
    new(@"^public\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\((?<params>.*)\)\s*->\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)(?<err>\s+error)?\s*$");

  // public global <Class> <name> -> <symbol> lock <rank>
  static readonly System.Text.RegularExpressions.Regex DfiGlobalLine =
    new(@"^public\s+global\s+(?<decl>.+?)\s*->\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)\s+lock\s+\d+\s*$");

  // public <type> <name> -> <symbol>: a variable
  static readonly System.Text.RegularExpressions.Regex DfiVarLine =
    new(@"^public\s+(?<decl>[^(]+?)\s*->\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)\s*$");

  // public [static] fun <results> <name>(<params>) -> <symbol> [error]   |   unrestricted fun ...
  static readonly System.Text.RegularExpressions.Regex DfiFunLine =
    new(@"^(?<decl>(public|unrestricted)\s.*\bfun\b.*\))\s*->\s*(?<sym>[A-Za-z_][A-Za-z0-9_]*)(?<err>\s+error)?(?<unsafe>\s+taskunsafe)?\s*$");

  static IEnumerable<string> List(string? text) =>
    (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
