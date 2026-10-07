using System.Diagnostics;
using dfparse;

namespace dfcompile;

public static class Program {
  public static int Main(string[] args) {
    string? path = null;
    string? output = null;
    string? cc = Environment.GetEnvironmentVariable("DFLAT_CC");
    bool emitOnly = false;
    bool run = false;

    for (int i = 0; i < args.Length; i++) {
      switch (args[i]) {
        case "--emit-c":
          emitOnly = true;
          break;
        case "--run":
          run = true;
          break;
        case "-o":
          output = i + 1 < args.Length ? args[++i] : null;
          break;
        case "--cc":
          cc = i + 1 < args.Length ? args[++i] : null;
          break;
        default:
          path = args[i];
          break;
      }
    }

    if (path == null) {
      Console.Error.WriteLine("usage: dfcompile <file.df> [-o out] [--emit-c] [--run] [--cc path-to-clang]");
      return 2;
    }
    if (!File.Exists(path)) {
      Console.Error.WriteLine($"file not found: {path}");
      return 2;
    }

    string cPath = Path.ChangeExtension(path, ".c");
    output ??= OperatingSystem.IsWindows() ? Path.ChangeExtension(path, ".exe") : Path.ChangeExtension(path, null);

    try {
      var tokens = new Lexer(File.ReadAllText(path)).Tokenize();
      var tree = new Parser(tokens).ParseProgram();
      var gen = new CodeGen();
      string c = gen.Generate(tree);
      foreach (var w in gen.Warnings) {
        Console.Error.WriteLine(path + w);
      }
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

    var ccArgs = new List<string> {
      "-std=c11", "-O2", "-fwrapv", "-Wno-unused-function", "-o", output, cPath
    };
    if (OperatingSystem.IsWindows()) {
      ccArgs.Insert(0, "-D_CRT_SECURE_NO_WARNINGS");
    } else {
      ccArgs.Add("-lm");
    }
    int code = RunProcess(clang, ccArgs);
    if (code != 0) {
      Console.Error.WriteLine($"clang failed with exit code {code}");
      return 1;
    }
    Console.WriteLine($"Executable: {output}");

    if (run) {
      Console.WriteLine("---- run ----");
      int exit = RunProcess(Path.GetFullPath(output), new List<string>());
      Console.WriteLine($"---- exit code {exit} ----");
    }
    return 0;
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
