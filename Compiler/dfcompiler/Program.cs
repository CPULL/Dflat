using System.Diagnostics;
using dfparser;

namespace dfcompiler;

public static class Program {
  public const string Version = "0.0.15alpha";

  // the version of the specification this compiler was built for: the variable `version` of the preprocessor (spec 19.1)
  public const string LanguageVersion = "0.0.13";

  // the targets of --target and the clang triple of each (a target equal to this machine passes none: clang's own default)
  static readonly Dictionary<string, string> Targets = new() {
    ["win-x64"] = "x86_64-pc-windows-msvc",
    ["win-x32"] = "i686-pc-windows-msvc",
    ["linux-x64"] = "x86_64-unknown-linux-gnu",
    ["linux-arm64"] = "aarch64-unknown-linux-gnu",
    ["osx-x64"] = "x86_64-apple-darwin",
    ["osx-arm64"] = "arm64-apple-darwin"
  };

  const string Usage = "usage: dfcompiler <file.df | project.dsprj | folder> [--output <file>] [--target <target>] [--emit-c] [--cc <path-to-clang>] "
    + "[--coptlev <0-3>] [--coptmode <size|speed>] [--debug | --release] [--run [-- program arguments]] | --version\n"
    + "  target: win-x64 win-x32 linux-x64 linux-arm64 osx-x64 osx-arm64, or CL:<options passed as they are to clang>\n"
    + "  option names are not case sensitive; --run must be the last option, the program arguments follow --";

  // the options of a build, from the command line or from a project file (a value that is null was not given)
  sealed class Settings {
    public string? Source { get; set; }
    public string? Output { get; set; }
    public string? Target { get; set; }
    public string? Cc { get; set; }
    public bool? EmitC { get; set; }
    public int? OptLevel { get; set; }
    public string? OptMode { get; set; }
    public string? Build { get; set; }
    public bool Run { get; set; }
    public List<string> ProgramArgs { get; } = new();
  }

  sealed class UsageException : Exception {
    public UsageException(string message) : base(message) {
    }
  }

  public static int Main(string[] args) {
    var cli = new Settings();
    string? input = null;
    try {
      input = ReadCommandLine(args, cli);
    } catch (UsageException ex) {
      Console.Error.WriteLine($"{ex.Message}\n{Usage}");
      return 2;
    }
    if (input == null && args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase)) {
      Console.WriteLine($"dfcompiler {Version} (language version {LanguageVersion})");
      return 0;
    }
    if (input == null) {
      Console.Error.WriteLine(Usage);
      return 2;
    }

    // a project file: its options are the defaults, the command line wins (spec 19, Compiler_Options.md)
    var project = new Settings();
    var projectVariables = new Dictionary<string, PpValue>();
    string? projectFile = null;
    if (Directory.Exists(input)) {
      var files = Directory.GetFiles(input, "*.dsprj");
      if (files.Length == 0) {
        Console.Error.WriteLine($"no .dsprj file in the folder {input}");
        return 2;
      }
      if (files.Length > 1) {
        Console.Error.WriteLine($"the folder {input} has several .dsprj files: give the one to use");
        return 2;
      }
      projectFile = files[0];
    } else if (input.EndsWith(".dsprj", StringComparison.OrdinalIgnoreCase)) {
      projectFile = input;
    }
    if (projectFile != null) {
      if (!File.Exists(projectFile)) {
        Console.Error.WriteLine($"file not found: {projectFile}");
        return 2;
      }
      try {
        ReadProject(projectFile, project, projectVariables);
      } catch (UsageException ex) {
        Console.Error.WriteLine($"{ex.Message}");
        return 2;
      }
      if (project.Source == null) {
        Console.Error.WriteLine($"{projectFile}: the project needs the file to compile: source=<file.df>");
        return 2;
      }
    } else {
      cli.Source = input;
    }

    string path = cli.Source ?? project.Source!;
    string? output = cli.Output ?? project.Output;
    string? target = cli.Target ?? project.Target;
    string? cc = cli.Cc ?? project.Cc ?? Environment.GetEnvironmentVariable("DFLAT_CC");
    bool emitOnly = cli.EmitC ?? project.EmitC ?? false;
    int optLevel = cli.OptLevel ?? project.OptLevel ?? 2;
    string optMode = cli.OptMode ?? project.OptMode ?? "speed";
    string build = cli.Build ?? project.Build ?? "debug";
    bool run = cli.Run;
    var programArgs = cli.ProgramArgs;

