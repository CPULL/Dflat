namespace dfparser;

public enum TokenKind {
  Ident,
  Keyword,
  Int,
  Real,
  String,
  InterpString,
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
    return $"{Line,4}:{Col,-4} {Kind,-12} {shown}";
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
    "fun", "class", "trait", "enum", "alias", "namespace", "import",
    "public", "internal", "private", "const", "override", "dyn", "impl",
    "if", "then", "else", "while", "for", "in", "step", "switch", "default",
    "jump", "break", "continue", "return", "throw", "catch", "atEnd", "enforce",
    "is", "true", "false", "null"
  };

  // Longest first. ">>" and ">>=" are not tokens: the parser joins adjacent '>'
  // so that generics like list<list<i32>> close correctly.
  static readonly string[] Ops = {
    "..=", "...", "??=", "<<=",
    "->", "_=", "==", "!=", "<=", ">=", "&&", "||", "^^", "++", "--",
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
      if (c == '$' && At(1) == '"') {
        LexInterpolated(l, cl);
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

  // JavaScript-style: a line break ends a statement only after a token that can end one.
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
      TokenKind.Ident or TokenKind.Int or TokenKind.Real or TokenKind.String
        or TokenKind.InterpString or TokenKind.Char or TokenKind.Label => true,
      TokenKind.Keyword => t.Text is "return" or "break" or "continue" or "default"
        or "true" or "false" or "null",
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

  // ---------------------------------------------------------------- numbers

  static bool IsBinDigit(char c) => c == '0' || c == '1';

  // digits with '_' separators: '_' must sit between two digits
  void ReadDigits(Func<char, bool> isDigit) {
    while (true) {
      if (isDigit(Cur)) {
        Advance();
      } else if (Cur == '_' && isDigit(At(1)) && pos > 0 && isDigit(src[pos - 1])) {
        Advance();
      } else {
        return;
      }
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
      ReadDigits(char.IsAsciiHexDigit);
      ReadSuffix("u", l, c);
      Add(kind, src[start..pos], l, c);
      return;
    }

    // "0b101" is binary; "0b" alone is zero with the byte suffix
    if (Cur == '0' && (At(1) == 'b' || At(1) == 'B') && IsBinDigit(At(2))) {
      Advance();
      Advance();
      ReadDigits(IsBinDigit);
      ReadSuffix("u", l, c);
      Add(kind, src[start..pos], l, c);
      return;
    }

    ReadDigits(char.IsDigit);
    // "0..10" is a range, "1.5" is a real
    if (Cur == '.' && char.IsDigit(At(1))) {
      kind = TokenKind.Real;
      Advance();
      ReadDigits(char.IsDigit);
    }
    bool exp = Cur == 'e' || Cur == 'E';
    bool expDigits = char.IsDigit(At(1)) || ((At(1) == '+' || At(1) == '-') && char.IsDigit(At(2)));
    if (exp && expDigits) {
      kind = TokenKind.Real;
      Advance();
      if (Cur == '+' || Cur == '-') {
        Advance();
      }
      ReadDigits(char.IsDigit);
    }

    if (kind == TokenKind.Real) {
      ReadSuffix("f", l, c);
    } else {
      // f on an integer literal makes it a real: 2f
      if ((Cur == 'f' || Cur == 'F') && !char.IsLetterOrDigit(At(1))) {
        Advance();
        kind = TokenKind.Real;
      } else {
        ReadSuffix("ulbs", l, c);
      }
    }
    Add(kind, src[start..pos], l, c);
  }

  // Suffix letters (case-insensitive), each at most once, at most two.
  void ReadSuffix(string allowed, int l, int c) {
    var seen = new HashSet<char>();
    while (char.IsLetter(Cur)) {
      char s = char.ToLowerInvariant(Cur);
      if (!allowed.Contains(s) || seen.Contains(s) || seen.Count == 2) {
        throw Error($"invalid number suffix '{Cur}'", line, col);
      }
      seen.Add(s);
      Advance();
    }
    if (Cur == '_' || char.IsDigit(Cur)) {
      throw Error("invalid character in number", line, col);
    }
    // l (64), s (16) and b (8) are sizes: at most one of them
    int sizes = (seen.Contains('l') ? 1 : 0) + (seen.Contains('s') ? 1 : 0) + (seen.Contains('b') ? 1 : 0);
    if (sizes > 1) {
      throw Error("conflicting number suffixes", l, c);
    }
  }

  // ---------------------------------------------------------------- strings

  // Validates one escape starting at '\'.
  void ReadEscape(int l, int c, bool interpolated) {
    Advance(); // backslash
    char e = Cur;
    switch (e) {
      case 'n':
      case 'r':
      case 't':
      case '\\':
      case '\'':
      case '"':
      case '0':
        Advance();
        return;
      case '{':
      case '}':
        if (!interpolated) {
          throw Error($"'\\{e}' is only valid in interpolated strings", line, col);
        }
        Advance();
        return;
      case 'u':
        Advance();
        ReadCodePoint(16, l, c);
        return;
      case '#':
        Advance();
        ReadCodePoint(10, l, c);
        return;
      default:
        throw Error($"unknown escape '\\{e}'", line, col);
    }
  }

  void ReadCodePoint(int radix, int l, int c) {
    if (Cur != '{') {
      throw Error(radix == 16 ? "code point escape is \\u{hex}" : "code point escape is \\#{decimal}", line, col);
    }
    Advance();
    int start = pos;
    while (radix == 16 ? char.IsAsciiHexDigit(Cur) : char.IsDigit(Cur)) {
      Advance();
    }
    string digits = src[start..pos];
    if (Cur != '}' || digits.Length == 0) {
      throw Error("malformed code point escape", line, col);
    }
    Advance();
    long value;
    try {
      value = Convert.ToInt64(digits, radix);
    } catch (Exception) {
      throw Error("code point out of range", l, c);
    }
    if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF)) {
      throw Error("invalid Unicode code point", l, c);
    }
  }

  void LexQuoted(char quote, TokenKind kind, int l, int c) {
    int start = pos;
    Advance();
    int chars = 0;
    while (Cur != quote) {
      if (pos >= src.Length || Cur == '\n') {
        throw Error(kind == TokenKind.String ? "unterminated string" : "unterminated char", l, c);
      }
      if (Cur == '\\') {
        ReadEscape(l, c, false);
      } else {
        Advance();
      }
      chars++;
    }
    Advance();
    if (kind == TokenKind.Char && chars != 1) {
      throw Error(chars == 0 ? "empty char literal" : "char literal holds one character", l, c);
    }
    Add(kind, src[start..pos], l, c);
  }

  // $"text {expr:spec} text": braces nest, and quotes inside braces are nested strings
  void LexInterpolated(int l, int c) {
    int start = pos;
    Advance(); // $
    Advance(); // "
    int depth = 0;
    while (true) {
      if (pos >= src.Length || Cur == '\n') {
        throw Error("unterminated interpolated string", l, c);
      }
      char ch = Cur;
      if (ch == '\\') {
        ReadEscape(l, c, true);
        continue;
      }
      if (depth == 0 && ch == '"') {
        Advance();
        break;
      }
      if (depth > 0 && (ch == '"' || ch == '\'')) {
        SkipNested(ch, l, c);
        continue;
      }
      if (ch == '{') {
        depth++;
      } else if (ch == '}') {
        if (depth == 0) {
          throw Error("unmatched '}' in interpolated string (use \\})", line, col);
        }
        depth--;
      }
      Advance();
    }
    if (depth != 0) {
      throw Error("unclosed '{' in interpolated string", l, c);
    }
    Add(TokenKind.InterpString, src[start..pos], l, c);
  }

  void SkipNested(char quote, int l, int c) {
    Advance();
    while (Cur != quote) {
      if (pos >= src.Length || Cur == '\n') {
        throw Error("unterminated string inside interpolation", l, c);
      }
      if (Cur == '\\') {
        Advance();
      }
      Advance();
    }
    Advance();
  }

  // ---------------------------------------------------------------- labels and operators

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
