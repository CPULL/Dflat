using System.Diagnostics;
using dfparser;

namespace dfcompiler;

public static class Program {
  public const string Version = "0.0.11alpha";

  // the targets of --target and the clang triple of each (a target equal to this machine passes none: clang's own default)
  static readonly Dictionary<string, string> Targets = new() {
    ["win-x64"] = "x86_64-pc-windows-msvc",
    ["win-x32"] = "i686-pc-windows-msvc",
    ["linux-x64"] = "x86_64-unknown-linux-gnu",
    ["linux-arm64"] = "aarch64-unknown-linux-gnu",
    ["osx-x64"] = "x86_64-apple-darwin",
    ["osx-arm64"] = "arm64-apple-darwin"
  };

  const string Usage = "usage: dfcompiler <file.df> [--output <file>] [--target <target>] [--emit-c] [--cc <path-to-clang>] "
    + "[--run [-- program arguments]] | --version\n"
    + "  target: win-x64 win-x32 linux-x64 linux-arm64 osx-x64 osx-arm64, or CL:<options passed as they are to clang>\n"
    + "  option names are not case sensitive; --run must be the last option, the program arguments follow --";

  public static int Main(string[] args) {
    string? path = null;
    string? output = null;
    string? target = null;
    string? cc = Environment.GetEnvironmentVariable("DFLAT_CC");
    bool emitOnly = false;
    bool run = false;
    var programArgs = new List<string>();

    // every option starts with two dashes; those that take a value read the next argument
    string? Value(ref int index, string option) {
      if (index + 1 >= args.Length) {
        Console.Error.WriteLine($"{option} needs a value\n{Usage}");
        return null;
      }
      return args[++index];
    }

    for (int i = 0; i < args.Length; i++) {
      if (args[i] == "--") {
        // everything after -- goes to the program, and only --run (the last option) can have arguments
        if (!run) {
          Console.Error.WriteLine($"the program arguments after -- need --run as the last option before them\n{Usage}");
          return 2;
        }
        programArgs.AddRange(args.Skip(i + 1));
        break;
      }
      // option names are not case sensitive (what follows CL: and the file names are not options)
      string option = args[i].StartsWith("--") ? args[i].ToLowerInvariant() : args[i];
      switch (option) {
        case "--version":
          Console.WriteLine($"dfcompiler {Version}");
          return 0;
        case "--emit-c":
          emitOnly = true;
          break;
        case "--run":
          run = true;
          if (i + 1 < args.Length && args[i + 1] != "--") {
            Console.Error.WriteLine($"--run must be the last option: write the program arguments after --, as in --run -- first second\n{Usage}");
            return 2;
          }
          break;
        case "--output":
          if ((output = Value(ref i, "--output")) == null) {
            return 2;
          }
          break;
        case "--target":
          if ((target = Value(ref i, "--target")) == null) {
            return 2;
          }
          break;
        case "--cc":
          if ((cc = Value(ref i, "--cc")) == null) {
            return 2;
          }
          break;
        default:
          if (args[i].StartsWith('-')) {
            Console.Error.WriteLine($"unknown option {args[i]}\n{Usage}");
            return 2;
          }
          path = args[i];
          break;
      }
    }

    if (path == null) {
      Console.Error.WriteLine(Usage);
      return 2;
    }
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
      var lexer = new Lexer(File.ReadAllText(path));
      var tokens = lexer.Tokenize();
      var parser = new Parser(tokens);
      var tree = parser.ParseProgram();
      var (c, genWarnings) = CodeGen.Compile(tree);
      foreach (var warning in lexer.Warnings.Concat(parser.Warnings).Concat(genWarnings)) {
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
    var ccArgs = new List<string> {
      "-std=c11", "-O2", "-fwrapv", "-Wno-unused-function",
      "-Wno-parentheses-equality", "-Wno-tautological-compare", "-o", output, cPath
    };
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