    if (!File.Exists(path)) {
      Console.Error.WriteLine($"file not found: {path}");
      return 2;
    }

    // the target: this machine unless --target says otherwise; CL:<text> is passed to clang as it is
    string host = HostTarget();
    bool custom = target != null && target.StartsWith("CL:");
    if (target != null && !custom && !Targets.ContainsKey(target)) {
      Console.Error.WriteLine($"unknown target '{target}': use {string.Join(", ", Targets.Keys)}, or CL:<options for clang>");
      return 2;
    }
    string effective = custom ? "custom" : target ?? host;
    bool forHost = !custom && effective == host;
    bool forWindows = !custom && effective.StartsWith("win-");

    // the generated C goes to .compilation and the executable to build.<target>, both next to the source file
    string sourceDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
    string name = Path.GetFileNameWithoutExtension(path);
    string cPath = Path.Combine(sourceDir, ".compilation", name + ".c");
    output ??= Path.Combine(sourceDir, "build." + effective, forWindows ? name + ".exe" : name);

    try {
      // the preprocessor (spec 19.1) builds the code that is compiled; its variables come from the options
      var variables = PpVariables.Build(custom || !Targets.ContainsKey(effective) ? host : effective, build, optLevel, optMode, LanguageVersion);
      Preprocessor preprocessor;
      try {
        preprocessor = new Preprocessor(variables, projectVariables);
      } catch (DfException ex) {
        Console.Error.WriteLine($"{projectFile}: error: {ex.Message}");
        return 1;
      }
      string text = preprocessor.Process(File.ReadAllText(path));
      var lexer = new Lexer(text);
      var tokens = lexer.Tokenize();
      var parser = new Parser(tokens);
      var tree = parser.ParseProgram();
      string typeTarget = custom || !Targets.ContainsKey(effective) ? host : effective;
      var (c, genWarnings) = CodeGen.Compile(tree, build == "release", typeTarget.EndsWith("x64") || typeTarget.EndsWith("arm64") ? 64 : 32);
      foreach (var warning in preprocessor.Warnings.Concat(lexer.Warnings).Concat(parser.Warnings).Concat(genWarnings)) {
        Console.Error.WriteLine(path + warning);
      }
      Directory.CreateDirectory(Path.GetDirectoryName(cPath)!);
      File.WriteAllText(cPath, c);
    } catch (DfException ex) {
      Console.Error.WriteLine($"{path}({ex.Line},{ex.Col}): error: {ex.Message}");
      return 1;
    } catch (CompileException ex) {
      Console.Error.WriteLine($"{path}({ex.Line}): error: {ex.Message}");
      return 1;
    }

    Console.WriteLine($"C code: {cPath}");
    if (emitOnly) {
      return 0;
    }

