using System.Text;
using System.Text.RegularExpressions;

namespace dfparser;

// The value of a preprocessor variable (spec 19.1): a number, a bool or a word (the name of an enum value, such as linux, or a text).
public sealed class PpValue {
  public enum Types { Number, Bool, Word }

  PpValue(Types type, long number, bool flag, string word) {
    Type = type;
    Number = number;
    Flag = flag;
    Word = word;
  }

  public Types Type { get; }
  public long Number { get; }
  public bool Flag { get; }
  public string Word { get; }

  public static PpValue OfNumber(long number) => new(Types.Number, number, false, "");
  public static PpValue OfBool(bool flag) => new(Types.Bool, 0, flag, "");
  public static PpValue OfWord(string word) => new(Types.Word, 0, false, word);

  public bool SameAs(PpValue other) {
    return Type == other.Type && Type switch {
      Types.Number => Number == other.Number,
      Types.Bool => Flag == other.Flag,
      _ => Word == other.Word
    };
  }

  public override string ToString() => Type switch {
    Types.Number => Number.ToString(),
    Types.Bool => Flag ? "true" : "false",
    _ => Word
  };
}

// A variable known to the preprocessor. Words: the values it can take when it is a word of a fixed list (os, cpu...), or null.
public sealed record PpVariable(PpValue Value, IReadOnlyCollection<string>? Words = null);

// The preprocessor (spec 19.1): it runs before the lexer and builds the code that is compiled. Every line it removes, and every directive,
// becomes an empty line, so the line numbers do not change. It is not part of the language.
public sealed class Preprocessor {
  sealed class Block {
    public bool ParentActive;
    public bool Taken;
    public bool Active;
    public bool SeenElse;
    public int Line;
  }

  enum TokenType { Word, Text, Open, Close, And, Or, Not, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, End }

  readonly record struct Token(TokenType Type, string Text);

