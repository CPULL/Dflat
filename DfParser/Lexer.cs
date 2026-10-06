namespace dfparse;

public enum TokenKind {
  Ident,
  Keyword,
  Int,
  Real,
  String,
  Char,
  Label,
  Op,
  Newline,
  Eof
}

public sealed class Token {
  public Token(TokenKind kind, string text, int line, int col) {
    Kind = kind;
    Text = text;
    Line = line;
    Col = col;
  }

  public TokenKind Kind { get; }
  public string Text { get; }
  public int Line { get; }
  public int Col { get; }

  public bool Is(string text) {
    return (Kind == TokenKind.Op || Kind == TokenKind.Keyword) && Text == text;
  }

  public override string ToString() {
    string shown = Kind switch {
      TokenKind.Newline => "\\n",
      TokenKind.Eof => "<eof>",
      _ => Text
    };
    return $"{Line,4}:{Col,-4} {Kind,-8} {shown}";
  }
}

public sealed class DfException : Exception {
  public DfException(string message, int line, int col) : base(message) {
    Line = line;
    Col = col;
  }

  public int Line { get; }
  public int Col { get; }
}

public sealed class Lexer {
  static readonly HashSet<string> Keywords = new() {
    "fun", "class", "enum", "public", "internal", "private", "const",
    "if", "else", "while", "for", "in", "step", "switch", "default",
    "jump", "return", "catch", "atEnd", "enforce", "true", "false", "null"
  };

  // Longest first. ">>" and ">>=" are not tokens: the parser joins adjacent '>'
  // so that generics like list<list<i32>> close correctly.
  static readonly string[] Ops = {
    "..=", "...", "??=", "<<=",
    "==", "!=", "<=", ">=", "&&", "||", "^^", "++", "--",
    "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", "?.", "??", "..",
    "+", "-", "*", "/", "%", "&", "|", "^", "!", "_", "=", "<", ">",
    "(", ")", "{", "}", "[", "]", ",", ";", ":", ".", "?"
  };

  readonly string src;
  readonly List<Token> tokens = new();
  int pos;
  int line = 1;
  int col = 1;

  public Lexer(string source) {
    src = source;
  }

  char Cur => pos < src.Length ? src[pos] : '\0';

  char At(int n) {
    return pos + n < src.Length ? src[pos + n] : '\0';
  }

  void Advance() {
    if (Cur == '\n') {
      line++;
      col = 1;
    } else {
      col++;
    }
    pos++;
  }

  static DfException Error(string msg, int l, int c) {
    return new DfException(msg, l, c);
  }

  public List<Token> Tokenize() {
    while (pos < src.Length) {
      char c = Cur;
      if (c == '\n') {
        AddNewline();
        Advance();
        continue;
      }
      if (char.IsWhiteSpace(c)) {
        Advance();
        continue;
      }
      if (c == '/' && At(1) == '/') {
        while (pos < src.Length && Cur != '\n') {
          Advance();
        }
        continue;
      }
      if (c == '/' && At(1) == '*') {
        SkipBlockComment();
        continue;
      }

      int l = line;
      int cl = col;
      int start = pos;

      if (char.IsLetter(c)) {
        while (char.IsLetterOrDigit(Cur) || Cur == '_') {
          Advance();
        }
        string word = src[start..pos];
        Add(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Ident, word, l, cl);
        continue;
      }
      if (char.IsDigit(c)) {
        LexNumber(l, cl);
        continue;
      }
      if (c == '"') {
        LexQuoted('"', TokenKind.String, l, cl);
        continue;
      }
      if (c == '\'') {
        LexQuoted('\'', TokenKind.Char, l, cl);
        continue;
      }
      if (c == '#') {
        LexLabel(l, cl);
        continue;
      }
      LexOp(l, cl);
    }
    AddNewline();
    tokens.Add(new Token(TokenKind.Eof, "", line, col));
    return tokens;
  }

  void Add(TokenKind kind, string text, int l, int c) {
    tokens.Add(new Token(kind, text, l, c));
  }