    string? clang = FindClang(cc);
    if (clang == null) {
      Console.Error.WriteLine("clang not found: add it to PATH, set DFLAT_CC, or pass --cc <path>");
      return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    string optimization = optMode == "size" ? "-Os" : "-O" + optLevel;
    var ccArgs = new List<string> {
      "-std=c11", optimization, "-fwrapv", "-Wno-unused-function",
      "-Wno-parentheses-equality", "-Wno-tautological-compare", "-o", output, cPath
    };
    if (build == "release") {
      ccArgs.Insert(0, "-DDF_RELEASE");   // an index outside a collection is a panic (spec 6.1)
    }
    if (custom) {
      // the text after CL: as it is, split at the spaces; nothing else is added for the target
      ccArgs.AddRange(target!["CL:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
    } else {
      if (!forHost) {
        ccArgs.Insert(0, "--target=" + Targets[effective]);
      }
      if (forWindows) {
        ccArgs.Insert(0, "-D_CRT_SECURE_NO_WARNINGS");
      } else {
        ccArgs.Add("-lm");
      }
    }
    int code = RunProcess(clang, ccArgs);
    if (code != 0) {
      Console.Error.WriteLine($"clang failed with exit code {code}");
      return 1;
    }
    Console.WriteLine($"Executable: {output}");

    if (run) {
      if (!forHost) {
        Console.Error.WriteLine($"not run: the program is built for {effective} and this machine is {host}");
        return 0;
      }
      Console.WriteLine("---- run ----");
      int exit = RunProcess(Path.GetFullPath(output), programArgs);
      Console.WriteLine($"---- exit code {exit} ----");
    }
    return 0;
  }

  // the command line: the input (a .df file, a .dsprj file or a folder) and the options; the options are not case sensitive
  static string? ReadCommandLine(string[] args, Settings settings) {
    string? input = null;

    // every option starts with two dashes; those that take a value read the next argument
    string Value(ref int index, string option) {
      if (index + 1 >= args.Length) {
        throw new UsageException($"{option} needs a value");
      }
      return args[++index];
    }

    for (int i = 0; i < args.Length; i++) {
      if (args[i] == "--") {
        // everything after -- goes to the program, and only --run (the last option) can have arguments
        if (!settings.Run) {
          throw new UsageException("the program arguments after -- need --run as the last option before them");
        }
        settings.ProgramArgs.AddRange(args.Skip(i + 1));
        break;
      }
      // option names are not case sensitive (what follows CL: and the file names are not options)
      string option = args[i].StartsWith("--") ? args[i].ToLowerInvariant() : args[i];
      switch (option) {
        case "--version":
          if (args.Length != 1) {
            throw new UsageException("--version is used alone");
          }
          return null;
        case "--emit-c":
          settings.EmitC = true;
          break;
        case "--run":
          settings.Run = true;
          if (i + 1 < args.Length && args[i + 1] != "--") {
            throw new UsageException("--run must be the last option: write the program arguments after --, as in --run -- first second");
          }
          break;
        case "--output":
          settings.Output = Value(ref i, "--output");
          break;
        case "--target":
          settings.Target = Value(ref i, "--target");
          break;
        case "--cc":
          settings.Cc = Value(ref i, "--cc");
          break;
        case "--coptlev":
          settings.OptLevel = ParseOptLevel(Value(ref i, "--COptLev"));
          break;
        case "--coptmode":
          settings.OptMode = ParseOptMode(Value(ref i, "--COptMode"));
          break;
        case "--debug":
          settings.Build = "debug";
          break;
        case "--release":
          settings.Build = "release";
          break;
        default:
          if (args[i].StartsWith('-')) {
            throw new UsageException($"unknown option {args[i]}");
          }
          input = args[i];
          break;
      }
    }
    return input;
  }

  static int ParseOptLevel(string text) {
    if (!int.TryParse(text, out int level) || level < 0 || level > 3) {
      throw new UsageException($"--COptLev takes a number from 0 to 3, not '{text}'");
    }
    return level;
  }

  static string ParseOptMode(string text) {
    string mode = text.ToLowerInvariant();
    if (mode is not ("size" or "speed")) {
      throw new UsageException($"--COptMode takes size or speed, not '{text}'");
    }
    return mode;
  }

  // The project file (.dsprj): one `key = value` per line, the key is the option name without the dashes, `;` starts a comment, paths are
  // relative to the folder of the file. CompilerVar=<name>=<value> defines a variable of the preprocessor. The options that are not built
  // yet are an error.
  static void ReadProject(string file, Settings settings, Dictionary<string, PpValue> variables) {
    string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
    string[] lines = File.ReadAllLines(file);
    for (int index = 0; index < lines.Length; index++) {
      string line = lines[index].Trim();
      if (line.Length == 0 || line.StartsWith(';')) {
        continue;
      }
      string where = $"{file}({index + 1})";
      int equal = line.IndexOf('=');
      if (equal <= 0) {
        throw new UsageException($"{where}: error: write option = value");
      }
      string key = line[..equal].Trim().ToLowerInvariant();
      string value = line[(equal + 1)..].Trim();
      try {
        switch (key) {
          case "source":
          case "root":
            settings.Source = Path.Combine(folder, value);
            break;
          case "output":
            settings.Output = value.EndsWith('/') || value.EndsWith('\\') ? throw new UsageException("an output folder is not built yet: give the file") : Path.Combine(folder, value);
            break;
          case "target":
            settings.Target = value;
            break;
          case "cc":
            settings.Cc = value;
            break;
          case "emit-c":
            settings.EmitC = ParseBool(value, key);
            break;
          case "coptlev":
            settings.OptLevel = ParseOptLevel(value);
            break;
          case "coptmode":
            settings.OptMode = ParseOptMode(value);
            break;
          case "build":
            settings.Build = value is "debug" or "release" ? value : throw new UsageException("build is debug or release");
            break;
          case "compilervar":
            DefineVariable(value, variables);
            break;
          case "run":
          case "version":
            throw new UsageException($"{key} cannot be used in a project file");
          default:
            throw new UsageException($"the option '{key}' is not built yet");
        }
      } catch (UsageException ex) {
        throw new UsageException($"{where}: error: {ex.Message}");
      }
    }
  }

  static bool ParseBool(string value, string key) {
    return value.ToLowerInvariant() switch {
      "true" => true,
      "false" => false,
      _ => throw new UsageException($"{key} is true or false")
    };
  }

  // CompilerVar=<variable name>=<value>: true, false, a number, or a text without spaces
  static void DefineVariable(string text, Dictionary<string, PpValue> variables) {
    int equal = text.IndexOf('=');
    if (equal <= 0) {
      throw new UsageException("CompilerVar is CompilerVar=<name>=<value>");
    }
    string name = text[..equal].Trim();
    string value = text[(equal + 1)..].Trim();
    if (value.Length == 0 || value.Any(char.IsWhiteSpace)) {
      throw new UsageException($"the value of {name} is true, false, a number, or a text without spaces");
    }
    if (variables.ContainsKey(name)) {
      throw new UsageException($"the variable {name} cannot be redefined");
    }
    variables[name] = value switch {
      "true" => PpValue.OfBool(true),
      "false" => PpValue.OfBool(false),
      _ when long.TryParse(value, out long number) => PpValue.OfNumber(number),
      _ => PpValue.OfWord(value)
    };
  }

  // win-x64, win-x32, linux-x64, linux-arm64, osx-x64, osx-arm64...: the system and the processor of this machine
  static string HostTarget() {
    string system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch {
      System.Runtime.InteropServices.Architecture.X64 => "x64",
      System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
      System.Runtime.InteropServices.Architecture.X86 => "x32",
      System.Runtime.InteropServices.Architecture.Arm => "arm",
      var other => other.ToString().ToLowerInvariant()
    };
    return $"{system}-{arch}";
  }

  static int RunProcess(string file, List<string> args) {
    var psi = new ProcessStartInfo(file) {
      UseShellExecute = false
    };
    foreach (var a in args) {
      psi.ArgumentList.Add(a);
    }
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
  }

  static string? FindClang(string? explicitPath) {
    if (!string.IsNullOrEmpty(explicitPath)) {
      return File.Exists(explicitPath) ? explicitPath : null;
    }
    string exe = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      try {
        string candidate = Path.Combine(dir.Trim(), exe);
        if (File.Exists(candidate)) {
          return candidate;
        }
      } catch (ArgumentException) {
        // malformed PATH entry
      }
    }
    if (!OperatingSystem.IsWindows()) {
      return null;
    }

    // Visual Studio's bundled clang, then the standalone LLVM installer
    var roots = new[] {
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    };
    foreach (var root in roots) {
      string vs = Path.Combine(root, "Microsoft Visual Studio");
      if (Directory.Exists(vs)) {
        var options = new EnumerationOptions {
          RecurseSubdirectories = true,
          IgnoreInaccessible = true
        };
        var found = Directory.EnumerateFiles(vs, "clang.exe", options)
          .Where(f => f.Contains(Path.Combine("Llvm", "x64", "bin"), StringComparison.OrdinalIgnoreCase))
          .OrderByDescending(f => f)
          .FirstOrDefault();
        if (found != null) {
          return found;
        }
      }
      string llvm = Path.Combine(root, "LLVM", "bin", "clang.exe");
      if (File.Exists(llvm)) {
        return llvm;
      }
    }
    return null;
  }
}
