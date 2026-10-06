namespace dfparse;

public static class Program {
  public static int Main(string[] args) {
    bool showTokens = false;
    string? path = null;
    foreach (var a in args) {
      if (a == "--tokens") {
        showTokens = true;
      } else {
        path = a;
      }
    }

    if (path == null) {
      Console.Error.WriteLine("usage: dfparse [--tokens] <file.df>");
      return 2;
    }
    if (!File.Exists(path)) {
      Console.Error.WriteLine($"file not found: {path}");
      return 2;
    }

    string src = File.ReadAllText(path);
    try {
      var tokens = new Lexer(src).Tokenize();
      if (showTokens) {
        foreach (var t in tokens) {
          Console.WriteLine(t);
        }
        return 0;
      }
      var tree = new Parser(tokens).ParseProgram();
      tree.Dump(Console.Out, 0);
      return 0;
    } catch (DfException ex) {
      Console.Error.WriteLine($"{path}({ex.Line},{ex.Col}): error: {ex.Message}");
      return 1;
    }
  }
}
