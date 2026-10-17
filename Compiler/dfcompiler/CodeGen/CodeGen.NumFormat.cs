using System.Text;
using dfparser;

namespace dfcompiler;

// Number formatting and parsing (spec 8.1, 8.2, 9) for the integers and the reals: x.toStr(format), {x:format} and T.parse.
// The work is done by the C runtime (RuntimeNumber.cs); this side checks the formats written in the code, which must be
// valid at compile time (spec 8), and chooses the C function for the type.
public sealed partial class CodeGen {
  static string[] NumNeed => new[] { "numfmt" };

  static bool IsNumberType(string type) => IsInt(type) || RealTypes.Contains(type);

  // x.toStr(format) and {x:format} on a number or a nullable number; null when the value is of another type
  Expr? NumberFormat(Expr value, string? literalSpec, string specCode, Node at) {
    string type = BaseOf(value.Type);
    if (!IsNumberType(type)) {
      return null;
    }
    if (literalSpec != null) {
      string? problem = NumberFormatProblem(literalSpec);
      if (problem != null) {
        throw Error($"invalid number format \"{literalSpec}\": {problem}", at);
      }
    }
    NeedChunk("numfmt");
    return new Expr(WithTemp(new Expr(specCode, "string"), spec => {
      string Format(Expr number) {
        string t = number.Type;
        if (RealTypes.Contains(t)) {
          return $"df_fmt_r64((double)({number.Code}), {(t == "r32" ? "true" : "false")}, {spec})";
        }
        return SignedTypes.Contains(t)
          ? $"df_fmt_i64((int64_t)({number.Code}), {Bits(t)}, {spec})"
          : $"df_fmt_u64((uint64_t)({number.Code}), {Bits(t)}, {spec})";
      }
      if (!IsNullable(value.Type)) {
        return Format(value);
      }
      return WithTemp(value, temp => $"({temp}.has ? {Format(new Expr(temp + ".val", type))} : df_fmt_null({spec}))");
    }), "string");
  }

  static string FromCodePoints(List<int> codePoints) {
    var text = new StringBuilder();
    foreach (int cp in codePoints) {
      text.Append(char.ConvertFromUtf32(cp));
    }
    return text.ToString();
  }

  // a format of a parse call written as a literal: "<prefix>{spec}{spec}<suffix>"
  void CheckParseFormat(string format, Node at) {
    var specs = new List<string>();
    int index = 0;
    bool placeholders = false;
    bool suffix = false;
    var current = new StringBuilder();
    while (index < format.Length) {
      char c = format[index];
      if (c == '\\' && index + 1 < format.Length && format[index + 1] is '{' or '}' or '\\') {
        index += 2;
        suffix |= placeholders;
        continue;
      }
      if (c == '{') {
        if (suffix) {
          throw Error($"invalid format \"{format}\": static text between placeholders", at);
        }
        placeholders = true;
        int close = format.IndexOf('}', index + 1);
        if (close < 0 || format.IndexOf('{', index + 1) is int open and >= 0 && open < close) {
          throw Error($"invalid format \"{format}\": a placeholder is not closed", at);
        }
        specs.Add(format[(index + 1)..close]);
        index = close + 1;
        continue;
      }
      if (c == '}') {
        throw Error($"invalid format \"{format}\": '}}' without '{{' (write \\}})", at);
      }
      suffix |= placeholders;
      index++;
    }
    if (specs.Count == 0) {
      throw Error($"invalid format \"{format}\": it needs a placeholder, as in \"0x{{bh}}\"", at);
    }
    foreach (string spec in specs) {
      string? problem = NumberFormatProblem(spec);
      if (problem != null) {
        throw Error($"invalid format \"{format}\": {problem}", at);
      }
    }
  }

