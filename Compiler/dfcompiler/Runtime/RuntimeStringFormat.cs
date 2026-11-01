namespace dfcompiler;

// C runtime for the string formats of spec 8.3: {text:spec} and text.toStr(spec). Added to a program only when it uses one.
// It builds on the text part of the runtime (trim, case, UTF-8 helpers).
public static class RuntimeStringFormat {
  public const string Source = """
/* ---- string formats (spec 8.3): {text:spec} and text.toStr(spec) ---- */

/* display columns of a code point: 0 for combining marks and zero-width characters, 2 for the wide East Asian and emoji ranges */
static int df_cp_columns(uint32_t c) {
  if (c == 0 || (c >= 0x300 && c <= 0x36F) || (c >= 0x200B && c <= 0x200F) || (c >= 0x20D0 && c <= 0x20FF)
      || (c >= 0xFE00 && c <= 0xFE0F) || (c >= 0xFE20 && c <= 0xFE2F) || (c >= 0x1AB0 && c <= 0x1AFF) || (c >= 0x1DC0 && c <= 0x1DFF)) {
    return 0;
  }
  if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF) || (c >= 0x3400 && c <= 0x4DBF)
      || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF)
      || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6) || (c >= 0x1F300 && c <= 0x1F64F)
      || (c >= 0x1F900 && c <= 0x1F9FF) || (c >= 0x20000 && c <= 0x3FFFD)) {
    return 2;
  }
  return 1;
}

typedef struct {
  bool hasWidth, columns;
  uint64_t width;
  uint32_t fill;
  int align;          /* 0 left (default), 1 center, 2 right */
  int trunc;          /* 0 none, 1 keep the start, 2 keep the end */
  df_string marker;
  int casing;         /* 0 none, 'U', 'L', 'S' sentence, 'T' title */
  bool quote;
  int trim;           /* 0 none, 1 left, 2 right, 3 both */
} df_sf;

static bool df_sf_letter(uint32_t c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

/* the grammar of 8.3; false for a spec that is not valid (the text is then left as it is) */
static bool df_sf_parse(df_string specText, df_sf* f) {
  df_sv spec = df_view(specText);
  memset(f, 0, sizeof *f);
  f->fill = ' ';
  uint64_t i = 0;
  while (i < spec.len) {
    uint32_t c = df_utf8_next(spec.p, spec.len, &i);
    uint32_t next = i < spec.len ? (uint32_t)(unsigned char)spec.p[i] : 0;
    switch (c | 32) {
      case 'w': {
        if ((next | 32) == 'c' && i + 1 < spec.len && spec.p[i + 1] >= '0' && spec.p[i + 1] <= '9') {
          f->columns = true;
          i++;
        }
        uint64_t digits = 0;
        uint64_t value = 0;
        while (i < spec.len && spec.p[i] >= '0' && spec.p[i] <= '9') {
          value = value * 10 + (uint64_t)(spec.p[i] - '0');
          i++;
          digits++;
        }
        if (digits == 0) return false;
        f->hasWidth = true;
        f->width = value;
        break;
      }
      case 'f': {
        if (i >= spec.len) return false;
        uint32_t fill = df_utf8_next(spec.p, spec.len, &i);
        if (df_sf_letter(fill)) return false;
        f->fill = fill;
        break;
      }
      case 'a': {
        if (i >= spec.len) return false;
        uint32_t mode = df_utf8_next(spec.p, spec.len, &i);
        if (mode == '<') f->align = 0; else if (mode == '^') f->align = 1; else if (mode == '>') f->align = 2; else return false;
        break;
      }
      case 't': {
        if (i >= spec.len) return false;
        uint32_t side = df_utf8_next(spec.p, spec.len, &i);
        if (side != '<' && side != '>') return false;
        if (i >= spec.len || spec.p[i] != '\'') return false;
        i++;
        uint64_t start = i;
        while (i < spec.len && spec.p[i] != '\'') {
          if (spec.p[i] == '\\' && i + 1 < spec.len) i++;
          i++;
        }
        if (i >= spec.len) return false;
        f->marker = df_str_slice(spec, start, i);
        i++;
        f->trunc = side == '<' ? 1 : 2;
        break;
      }
      case 'c': {
        if (i >= spec.len) return false;
        uint32_t mode = df_utf8_next(spec.p, spec.len, &i) | 32;
        if (mode == 'u') f->casing = 'U'; else if (mode == 'l') f->casing = 'L'; else if (mode == 's') f->casing = 'S'; else if (mode == 't') f->casing = 'T'; else return false;
        break;
      }
      case 'q':
        f->quote = true;
        break;
      case 's': {
        uint32_t side = next | 32;
        if (side == 'l') { f->trim = 1; i++; }
        else if (side == 'r') { f->trim = 2; i++; }
        else f->trim = 3;
        break;
      }
      default:
        return false;
    }
  }
  return !(f->trunc && !f->hasWidth);
}

static uint64_t df_sf_measure(df_string text, bool columns) {
  if (!columns) return df_str_chars(text);
  df_sv s = df_view(text);
  uint64_t n = 0;
  uint64_t i = 0;
  while (i < s.len) n += (uint64_t)df_cp_columns(df_utf8_next(s.p, s.len, &i));
  return n;
}

/* U upper, L lower, S sentence (the first letter), T title (the first letter of every word) */
static df_string df_sf_case(df_string text, int mode) {
  if (mode == 'U') return df_str_toupper(text);
  if (mode == 'L') return df_str_tolower(text);
  df_sv s = df_view(text);
  df_sb b = { 0 };
  uint64_t i = 0;
  bool start = true;
  while (i < s.len) {
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    df_sb_cp(&b, start ? df_cp_toupper(cp) : df_cp_tolower(cp));
    start = mode == 'S' ? false : df_cp_is_space(cp);
  }
  return df_sb_done(&b);
}

/* the escapes of a Dflat string literal, between double quotes */
static df_string df_sf_quote(df_string text) {
  df_sv s = df_view(text);
  df_sb b = { 0 };
  df_sb_bytes(&b, "\"", 1);
  uint64_t i = 0;
  while (i < s.len) {
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    switch (cp) {
      case '"': df_sb_bytes(&b, "\\\"", 2); break;
      case '\\': df_sb_bytes(&b, "\\\\", 2); break;
      case '\n': df_sb_bytes(&b, "\\n", 2); break;
      case '\r': df_sb_bytes(&b, "\\r", 2); break;
      case '\t': df_sb_bytes(&b, "\\t", 2); break;
      case 0: df_sb_bytes(&b, "\\0", 2); break;
      default:
        if (cp < 32 || cp == 127) {
          char hex[16];
          int n = snprintf(hex, sizeof hex, "\\u{%X}", (unsigned)cp);
          df_sb_bytes(&b, hex, (uint64_t)n);
        } else {
          df_sb_cp(&b, cp);
        }
    }
  }
  df_sb_bytes(&b, "\"", 1);
  return df_sb_done(&b);
}

/* the marker as written between the quotes: \' and \\ stand for themselves */
static df_string df_sf_marker(df_string text) {
  df_sv m = df_view(text);
  df_sb b = { 0 };
  uint64_t i = 0;
  while (i < m.len) {
    if (m.p[i] == '\\' && i + 1 < m.len) i++;
    df_sb_bytes(&b, m.p + i, 1);
    i++;
  }
  return df_sb_done(&b);
}

/* the first characters of s that fit in the room given (characters or columns) */
static df_string df_sf_prefix(df_string text, uint64_t room, bool columns) {
  df_sv s = df_view(text);
  uint64_t i = 0;
  uint64_t used = 0;
  while (i < s.len) {
    uint64_t before = i;
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    uint64_t w = columns ? (uint64_t)df_cp_columns(cp) : 1;
    if (used + w > room) return df_str_slice(s, 0, before);
    used += w;
  }
  return df_str_dup(text);
}

static df_string df_sf_suffix(df_string text, uint64_t room, bool columns) {
  df_sv s = df_view(text);
  uint64_t i = s.len;
  uint64_t used = 0;
  while (i > 0) {
    uint64_t at = i;
    uint32_t cp = df_utf8_prev(s.p, &at);
    uint64_t w = columns ? (uint64_t)df_cp_columns(cp) : 1;
    if (used + w > room) break;
    used += w;
    i = at;
  }
  return df_str_slice(s, i, s.len);
}

/* order: trim, case, quote, truncate (the marker counts in the width), then fill and alignment */
static df_string df_str_format(df_string s, df_string spec) {
  df_string original = s;
  df_sf f;
  if (!df_sf_parse(spec, &f)) return df_str_dup(s);
  if (f.trim == 1) s = df_str_trim_left(s); else if (f.trim == 2) s = df_str_trim_right(s); else if (f.trim == 3) s = df_str_trim(s);
  if (f.casing) s = df_sf_case(s, f.casing);
  if (f.quote) s = df_sf_quote(s);
  if (f.hasWidth && f.trunc && df_sf_measure(s, f.columns) > f.width) {
    df_string marker = df_sf_marker(f.marker);
    uint64_t markerWidth = df_sf_measure(marker, f.columns);
    if (markerWidth >= f.width) {
      s = df_sf_prefix(marker, f.width, f.columns);
    } else {
      uint64_t room = f.width - markerWidth;
      df_sb b = { 0 };
      if (f.trunc == 1) {
        df_sv kept = df_view(df_sf_prefix(s, room, f.columns));
        df_sv mark = df_view(marker);
        df_sb_bytes(&b, kept.p, kept.len);
        df_sb_bytes(&b, mark.p, mark.len);
      } else {
        df_sv kept = df_view(df_sf_suffix(s, room, f.columns));
        df_sv mark = df_view(marker);
        df_sb_bytes(&b, mark.p, mark.len);
        df_sb_bytes(&b, kept.p, kept.len);
      }
      s = df_sb_done(&b);
    }
  }
  if (f.hasWidth) {
    uint64_t size = df_sf_measure(s, f.columns);
    int fillWidth = df_cp_columns(f.fill);
    if (size < f.width && (!f.columns || fillWidth > 0)) {
      uint64_t gap = f.width - size;
      uint64_t count = f.columns ? gap / (uint64_t)fillWidth : gap;
      uint64_t before = f.align == 2 ? count : f.align == 1 ? count / 2 : 0;
      uint64_t after = count - before;
      df_sb b = { 0 };
      df_sv text = df_view(s);
      for (uint64_t k = 0; k < before; k++) df_sb_cp(&b, f.fill);
      df_sb_bytes(&b, text.p, text.len);
      for (uint64_t k = 0; k < after; k++) df_sb_cp(&b, f.fill);
      s = df_sb_done(&b);
    }
  }
  return s == original ? df_str_dup(s) : s;
}

""";
}
