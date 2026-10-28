using dfparser;

namespace dfcompiler;

// The files of a compilation share one line numbering: the lines of file i start at i * Stride, so every node knows its file
// through its line. File 0 is the root file, whose lines keep their own numbers.
static class SourceMap {
  public const int Stride = 100000;
  static readonly List<string> files = new();

  public static int Add(string path) {
    files.Add(path);
    return files.Count - 1;
  }

  public static int Base(int index) => index * Stride;

  public static int RealLine(int line) => line % Stride;

  static string FileOf(int line) {
    int index = line / Stride;
    return index < files.Count ? files[index] : files[0];
  }

  // "file(line)" and "file(line,col)"
  public static string Where(int line) => $"{FileOf(line)}({RealLine(line)})";

  public static string Where(int line, int col) => $"{FileOf(line)}({RealLine(line)},{col})";

  // a warning "(line): warning ..." of the lexer, the parser or the code generator, with its file
  public static string Fix(string warning) {
    int close = warning.IndexOf(')');
    if (warning.StartsWith('(') && close > 1 && int.TryParse(warning[1..close], out int line)) {
      return Where(line) + warning[(close + 1)..];
    }
    return files[0] + warning;
  }
}

// an error of the loading of the files that is already written with its place
sealed class SourceException : Exception {
  public SourceException(string message) : base(message) {
  }
}

// Reads the root file and every file it reaches through `import` (spec 16): the roots are the folder of the program, the libraries
// given with include= and the stdlib. A multi-file project also compiles the other files of the folder of the root file. The files are
// merged into one program: the other files first, the root file last.
sealed class SourceLoader {
  sealed class SourceRoot {
    public SourceRoot(string folder, string ns, string what) {
      Folder = folder;
      Namespace = ns;
      What = what;
    }

    public string Folder { get; }
    public string Namespace { get; }    // lower case; "" for a program without a namespace line
    public string What { get; }         // for the messages
  }

  sealed class SourceFile {
    public SourceFile(string path, Node tree) {
      Path = path;
      Tree = tree;
    }

    public string Path { get; }
    public Node Tree { get; }
    public string Namespace { get; set; } = "";
  }

  static readonly HashSet<string> RootFileNames = new(StringComparer.OrdinalIgnoreCase) { "main.df", "program.df", "lib.df" };

  readonly IReadOnlyDictionary<string, PpVariable> variables;
  readonly IReadOnlyDictionary<string, PpValue> projectVariables;
  readonly Dictionary<string, SourceFile> parsed = new(StringComparer.OrdinalIgnoreCase);
  readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
  readonly List<SourceFile> order = new();
  readonly List<SourceRoot> roots = new();
  readonly Dictionary<string, string> declaredIn = new();   // top-level name -> namespace, while names are not qualified
  SourceFile? rootFile;

  public SourceLoader(IReadOnlyDictionary<string, PpVariable> variables, IReadOnlyDictionary<string, PpValue> projectVariables) {
    this.variables = variables;
    this.projectVariables = projectVariables;
  }

  // the warnings with their files, ready to print
  public List<string> Warnings { get; } = new();

  // the libraries reached through an import: their root namespace and folder (the native parts they need: NativeLibraries.cs)
  public Dictionary<string, string> ImportedLibraries { get; } = new();

  // a multi-file compilation: a project whose root file is main.df, program.df or lib.df (spec 16)
  public static bool IsMultiFile(string? projectFile, string rootPath) {
    return projectFile != null && RootFileNames.Contains(System.IO.Path.GetFileName(rootPath));
  }

  public Node Load(string rootPath, bool multiFile, IReadOnlyList<string> includes, string? projectFile) {
    rootFile = Parse(rootPath, displayPath: rootPath);
    if (multiFile && Namespaces(rootFile).Count == 0) {
      int firstLine = rootFile.Tree.Children.Count > 0 ? rootFile.Tree.Children[0].Line : 1;
      throw new CompileException("the root file of a multi-file project declares its namespace (namespace <name>)", firstLine);
    }
    string rootNs = RootNamespace(rootFile, "the root file");
    rootFile.Namespace = rootNs;
    string rootFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(rootPath))!;
    roots.Add(new SourceRoot(rootFolder, rootNs, "the program"));
    roots.Add(new SourceRoot("", "root", "the standard library"));

    if (multiFile) {
      foreach (string include in includes) {
        AddLibrary(include, projectFile!);
      }
    } else if (includes.Count > 0) {
      Warnings.Add($"{projectFile}: warning L2: include= is ignored in a single-file compilation (the root file is not main.df, program.df or lib.df)");
    }
    CheckOverlaps();

