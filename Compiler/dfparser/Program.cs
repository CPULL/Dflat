namespace dfparser;

public static class Program {
  public const string Version = "0.0.1alpha";

  public static int Main(string[] args) {
    bool showTokens = false;
    string? path = null;
    foreach (var arg in args) {
      if (arg is "-version" or "--version") {
        Console.WriteLine($"dfparser {Version}");
        return 0;
      }
      if (arg == "--tokens") {
        showTokens = true;
      } else {
        path = arg;
      }
    }

    if (path == null) {
      Console.Error.WriteLine("usage: dfparser [--tokens] <file.df> | -version");
      return 2;
    }
    if (!File.Exists(path)) {
      Console.Error.WriteLine($"file not found: {path}");
      return 2;
    }

    string src = File.ReadAllText(path);
    try {
      // the preprocessor of spec 19.1, with an x64 machine of this system (dfcompiler gives it the variables of its options)
      string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
      var preprocessor = new Preprocessor(PpVariables.Build(os, "x64", 64, "debug", 2, "speed", "0.0.12"));
      src = preprocessor.Process(src);
      var lexer = new Lexer(src);
      var tokens = lexer.Tokenize();
      if (showTokens) {
        foreach (var token in tokens) {
          Console.WriteLine(token);
        }
        return 0;
      }
      var parser = new Parser(tokens);
      var tree = parser.ParseProgram();
      foreach (var warning in preprocessor.Warnings.Concat(lexer.Warnings).Concat(parser.Warnings)) {
        Console.Error.WriteLine(path + warning);
      }
      tree.Dump(Console.Out, 0);
      return 0;
    } catch (DfException ex) {
      Console.Error.WriteLine($"{path}({ex.Line},{ex.Col}): error: {ex.Message}");
      return 1;
    }
  }
}