  static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
  static readonly Regex NumberPattern = new(@"^([0-9]+)(kb|mb|gb)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

  readonly Dictionary<string, PpVariable> predefined;
  readonly HashSet<string> locked;                     // defined by the project file: they cannot be redefined
  readonly Dictionary<string, PpValue> defined = new();
  readonly Stack<Block> blocks = new();
  readonly Stack<int> regions = new();
  int currentLine;

  // predefined: the variables of the compiler (they cannot be redefined); fromProject: the variables of the project file (CompilerVar)
  public Preprocessor(IReadOnlyDictionary<string, PpVariable> predefinedVariables, IReadOnlyDictionary<string, PpValue>? fromProject = null) {
    predefined = new Dictionary<string, PpVariable>(predefinedVariables);
    locked = new HashSet<string>();
    if (fromProject != null) {
      foreach (var (name, value) in fromProject) {
        CheckName(name, 0);
        defined[name] = value;
        locked.Add(name);
      }
    }
  }

  // "(line): warning L3: message"
  public List<string> Warnings { get; } = new();

  bool Active => blocks.Count == 0 || blocks.Peek().Active;

  static DfException Error(string message, int line) => new(message, line, 1);

  // the name of a variable: not a predefined one, not a value of a predefined one, not true / false
  void CheckName(string name, int line) {
    if (!NamePattern.IsMatch(name)) {
      throw Error($"'{name}' is not a valid variable name", line);
    }
    if (predefined.ContainsKey(name)) {
      throw Error($"the variable {name} is predefined and cannot be redefined", line);
    }
    if (name is "true" or "false") {
      throw Error($"the variable {name} cannot be used: it is a bool value", line);
    }
    foreach (var (variable, info) in predefined) {
      if (info.Words != null && info.Words.Contains(name)) {
        throw Error($"the variable {name} cannot be used: it matches one value of {variable}", line);
      }
    }
    if (locked.Contains(name)) {
      throw Error($"the variable {name} cannot be redefined: it is defined in the project file", line);
    }
  }

  public string Process(string source) {
    var lines = source.Split('\n');
    var output = new StringBuilder(source.Length);
    for (int index = 0; index < lines.Length; index++) {
      currentLine = index + 1;
      string raw = lines[index];
      bool carriage = raw.EndsWith('\r');
      string text = carriage ? raw[..^1] : raw;
      string result;
      if (text.StartsWith("##")) {
        Directive(text);
        result = "";                                  // a directive is an empty line
      } else {
        result = Active ? text : "";                  // a removed line is an empty line
      }
      output.Append(result);
      if (carriage) {
        output.Append('\r');
      }
      if (index < lines.Length - 1) {
        output.Append('\n');
      }
    }
    if (blocks.Count > 0) {
      throw Error("##if without ##endif", blocks.Peek().Line);
    }
    foreach (int regionLine in regions.Reverse()) {
      Warnings.Add($"({regionLine}): warning L3: ##region without ##endregion");
    }
    return output.ToString();
  }

  // ---------------------------------------------------------------- directives

  void Directive(string text) {
    int end = 2;
    while (end < text.Length && char.IsLetter(text[end])) {
      end++;
    }
    string keyword = text[2..end];
    string rest = text[end..];
    if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) {
      throw Error($"unknown directive ##{keyword}{rest.Split(' ')[0]}", currentLine);
    }
    rest = rest.Trim();
    if (rest.Contains('#')) {
      throw Error("'#' is not allowed in directive text", currentLine);
    }
    switch (keyword) {
      case "define":
        if (Active) {
          Define(rest);
        }
        break;
      case "if": {
        bool parent = Active;
        bool condition = parent && Condition(rest);
        blocks.Push(new Block { ParentActive = parent, Taken = condition, Active = parent && condition, Line = currentLine });
        break;
      }
      case "elseif": {
        var block = BlockFor("##elseif");
        if (block.SeenElse) {
          throw Error("##elseif after ##else", currentLine);
        }
        if (!block.ParentActive || block.Taken) {
          block.Active = false;
        } else {
          bool condition = Condition(rest);
          block.Active = condition;
          block.Taken = condition;
        }
        break;
      }
      case "else": {
        var block = BlockFor("##else");
        if (block.SeenElse) {
          throw Error("##else twice in the same ##if", currentLine);
        }
        block.SeenElse = true;
        block.Active = block.ParentActive && !block.Taken;
        block.Taken = true;
        break;
      }
      case "endif":
        BlockFor("##endif");
        blocks.Pop();
        break;
      case "region":
        regions.Push(currentLine);
        break;
      case "endregion":
        if (regions.Count == 0) {
          Warnings.Add($"({currentLine}): warning L3: ##endregion without ##region");
        } else {
          regions.Pop();
        }
        break;
      case "emiterror":
        if (Active) {
          throw Error(Unquote(rest), currentLine);
        }
        break;
      case "emitwarning":
        if (Active) {
          Warnings.Add($"({currentLine}): warning: {Unquote(rest)}");
        }
        break;
      default:
        throw Error($"unknown directive ##{keyword}", currentLine);
    }
  }

  Block BlockFor(string directive) {
    if (blocks.Count == 0) {
      throw Error($"{directive} without ##if", currentLine);
    }
    return blocks.Peek();
  }

  static string Unquote(string text) {
    return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
  }

  // ##define name = value   (a value, or a condition that gives a bool)
  void Define(string text) {
    int equal = text.IndexOf('=');
    if (equal < 0) {
      throw Error("##define needs a variable and a value: ##define name = value", currentLine);
    }
    string name = text[..equal].Trim();
    CheckName(name, currentLine);
    var tokens = Tokenize(text[(equal + 1)..]);
    if (tokens.Count == 2 && tokens[0].Type is TokenType.Word or TokenType.Text) {
      defined[name] = Operand(tokens[0], null, out _, true);   // a lone value: a number, a bool, a word or the value of a variable
      return;
    }
    var cursor = new Cursor(tokens);
    var value = Expression(cursor);
    cursor.ExpectEnd(this);
    defined[name] = value;
  }

