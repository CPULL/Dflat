using dfparser;

namespace dfcompiler;

// String formats (spec 8.3): {text:spec} and text.toStr(spec). The work is done by the C runtime (RuntimeStringFormat.cs); this
// side checks the specs written in the code, which must be valid at compile time (spec 8).
public sealed partial class CodeGen {
  // x.toStr(format) and {x:format} on a string; null when the value is of another type
  Expr? StringFormat(Expr value, string? literalSpec, string specCode, Node at) {
    if (value.Type != "string") {
      return null;
    }
    if (literalSpec != null) {
      string? problem = StringFormatProblem(literalSpec);
      if (problem != null) {
        throw Error($"invalid string format \"{literalSpec}\": {problem}", at);
      }
    }
    NeedChunk("strfmt");
    return new Expr($"df_str_format({value.Code}, {specCode})", "string");
  }

  // the same grammar as df_sf_parse in the runtime: null when the spec is valid, otherwise the reason
  static string? StringFormatProblem(string spec) {
    var runes = spec.EnumerateRunes().Select(rune => rune.Value).ToList();
    int i = 0;
    int Peek() => i < runes.Count ? runes[i] : -1;
    int Get() => i < runes.Count ? runes[i++] : -1;
    bool Letter(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    bool Digit(int c) => c is >= '0' and <= '9';
    bool width = false;
    bool truncates = false;
    while (i < runes.Count) {
      int c = Get();
      switch (c) {
        case 'w' or 'W': {
          if (Peek() is 'c' or 'C' && i + 1 < runes.Count && Digit(runes[i + 1])) {
            i++;
          }
          int digits = 0;
          while (Digit(Peek())) {
            i++;
            digits++;
          }
          if (digits == 0) {
            return "w needs a number (wc for display columns)";
          }
          width = true;
          break;
        }
        case 'f' or 'F': {
          int n = Get();
          if (n < 0) {
            return "f needs a fill character";
          }
          if (Letter(n)) {
            return "a letter cannot be the fill character: it is an option";
          }
          break;
        }
        case 'a' or 'A':
          if (Get() is not ('<' or '^' or '>')) {
            return "a needs <, ^ or >";
          }
          break;
        case 't' or 'T': {
          if (Get() is not ('<' or '>')) {
            return "t needs < (keep the start) or > (keep the end)";
          }
          if (Get() != '\'') {
            return "t needs its marker between single quotes, as in t<'...'";
          }
          while (true) {
            int n = Get();
            if (n < 0) {
              return "the marker of t is not closed";
            }
            if (n == '\'') {
              break;
            }
            if (n == '\\' && Get() < 0) {
              return "the marker of t is not closed";
            }
          }
          truncates = true;
          break;
        }
        case 'c' or 'C':
          if (Get() is not ('U' or 'u' or 'L' or 'l' or 'S' or 's' or 'T' or 't')) {
            return "c needs U, L, S or T";
          }
          break;
        case 'q' or 'Q':
          break;
        case 's' or 'S':
          if (Peek() is 'l' or 'L' or 'r' or 'R') {
            i++;
          }
          break;
        default:
          return Letter(c) ? $"unknown option '{(char)c}'" : $"unexpected character '{char.ConvertFromUtf32(c)}'";
      }
    }
    return truncates && !width ? "t needs a width (w or wc)" : null;
  }
}