  // Go-style: a line break ends a statement only after a token that can end one.
  void AddNewline() {
    if (tokens.Count == 0) {
      return;
    }
    var last = tokens[^1];
    if (last.Kind != TokenKind.Newline && EndsStatement(last)) {
      tokens.Add(new Token(TokenKind.Newline, "\n", line, col));
    }
  }

  static bool EndsStatement(Token t) {
    return t.Kind switch {
      TokenKind.Ident or TokenKind.Int or TokenKind.Real or TokenKind.String or TokenKind.Char => true,
      TokenKind.Keyword => t.Text is "return" or "true" or "false" or "null",
      TokenKind.Op => t.Text is ")" or "]" or "}" or "++" or "--" or "!",
      _ => false
    };
  }

  void SkipBlockComment() {
    int l = line;
    int c = col;
    Advance();
    Advance();
    bool sawNewline = false;
    while (!(Cur == '*' && At(1) == '/')) {
      if (pos >= src.Length) {
        throw Error("unterminated comment", l, c);
      }
      if (Cur == '\n') {
        sawNewline = true;
      }
      Advance();
    }
    Advance();
    Advance();
    if (sawNewline) {
      AddNewline();
    }
  }

  void LexNumber(int l, int c) {
    int start = pos;
    var kind = TokenKind.Int;
    if (Cur == '0' && (At(1) == 'x' || At(1) == 'X')) {
      Advance();
      Advance();
      if (!char.IsAsciiHexDigit(Cur)) {
        throw Error("invalid hex number", l, c);
      }
      while (char.IsAsciiHexDigit(Cur)) {
        Advance();
      }
    } else {
      while (char.IsDigit(Cur)) {
        Advance();
      }
      // "0..10" is a range, "1.5" is a real
      if (Cur == '.' && char.IsDigit(At(1))) {
        kind = TokenKind.Real;
        Advance();
        while (char.IsDigit(Cur)) {
          Advance();
        }
      }
      bool exp = Cur == 'e' || Cur == 'E';
      bool expDigits = char.IsDigit(At(1)) || ((At(1) == '+' || At(1) == '-') && char.IsDigit(At(2)));
      if (exp && expDigits) {
        kind = TokenKind.Real;
        Advance();
        if (Cur == '+' || Cur == '-') {
          Advance();
        }
        while (char.IsDigit(Cur)) {
          Advance();
        }
      }
    }
    if (char.IsLetter(Cur) || Cur == '_') {
      throw Error("invalid character in number", line, col);
    }
    Add(kind, src[start..pos], l, c);
  }

  void LexQuoted(char quote, TokenKind kind, int l, int c) {
    int start = pos;
    Advance();
    while (Cur != quote) {
      if (pos >= src.Length || Cur == '\n') {
        throw Error(kind == TokenKind.String ? "unterminated string" : "unterminated char", l, c);
      }
      if (Cur == '\\') {
        Advance();
      }
      Advance();
    }
    Advance();
    string text = src[start..pos];
    if (kind == TokenKind.Char && text.Length <= 2) {
      throw Error("empty char literal", l, c);
    }
    Add(kind, text, l, c);
  }

  void LexLabel(int l, int c) {
    Advance();
    int start = pos;
    if (!char.IsLetter(Cur)) {
      throw Error("labels are written #name#", l, c);
    }
    while (char.IsLetterOrDigit(Cur) || Cur == '_') {
      Advance();
    }
    string name = src[start..pos];
    if (Cur != '#') {
      throw Error("labels are written #name#", l, c);
    }
    Advance();
    Add(TokenKind.Label, name, l, c);
  }

  void LexOp(int l, int c) {
    foreach (var op in Ops) {
      if (string.CompareOrdinal(src, pos, op, 0, op.Length) == 0) {
        for (int i = 0; i < op.Length; i++) {
          Advance();
        }
        Add(TokenKind.Op, op, l, c);
        return;
      }
    }
    throw Error($"unexpected character '{Cur}'", l, c);
  }
}
