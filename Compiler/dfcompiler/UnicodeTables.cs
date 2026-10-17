using System.Globalization;
using System.Text;

namespace dfcompiler;

// Unicode data for the generated C program (spec 4.1). The tables come from the .NET Unicode database
// at compile time, so the program needs no libc locale support; only the requested tables are emitted.
public static class UnicodeTables {
  // predicate name (as used in the C function df_cp_is_<name>) → test on a code point
  static readonly Dictionary<string, Func<int, bool>> Predicates = new() {
    ["letter"] = cp => IsCategory(cp, UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter,
      UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter),
    ["digit"] = cp => IsCategory(cp, UnicodeCategory.DecimalDigitNumber),
    ["upper"] = cp => IsCategory(cp, UnicodeCategory.UppercaseLetter),
    ["lower"] = cp => IsCategory(cp, UnicodeCategory.LowercaseLetter),
    ["punct"] = cp => IsCategory(cp, UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation,
      UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation,
      UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation),
    ["symbol"] = cp => IsCategory(cp, UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol,
      UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol),
    ["control"] = cp => IsCategory(cp, UnicodeCategory.Control),
    ["space"] = cp => IsRune(cp) && Rune.IsWhiteSpace(new Rune(cp)),
    // Trim: spaces and everything that does not print
    ["trim"] = cp => (IsRune(cp) && Rune.IsWhiteSpace(new Rune(cp)))
      || IsCategory(cp, UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.LineSeparator,
        UnicodeCategory.ParagraphSeparator)
  };

  static bool IsRune(int cp) => cp is >= 0 and <= 0x10FFFF && !(cp is >= 0xD800 and <= 0xDFFF);

  static bool IsCategory(int cp, params UnicodeCategory[] categories) {
    return IsRune(cp) && Array.IndexOf(categories, CharUnicodeInfo.GetUnicodeCategory(cp)) >= 0;
  }

  public static bool IsPredicate(string name) => Predicates.ContainsKey(name);

  // sorted ranges [lo, hi] where the predicate is true
  static string Ranges(string name, Func<int, bool> test) {
    var sb = new StringBuilder();
    int count = 0;
    int start = -1;
    for (int cp = 0; cp <= 0x110000; cp++) {
      bool on = cp <= 0x10FFFF && test(cp);
      if (on && start < 0) {
        start = cp;
      } else if (!on && start >= 0) {
        sb.Append($"0x{start:X},0x{cp - 1:X},");
        count++;
        start = -1;
        if (count % 8 == 0) {
          sb.Append('\n');
        }
      }
    }
    return $"static const uint32_t df_u_{name}[] = {{\n{sb}0}};\n"
      + $"static bool df_cp_is_{name}(uint32_t cp) {{ return df_in_ranges(df_u_{name}, {count}, cp); }}\n\n";
  }

  // case mapping as runs: first, last, delta, step (1 = every code point, 2 = every other one)
  static string CaseMap(string name, Func<Rune, Rune> map) {
    var runs = new List<(int Lo, int Hi, int Delta, int Step)>();
    for (int cp = 0; cp <= 0x10FFFF; cp++) {
      if (!IsRune(cp)) {
        continue;
      }
      int mapped = map(new Rune(cp)).Value;
      if (mapped == cp) {
        continue;
      }
      int delta = mapped - cp;
      if (runs.Count > 0) {
        var last = runs[^1];
        int gap = cp - last.Hi;
        if (last.Delta == delta && (last.Lo == last.Hi ? gap is 1 or 2 : gap == last.Step)) {
          runs[^1] = (last.Lo, cp, delta, last.Lo == last.Hi ? gap : last.Step);
          continue;
        }
      }
      runs.Add((cp, cp, delta, 1));
    }
    var sb = new StringBuilder();
    for (int index = 0; index < runs.Count; index++) {
      var (lo, hi, delta, step) = runs[index];
      sb.Append($"{{0x{lo:X},0x{hi:X},{delta},{step}}},");
      if ((index + 1) % 6 == 0) {
        sb.Append('\n');
      }
    }
    return $"static const df_case_run df_u_{name}[] = {{\n{sb}{{0,0,0,1}}}};\n"
      + $"static uint32_t df_cp_{name}(uint32_t cp) {{ return df_map_case(df_u_{name}, {runs.Count}, cp); }}\n\n";
  }

  // the C source for the requested tables ("letter", "digit", ..., "toupper", "tolower")
  public static string Generate(IEnumerable<string> wanted) {
    var sb = new StringBuilder();
    sb.Append(Helpers);
    var names = new HashSet<string>(wanted);
    if (names.Contains("letterordigit")) {
      names.Add("letter");
      names.Add("digit");
    }
    foreach (string name in names.OrderBy(item => item, StringComparer.Ordinal)) {
      if (name is "hexdigit" or "ascii" or "letterordigit") {
        continue;
      }
      if (name == "toupper") {
        sb.Append(CaseMap("toupper", Rune.ToUpperInvariant));
      } else if (name == "tolower") {
        sb.Append(CaseMap("tolower", Rune.ToLowerInvariant));
      } else {
        sb.Append(Ranges(name, Predicates[name]));
      }
    }
    if (names.Contains("letterordigit")) {
      sb.Append("static bool df_cp_is_letterordigit(uint32_t cp) { return df_cp_is_letter(cp) || df_cp_is_digit(cp); }\n\n");
    }
    return sb.ToString();
  }

  const string Helpers = """
/* ---- Unicode tables ---- */

typedef struct { uint32_t lo, hi; int32_t delta; int32_t step; } df_case_run;

static bool df_in_ranges(const uint32_t* table, int count, uint32_t cp) {
  int low = 0;
  int high = count - 1;
  while (low <= high) {
    int mid = (low + high) / 2;
    if (cp < table[mid * 2]) {
      high = mid - 1;
    } else if (cp > table[mid * 2 + 1]) {
      low = mid + 1;
    } else {
      return true;
    }
  }
  return false;
}

static bool df_cp_is_hexdigit(uint32_t cp) {
  return (cp >= '0' && cp <= '9') || (cp >= 'a' && cp <= 'f') || (cp >= 'A' && cp <= 'F');
}

static bool df_cp_is_ascii(uint32_t cp) {
  return cp < 128;
}

static uint32_t df_map_case(const df_case_run* table, int count, uint32_t cp) {
  int low = 0;
  int high = count - 1;
  while (low <= high) {
    int mid = (low + high) / 2;
    if (cp < table[mid].lo) {
      high = mid - 1;
    } else if (cp > table[mid].hi) {
      low = mid + 1;
    } else {
      if ((cp - table[mid].lo) % (uint32_t)table[mid].step == 0) {
        return (uint32_t)((int32_t)cp + table[mid].delta);
      }
      return cp;
    }
  }
  return cp;
}


""";
}