    // a multi-file project also compiles the other files of the folder of the root file; a single file ignores them
    if (multiFile) {
      foreach (string sibling in Directory.GetFiles(rootFolder, "*.df").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
        var file = Parse(sibling, sibling);
        if (file != rootFile) {
          file.Namespace = FileNamespace(file, rootNs);
          Visit(file, multiFile);
        }
      }
    }
    Visit(rootFile, multiFile);

    // the program: the imported files first (their declarations are seen before they are used), then the root file
    var program = rootFile.Tree;
    var merged = new List<Node>();
    foreach (var file in order) {
      merged.AddRange(file.Tree.Children.Where(statement => statement.Kind != "Import"));
    }
    program.Children.Clear();
    program.Children.AddRange(merged);
    return program;
  }

  // preprocessor, lexer and parser of one file; its lines are numbered from its place in the source map
  SourceFile Parse(string path, string displayPath) {
    string full = System.IO.Path.GetFullPath(path);
    if (parsed.TryGetValue(full, out var known)) {
      return known;
    }
    int index = SourceMap.Add(displayPath);
    int lineBase = SourceMap.Base(index);
    var preprocessor = new Preprocessor(variables, projectVariables);
    string text;
    try {
      text = preprocessor.Process(File.ReadAllText(full));
    } catch (DfException ex) {
      throw new DfException(ex.Message, lineBase + ex.Line, ex.Col);
    }
    if (text.Count(c => c == '\n') >= SourceMap.Stride - 1) {
      throw new SourceException($"{displayPath}: error: a file can have at most {SourceMap.Stride - 2} lines");
    }
    var lexer = new Lexer(text, lineBase + 1);
    var parser = new Parser(lexer.Tokenize());
    var tree = parser.ParseProgram();
    Warnings.AddRange(preprocessor.Warnings.Select(warning => SourceMap.Fix(Shift(warning, lineBase))));
    Warnings.AddRange(lexer.Warnings.Concat(parser.Warnings).Select(SourceMap.Fix));
    var file = new SourceFile(full, tree);
    parsed[full] = file;
    return file;
  }

  // the preprocessor counts the lines of its file from 1
  static string Shift(string warning, int lineBase) {
    int close = warning.IndexOf(')');
    if (warning.StartsWith('(') && close > 1 && int.TryParse(warning[1..close], out int line)) {
      return $"({lineBase + line}){warning[(close + 1)..]}";
    }
    return warning;
  }

  static List<Node> Namespaces(SourceFile file) {
    var found = file.Tree.Children.Where(statement => statement.Kind is "Namespace" or "NamespaceLocal").ToList();
    if (found.Count > 1) {
      throw new CompileException("a file has one namespace line only", found[1].Line);
    }
    return found;
  }

  // the namespace written in a root file; a program without it has the empty namespace
  static string RootNamespace(SourceFile file, string what) {
    var found = Namespaces(file);
    if (found.Count == 0) {
      return "";
    }
    if (found[0].Kind == "NamespaceLocal") {
      throw new CompileException($"{what} declares its namespace explicitly: namespace local needs a root namespace to start from", found[0].Line);
    }
    return found[0].Text!.ToLowerInvariant();
  }

  // a library: a folder with lib.df at the top, which declares the root namespace of the library
  void AddLibrary(string folder, string projectFile) {
    if (!Directory.Exists(folder)) {
      throw new SourceException($"{projectFile}: error: the library folder {folder} does not exist");
    }
    string lib = System.IO.Path.Combine(folder, "lib.df");
    if (!File.Exists(lib)) {
      throw new SourceException($"{projectFile}: error: the library {folder} has no lib.df (its root file)");
    }
    var file = Parse(lib, lib);
    if (Namespaces(file).Count == 0) {
      throw new SourceException($"{lib}: error: the root file of a library declares its root namespace");
    }
    string ns = RootNamespace(file, "the root file of a library");
    file.Namespace = ns;
    roots.Add(new SourceRoot(System.IO.Path.GetFullPath(folder), ns, "the library " + folder));
  }

  // two roots with the same namespace, or one inside the other (the stdlib included), are an error
  void CheckOverlaps() {
    for (int i = 0; i < roots.Count; i++) {
      for (int j = i + 1; j < roots.Count; j++) {
        string a = roots[i].Namespace, b = roots[j].Namespace;
        if (a == "" || b == "") {
          continue;
        }
        if (a == b || a.StartsWith(b + ".") || b.StartsWith(a + ".")) {
          throw new SourceException($"error: the namespace '{a}' of {roots[i].What} and '{b}' of {roots[j].What} overlap: "
            + "two roots cannot have the same namespace or one inside the other");
        }
      }
    }
  }

  // a file and, before it, everything it imports
  void Visit(SourceFile file, bool multiFile) {
    if (!visited.Add(file.Path)) {
      return;
    }
    CheckFile(file);
    foreach (var import in file.Tree.Children.Where(statement => statement.Kind == "Import")) {
      foreach (var imported in Resolve(import, multiFile)) {
        Visit(imported, multiFile);
      }
    }
    order.Add(file);
  }

  // only the root file has top-level code, and a name is declared in one namespace only (names are not qualified yet)
  void CheckFile(SourceFile file) {
    bool isRoot = file == rootFile;
    foreach (var statement in file.Tree.Children) {
      bool declaration = statement.Kind is "Fun" or "Alias" or "Enum" or "Class" or "Trait" or "GenericSet"
        or "Namespace" or "NamespaceLocal" or "Import" || IsConst(statement);
      if (!isRoot && !declaration) {
        throw new CompileException("only the root file can have top-level code: this file can have only declarations", statement.Line);
      }
      if (!isRoot && statement.Kind == "Fun" && statement.Text == "main") {
        throw new CompileException("main can be only in the root file", statement.Line);
      }
      if (declaration && statement.Text != null && statement.Kind is not ("Namespace" or "NamespaceLocal" or "Import")) {
        if (declaredIn.TryGetValue(statement.Text, out var other) && other != file.Namespace) {
          throw new CompileException($"'{statement.Text}' is declared in the namespace '{Show(other)}' and in '{Show(file.Namespace)}': "
            + "names qualified by their namespace are not built yet, so a name can be used in one namespace only", statement.Line);
        }
        declaredIn[statement.Text] = file.Namespace;
      }
    }
  }

  static string Show(string ns) => ns == "" ? "(none)" : ns;

  static bool IsConst(Node statement) {
    return statement.Kind == "Decl"
      && statement.Children.FirstOrDefault(child => child.Kind == "Mods")?.Text?.Split(' ').Contains("const") == true;
  }

  // import a.b.c: the root whose namespace starts the path, then down its folders; all the .df files of the folder
  List<SourceFile> Resolve(Node import, bool multiFile) {
    string name = import.Text!;
    string lower = name.ToLowerInvariant();
    SourceRoot? best = null;
    foreach (var root in roots) {
      bool matches = root.Namespace == "" || lower == root.Namespace || lower.StartsWith(root.Namespace + ".");
      if (matches && (best == null || root.Namespace.Length > best.Namespace.Length)) {
        best = root;
      }
    }
    if (best == null) {
      throw NotFound(name, import, multiFile);
    }
    if (best.Namespace == "root") {
      return new List<SourceFile>();   // the stdlib is part of the compiler
    }
    if (best.What.StartsWith("the library ")) {
      ImportedLibraries[best.Namespace] = best.Folder;
    }
    string rest = best.Namespace == "" ? lower : lower == best.Namespace ? "" : lower[(best.Namespace.Length + 1)..];
    string folder = best.Folder;
    var segments = new List<string>();
    foreach (string segment in rest.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
      var matching = Directory.GetDirectories(folder)
        .Where(dir => string.Equals(System.IO.Path.GetFileName(dir), segment, StringComparison.OrdinalIgnoreCase)).ToList();
      if (matching.Count > 1) {
        throw new CompileException($"the folders {string.Join(" and ", matching.Select(System.IO.Path.GetFileName))} differ only by case", import.Line);
      }
      if (matching.Count == 0) {
        throw NotFound(name, import, multiFile);
      }
      folder = matching[0];
      segments.Add(System.IO.Path.GetFileName(folder));
    }
    string expected = string.Join(".", new[] { best.Namespace }.Concat(segments.Select(s => s.ToLowerInvariant())).Where(s => s != ""));
    var files = new List<SourceFile>();
    foreach (string path in Directory.GetFiles(folder, "*.df").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
      var file = Parse(path, path);
      file.Namespace = FileNamespace(file, expected);
      files.Add(file);
    }
    if (files.Count == 0) {
      throw new CompileException($"the folder of the namespace '{name}' has no .df file", import.Line);
    }
    return files;
  }

  // a file of a folder is in the namespace of the folder: written in full or as namespace local
  string FileNamespace(SourceFile file, string expected) {
    if (file == rootFile) {
      return file.Namespace;
    }
    var found = Namespaces(file);
    int firstLine = file.Tree.Children.Count > 0 ? file.Tree.Children[0].Line : SourceMap.Base(0);
    if (found.Count == 0) {
      throw new CompileException($"this file needs the line namespace {Show(expected)} (or namespace local)", firstLine);
    }
    if (found[0].Kind == "Namespace" && found[0].Text!.ToLowerInvariant() != expected) {
      throw new CompileException($"the namespace of this file is the one of its folder: {Show(expected)}, not {found[0].Text}", found[0].Line);
    }
    return expected;
  }

  static CompileException NotFound(string name, Node import, bool multiFile) {
    string hint = multiFile ? "" : " (a single-file compilation can import only its own subfolders and the stdlib)";
    return new CompileException($"cannot find the namespace '{name}'{hint}", import.Line);
  }
}
