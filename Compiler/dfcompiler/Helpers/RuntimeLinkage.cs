using System.Text;

namespace dfcompiler;

// How the C runtime goes into a compiled unit (spec 15.8). The runtime source declares every top-level function and variable static:
// - Static: as written, a program alone (one unit, one runtime).
// - Exported: the program of a build that links compiled libraries defines the runtime with external linkage, so the libraries use it.
// - Imported: a compiled library declares the same runtime without defining it: functions become prototypes, variables extern
//   declarations; types, macros and static inline helpers stay as they are. There is one runtime state and one heap in the program.
// static inline functions and the static functions written by macros (#define ... static T f() ...) are copied in each unit: they are
// pure helpers, and any state they touch is a runtime variable, shared.
public enum RuntimeLinkageMode { Static, Exported, Imported }

public static class RuntimeLinkage {
  // the mode a build forces for its tests (DFLAT_RUNTIME_LINKAGE=exported); null when not set
  public static RuntimeLinkageMode? Forced =>
    Environment.GetEnvironmentVariable("DFLAT_RUNTIME_LINKAGE")?.ToLowerInvariant() switch {
      "exported" => RuntimeLinkageMode.Exported,
      "imported" => RuntimeLinkageMode.Imported,
      _ => null
    };

  // owns: only the definitions of these names change (the stdlib in the generated code); null: all of them (the runtime source)
  public static string Apply(string source, RuntimeLinkageMode mode, Func<string, bool>? owns = null) {
    if (mode == RuntimeLinkageMode.Static) {
      return source;
    }
    var lines = source.Split('\n');
    var output = new StringBuilder(source.Length);
    bool inMacro = false;
    int index = 0;
    while (index < lines.Length) {
      string line = lines[index];
      bool continues = line.TrimEnd('\r').EndsWith('\\');
      if (!inMacro && line.StartsWith("static ") && !line.StartsWith("static inline ")) {
        // a top-level definition: collect it up to its end (the closing brace of a body, or the ';' of a declaration)
        var text = new StringBuilder();
        int end = DefinitionEnd(lines, index, text);
        output.Append(owns == null || owns(DefinedName(text.ToString())) ? Rewrite(text.ToString(), mode) : text.ToString());
        index = end + 1;
        inMacro = false;
        continue;
      }
      output.Append(line);
      if (index < lines.Length - 1) {
        output.Append('\n');
      }
      inMacro = continues;
      index++;
    }
    return output.ToString();
  }

  // the last line of the definition that starts at line start; its text (with the line breaks) goes into text
  static int DefinitionEnd(string[] lines, int start, StringBuilder text) {
    int depth = 0;
    bool inBlockComment = false;
    for (int index = start; index < lines.Length; index++) {
      string line = lines[index];
      text.Append(line).Append('\n');
      for (int at = 0; at < line.Length; at++) {
        char c = line[at];
        if (inBlockComment) {
          if (c == '*' && at + 1 < line.Length && line[at + 1] == '/') {
            inBlockComment = false;
            at++;
          }
          continue;
        }
        if (c == '/' && at + 1 < line.Length && line[at + 1] == '*') {
          inBlockComment = true;
          at++;
          continue;
        }
        if (c == '/' && at + 1 < line.Length && line[at + 1] == '/') {
          break;
        }
        if (c is '"' or '\'') {
          at = SkipLiteral(line, at);
          continue;
        }
        if (c == '{') {
          depth++;
        } else if (c == '}') {
          depth--;
          if (depth == 0 && !HasInitializer(text.ToString())) {
            return index;   // the body of a function ends
          }
        } else if (c == ';' && depth == 0) {
          return index;     // a declaration, or a variable with its initializer
        }
      }
    }
    return lines.Length - 1;
  }

  static int SkipLiteral(string line, int at) {
    char quote = line[at];
    for (int index = at + 1; index < line.Length; index++) {
      if (line[index] == '\\') {
        index++;
      } else if (line[index] == quote) {
        return index;
      }
    }
    return line.Length;
  }

  // static T x = { ... };: an initializer in braces ends at its ';', not at its closing brace
  static bool HasInitializer(string text) {
    int brace = text.IndexOf('{');
    int equals = text.IndexOf('=');
    return equals >= 0 && (brace < 0 || equals < brace);
  }

  static string Rewrite(string definition, RuntimeLinkageMode mode) {
    string body = definition["static ".Length..];
    if (mode == RuntimeLinkageMode.Exported) {
      return body;
    }
    // Imported: the declaration only
    int brace = body.IndexOf('{');
    int equals = body.IndexOf('=');
    string head;
    if (brace >= 0 && (equals < 0 || brace < equals)) {
      head = body[..brace];                  // a function: its header
    } else if (equals >= 0) {
      head = body[..equals];                 // a variable: without its initializer
    } else {
      head = body.TrimEnd().TrimEnd(';');    // a prototype or a variable without initializer
    }
    return "extern " + StripComments(head).Trim() + ";\n";
  }

  // the name a definition defines: the identifier before the parameters of a function, or before the initializer / ';' of a variable
  static string DefinedName(string definition) {
    string head = StripComments(definition);
    int end = new[] { head.IndexOf('('), head.IndexOf('='), head.IndexOf('{'), head.IndexOf(';'), head.IndexOf('[') }
      .Where(at => at >= 0).DefaultIfEmpty(head.Length).Min();
    string before = head[..end].TrimEnd();
    int start = before.Length;
    while (start > 0 && (char.IsLetterOrDigit(before[start - 1]) || before[start - 1] == '_')) {
      start--;
    }
    return before[start..];
  }

  static string StripComments(string text) {
    var sb = new StringBuilder();
    for (int index = 0; index < text.Length; index++) {
      if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*') {
        int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        index = close < 0 ? text.Length : close + 1;
        continue;
      }
      if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/') {
        int newline = text.IndexOf('\n', index);
        index = newline < 0 ? text.Length : newline - 1;
        continue;
      }
      sb.Append(text[index]);
    }
    return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
  }
}