  // the same grammar as df_nf_parse in the runtime: null when the spec is valid, otherwise the reason
  static string? NumberFormatProblem(string spec) {
    var runes = spec.EnumerateRunes().Select(rune => rune.Value).ToList();
    int i = 0;
    int Peek() => i < runes.Count ? runes[i] : -1;
    int Get() => i < runes.Count ? runes[i++] : -1;
    bool Letter(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    bool Digit(int c) => c is >= '0' and <= '9';
    bool Symbol(int c) => c >= 0 && !Letter(c);
    int Number() {
      int digits = 0;
      while (Digit(Peek())) {
        i++;
        digits++;
      }
      return digits;
    }
    bool sci = false;
    while (i < runes.Count) {
      int c = Get();
      switch (c) {
        case 'b' or 'B': {
          int n = Get();
          if (n is 'h' or 'H' or 'd' or 'D' or 'b' or 'B' or 'o' or 'O') {
            break;
          }
          if (n == '6' && Get() == '4') {
            break;
          }
          return "after 'b' write d, h, H, b, o or 64";
        }
        case 'd' or 'D': {
          int n = Peek();
          if (n is 'l' or 'L') {
            i++;
          } else if (Symbol(n) && !Digit(n)) {
            i++;
          }
          Number();
          n = Peek();
          if (n is 'u' or 'b' or 'c' or 'f' or 't' or 'U' or 'B' or 'C' or 'F' or 'T') {
            i++;
          }
          break;
        }
        case 's' or 'S': {
          int n = Peek();
          if (n is 't' or 'T') {
            i++;
            if (Get() is not ('-' or '+')) {
              return "St needs - or +";
            }
          } else if (n is '-' or '+') {
            i++;
          } else if (n == '(') {
            i++;
            if (Get() != ')') {
              return "S( needs )";
            }
          }
          break;
        }
        case 'w' or 'W':
          if (Number() == 0) {
            return "w needs a number";
          }
          break;
        case 'f' or 'F': {
          int n = Get();
          if (!Symbol(n)) {
            return n < 0 ? "f needs a fill character" : "a letter cannot be the fill character: it is an option";
          }
          break;
        }
        case 'a' or 'A': {
          int n = Get();
          if (n is not ('<' or '>' or '^' or '.')) {
            return "a needs <, >, ^ or .";
          }
          break;
        }
        case 'g' or 'G': {
          int n = Get();
          if (n is 'l' or 'L') {
            break;
          }
          if (!Symbol(n) || Digit(n)) {
            return "g needs a separator that is not a letter or a digit";
          }
          int size = Get();
          if (size is < '1' or > '9') {
            return "g needs a group size from 1 to 9 after the separator";
          }
          while (Peek() is >= '1' and <= '9') {
            i++;
            int separator = Get();
            if (!Symbol(separator) || Digit(separator)) {
              return "a group size needs a separator that is not a letter or a digit";
            }
          }
          if (Peek() is 'r' or 'R') {
            i++;
          }
          break;
        }
        case 'e' or 'E': {
          sci = true;
          if (Peek() == '[') {
            i++;
            while (true) {
              int n = Get();
              if (n < 0) {
                return "the text of e[...] is not closed";
              }
              if (n == ']') {
                break;
              }
              if (n == '\\' && Get() < 0) {
                return "the text of e[...] is not closed";
              }
            }
          }
          if (Peek() is '0' or '#') {
            i++;
            if (Number() == 0) {
              return "the exponent padding needs a number, as in e02";
            }
          }
          break;
        }
        default:
          return Letter(c) ? $"unknown option '{(char)c}'" : $"unexpected character '{char.ConvertFromUtf32(c)}'";
      }
    }
    _ = sci;
    return null;
  }

  // the standard library part: T.parse for the integers and the reals (spec 9)
  static void AddNumberStdlib(Action<string, StdFun> add) {
    foreach (string type in IntTypes.Concat(new[] { "r32", "r64" })) {
      string? silent = type[0] == 'r' ? null : type[0] == 'u' ? "OutOfRangeCastException,OverflowException" : "OutOfRangeCastException";
      string c = "df_parse_" + type;
      add($"{type}.parse", Fun(type, c, new[] { "string" }, "0", NumNeed, silent) with { Names = new[] { "text" } });
      add($"{type}.parse", Fun(type, c, new[] { "string", type }, "", NumNeed, silent) with { Names = new[] { "text", "default" } });
      add($"{type}.parse", Fun(type, c + "_f", new[] { "string", type, "string" }, "", NumNeed, silent) with { Names = new[] { "text", "default", "format" } });
      add($"{type}.parse", Fun(type, c + "_fmt", new[] { "string", "string" }, "", NumNeed, silent) with { Names = new[] { "text", "format" } });
    }
  }
}