  // ---------------------------------------------------------------- conditions

  bool Condition(string text) {
    var cursor = new Cursor(Tokenize(text));
    var value = Expression(cursor);
    cursor.ExpectEnd(this);
    if (value.Type != PpValue.Types.Bool) {
      throw Error("a condition must be a bool: compare the variable with a value, or use a bool variable", currentLine);
    }
    return value.Flag;
  }

  sealed class Cursor {
    readonly List<Token> tokens;
    int position;

    public Cursor(List<Token> list) {
      tokens = list;
    }

    public Token Peek => tokens[position];
    public Token Next() => tokens[position++];

    public void ExpectEnd(Preprocessor owner) {
      if (Peek.Type != TokenType.End) {
        throw Error($"unexpected '{Peek.Text}' in the condition", owner.currentLine);
      }
    }
  }

  List<Token> Tokenize(string text) {
    var tokens = new List<Token>();
    int i = 0;
    while (i < text.Length) {
      char c = text[i];
      if (char.IsWhiteSpace(c)) {
        i++;
      } else if (c == '(') {
        tokens.Add(new Token(TokenType.Open, "("));
        i++;
      } else if (c == ')') {
        tokens.Add(new Token(TokenType.Close, ")"));
        i++;
      } else if (c == '"') {
        int close = text.IndexOf('"', i + 1);
        if (close < 0) {
          throw Error("a text between quotes is not closed", currentLine);
        }
        tokens.Add(new Token(TokenType.Text, text[(i + 1)..close]));
        i = close + 1;
      } else if (Two(text, i, "&&")) {
        tokens.Add(new Token(TokenType.And, "&&"));
        i += 2;
      } else if (Two(text, i, "||")) {
        tokens.Add(new Token(TokenType.Or, "||"));
        i += 2;
      } else if (Two(text, i, "==")) {
        tokens.Add(new Token(TokenType.Equal, "=="));
        i += 2;
      } else if (Two(text, i, "!=")) {
        tokens.Add(new Token(TokenType.NotEqual, "!="));
        i += 2;
      } else if (Two(text, i, "<=")) {
        tokens.Add(new Token(TokenType.LessEqual, "<="));
        i += 2;
      } else if (Two(text, i, ">=")) {
        tokens.Add(new Token(TokenType.GreaterEqual, ">="));
        i += 2;
      } else if (c == '<') {
        tokens.Add(new Token(TokenType.Less, "<"));
        i++;
      } else if (c == '>') {
        tokens.Add(new Token(TokenType.Greater, ">"));
        i++;
      } else if (c == '!') {
        tokens.Add(new Token(TokenType.Not, "!"));
        i++;
      } else if (char.IsLetterOrDigit(c) || c is '_' or '.') {
        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) {
          i++;
        }
        tokens.Add(new Token(TokenType.Word, text[start..i]));
      } else {
        throw Error($"unexpected character '{c}' in the condition", currentLine);
      }
    }
    tokens.Add(new Token(TokenType.End, ""));
    return tokens;
  }

  static bool Two(string text, int i, string pair) => i + 1 < text.Length && text[i] == pair[0] && text[i + 1] == pair[1];

  // comparisons joined by && and ||: the same precedence, from left to right (spec 19.1)
  PpValue Expression(Cursor cursor) {
    var left = Comparison(cursor);
    while (cursor.Peek.Type is TokenType.And or TokenType.Or) {
      var op = cursor.Next();
      var right = Comparison(cursor);
      if (left.Type != PpValue.Types.Bool || right.Type != PpValue.Types.Bool) {
        throw Error($"'{op.Text}' joins bool values", currentLine);
      }
      left = PpValue.OfBool(op.Type == TokenType.And ? left.Flag && right.Flag : left.Flag || right.Flag);
    }
    return left;
  }

  PpValue Comparison(Cursor cursor) {
    if (cursor.Peek.Type == TokenType.Open) {
      cursor.Next();
      var inner = Expression(cursor);
      if (cursor.Peek.Type != TokenType.Close) {
        throw Error("')' expected in the condition", currentLine);
      }
      cursor.Next();
      return inner;
    }
    if (cursor.Peek.Type == TokenType.Not) {
      cursor.Next();
      var token = cursor.Next();
      var value = token.Type == TokenType.Word ? Operand(token, null, out _, false) : throw Error("'!' goes before a bool variable", currentLine);
      if (value.Type != PpValue.Types.Bool) {
        throw Error($"'!' works only on a bool variable: for '{token.Text}' use !=", currentLine);
      }
      return PpValue.OfBool(!value.Flag);
    }
    var first = cursor.Next();
    if (first.Type is not (TokenType.Word or TokenType.Text)) {
      throw Error($"unexpected '{first.Text}' in the condition", currentLine);
    }
    var left = Operand(first, null, out var variable, false);
    var next = cursor.Peek.Type;
    if (next is not (TokenType.Equal or TokenType.NotEqual or TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual)) {
      return left;
    }
    var op = cursor.Next();
    var second = cursor.Next();
    if (second.Type is not (TokenType.Word or TokenType.Text)) {
      throw Error($"a value expected after '{op.Text}'", currentLine);
    }
    var right = Operand(second, variable, out _, true);
    if (op.Type is TokenType.Equal or TokenType.NotEqual) {
      bool same = left.SameAs(right);
      return PpValue.OfBool(op.Type == TokenType.Equal ? same : !same);
    }
    if (left.Type != PpValue.Types.Number || right.Type != PpValue.Types.Number) {
      throw Error($"'{op.Text}' works only on numbers: strings and enum values have no order", currentLine);
    }
    return PpValue.OfBool(op.Type switch {
      TokenType.Less => left.Number < right.Number,
      TokenType.LessEqual => left.Number <= right.Number,
      TokenType.Greater => left.Number > right.Number,
      _ => left.Number >= right.Number
    });
  }

  PpVariable? Find(string name, out PpValue value) {
    if (predefined.TryGetValue(name, out var info)) {
      value = info.Value;
      return info;
    }
    if (defined.TryGetValue(name, out var user)) {
      value = user;
      return new PpVariable(user);
    }
    value = PpValue.OfBool(false);
    return null;
  }

  // a word or a text: the value of a variable, a number, true / false, or (when a word is allowed) the word itself.
  // against: the variable on the left of the comparison, to check the word against its list of values
  PpValue Operand(Token token, PpVariable? against, out PpVariable? variable, bool wordAllowed) {
    variable = null;
    if (token.Type == TokenType.Text) {
      return PpValue.OfWord(token.Text);
    }
    string word = token.Text;
    var info = Find(word, out var value);
    if (info != null) {
      variable = info;
      return value;
    }
    if (word is "true" or "false") {
      return PpValue.OfBool(word == "true");
    }
    var number = NumberPattern.Match(word);
    if (number.Success) {
      long factor = number.Groups[2].Value.ToLowerInvariant() switch { "kb" => 1024L, "mb" => 1024L * 1024, "gb" => 1024L * 1024 * 1024, _ => 1L };
      if (!long.TryParse(number.Groups[1].Value, out long amount) || amount > long.MaxValue / factor) {
        throw Error($"'{word}' is too big", currentLine);
      }
      return PpValue.OfNumber(amount * factor);
    }
    if (!wordAllowed) {
      throw Error($"unknown variable '{word}'", currentLine);
    }
    if (against?.Words != null && !against.Words.Contains(word)) {
      throw Error($"'{word}' is not a value of that variable (its values: {string.Join(", ", against.Words)})", currentLine);
    }
    return PpValue.OfWord(word);
  }
}
