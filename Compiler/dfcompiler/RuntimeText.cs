namespace dfcompiler;

// C runtime for the string functions (spec 4.2), added to a program only when it uses them.
// Needs the Unicode tables "trim", "toupper" and "tolower" (UnicodeTables).
public static class RuntimeText {
  public const string Source = """
/* ---- text: UTF-8 strings (spec 4.2) ---- */

#define DF_NOT_FOUND UINT64_MAX

/* decodes the character at *i and moves past it; a stray byte is taken as a character */
static uint32_t df_utf8_next(const char* p, uint64_t len, uint64_t* i) {
  unsigned char c = (unsigned char)p[*i];
  uint32_t cp;
  int extra;
  if (c < 0x80) {
    cp = c;
    extra = 0;
  } else if ((c & 0xE0) == 0xC0) {
    cp = c & 0x1F;
    extra = 1;
  } else if ((c & 0xF0) == 0xE0) {
    cp = c & 0x0F;
    extra = 2;
  } else if ((c & 0xF8) == 0xF0) {
    cp = c & 0x07;
    extra = 3;
  } else {
    cp = c;
    extra = 0;
  }
  (*i)++;
  while (extra-- > 0 && *i < len && (((unsigned char)p[*i]) & 0xC0) == 0x80) {
    cp = (cp << 6) | ((unsigned char)p[*i] & 0x3F);
    (*i)++;
  }
  return cp;
}

/* moves *i (greater than 0) to the start of the previous character and returns it */
static uint32_t df_utf8_prev(const char* p, uint64_t* i) {
  uint64_t end = *i;
  uint64_t start = end - 1;
  while (start > 0 && (((unsigned char)p[start]) & 0xC0) == 0x80) {
    start--;
  }
  uint64_t at = start;
  uint32_t cp = df_utf8_next(p, end, &at);
  *i = start;
  return cp;
}

static int df_utf8_encode(uint32_t c, char* buf) {
  if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
    c = 0xFFFD;
  }
  if (c < 0x80) {
    buf[0] = (char)c;
    return 1;
  }
  if (c < 0x800) {
    buf[0] = (char)(0xC0 | (c >> 6));
    buf[1] = (char)(0x80 | (c & 0x3F));
    return 2;
  }
  if (c < 0x10000) {
    buf[0] = (char)(0xE0 | (c >> 12));
    buf[1] = (char)(0x80 | ((c >> 6) & 0x3F));
    buf[2] = (char)(0x80 | (c & 0x3F));
    return 3;
  }
  buf[0] = (char)(0xF0 | (c >> 18));
  buf[1] = (char)(0x80 | ((c >> 12) & 0x3F));
  buf[2] = (char)(0x80 | ((c >> 6) & 0x3F));
  buf[3] = (char)(0x80 | (c & 0x3F));
  return 4;
}

/* growing buffer used to build strings */
typedef struct { char* p; uint64_t len; uint64_t cap; } df_sb;

static void df_sb_bytes(df_sb* b, const char* p, uint64_t n) {
  if (n == 0) {
    return;
  }
  if (b->len + n > b->cap) {
    uint64_t cap = b->cap ? b->cap : 32;
    while (cap < b->len + n) {
      cap *= 2;
    }
    char* bigger = (char*)df_alloc(cap);
    if (b->len) {
      memcpy(bigger, b->p, b->len);
    }
    b->p = bigger;
    b->cap = cap;
  }
  memcpy(b->p + b->len, p, n);
  b->len += n;
}

static void df_sb_cp(df_sb* b, uint32_t cp) {
  char buf[4];
  int n = df_utf8_encode(cp, buf);
  df_sb_bytes(b, buf, (uint64_t)n);
}

static df_string df_sb_done(df_sb* b) {
  df_string s = { b->p ? b->p : "", b->len };
  return s;
}

static df_string df_str_slice(df_string s, uint64_t from, uint64_t to) {
  df_string r = { s.p + from, to - from };
  return r;
}

/* byte offset of a character position; the end of the string when the position is past it */
static uint64_t df_str_offset(df_string s, uint64_t chars) {
  uint64_t i = 0;
  uint64_t n = 0;
  while (n < chars && i < s.len) {
    df_utf8_next(s.p, s.len, &i);
    n++;
  }
  return i;
}

static uint32_t df_cp_fold(uint32_t cp) {
  return df_cp_tolower(df_cp_toupper(cp));
}

/* does the needle sit at byte offset 'at'? 'end' receives the byte offset after the match */
static bool df_match_at(df_string h, uint64_t at, df_string n, bool cs, uint64_t* end) {
  uint64_t hi = at;
  uint64_t ni = 0;
  while (ni < n.len) {
    if (hi >= h.len) {
      return false;
    }
    uint32_t a = df_utf8_next(h.p, h.len, &hi);
    uint32_t b = df_utf8_next(n.p, n.len, &ni);
    if (cs ? a != b : df_cp_fold(a) != df_cp_fold(b)) {
      return false;
    }
  }
  *end = hi;
  return true;
}

/* first match at or after byte offset 'from' */
static uint64_t df_find(df_string h, df_string n, uint64_t from, bool cs, uint64_t* end) {
  uint64_t at = from;
  while (true) {
    if (df_match_at(h, at, n, cs, end)) {
      return at;
    }
    if (at >= h.len) {
      return DF_NOT_FOUND;
    }
    df_utf8_next(h.p, h.len, &at);
  }
}

static int64_t df_str_indexof(df_string s, df_string v, bool cs) {
  uint64_t end;
  uint64_t at = df_find(s, v, 0, cs, &end);
  if (at == DF_NOT_FOUND) {
    return -1;
  }
  return (int64_t)df_str_chars(df_str_slice(s, 0, at));
}

static int64_t df_str_lastindexof(df_string s, df_string v, bool cs) {
  uint64_t at = 0;
  uint64_t last = DF_NOT_FOUND;
  uint64_t end;
  while (true) {
    if (df_match_at(s, at, v, cs, &end)) {
      last = at;
    }
    if (at >= s.len) {
      break;
    }
    df_utf8_next(s.p, s.len, &at);
  }
  if (last == DF_NOT_FOUND) {
    return -1;
  }
  return (int64_t)df_str_chars(df_str_slice(s, 0, last));
}

static int64_t df_str_indexof_c(df_string s, uint32_t c, bool cs) { return df_str_indexof(s, df_str_from_char(c), cs); }
static int64_t df_str_lastindexof_c(df_string s, uint32_t c, bool cs) { return df_str_lastindexof(s, df_str_from_char(c), cs); }

static bool df_str_contains(df_string s, df_string v, bool cs) {
  uint64_t end;
  return df_find(s, v, 0, cs, &end) != DF_NOT_FOUND;
}

static bool df_str_startswith(df_string s, df_string v, bool cs) {
  uint64_t end;
  return df_match_at(s, 0, v, cs, &end);
}

static bool df_str_endswith(df_string s, df_string v, bool cs) {
  uint64_t n = df_str_chars(v);
  uint64_t h = df_str_chars(s);
  if (n > h) {
    return false;
  }
  uint64_t end;
  uint64_t at = df_str_offset(s, h - n);
  return df_match_at(s, at, v, cs, &end) && end == s.len;
}

static df_string df_str_replace(df_string s, df_string find, df_string repl, bool cs) {
  if (find.len == 0) {
    return s;
  }
  df_sb b = { 0 };
  uint64_t pos = 0;
  uint64_t end;
  uint64_t at;
  bool any = false;
  while ((at = df_find(s, find, pos, cs, &end)) != DF_NOT_FOUND) {
    df_sb_bytes(&b, s.p + pos, at - pos);
    df_sb_bytes(&b, repl.p, repl.len);
    pos = end;
    any = true;
  }
  if (!any) {
    return s;
  }
  df_sb_bytes(&b, s.p + pos, s.len - pos);
  return df_sb_done(&b);
}

static df_arr_string df_str_split(df_string s, df_string sep) {
  uint64_t count = 1;
  uint64_t pos = 0;
  uint64_t at;
  uint64_t end;
  if (sep.len > 0) {
    while ((at = df_find(s, sep, pos, true, &end)) != DF_NOT_FOUND) {
      count++;
      pos = end;
    }
  }
  df_arr_string result;
  result.len = count;
  result.p = (df_string*)df_calloc(count, sizeof(df_string));
  pos = 0;
  uint64_t k = 0;
  if (sep.len > 0) {
    while ((at = df_find(s, sep, pos, true, &end)) != DF_NOT_FOUND) {
      result.p[k++] = df_str_slice(s, pos, at);
      pos = end;
    }
  }
  result.p[k] = df_str_slice(s, pos, s.len);
  return result;
}

static bool df_in_set(df_string set, uint32_t cp) {
  uint64_t i = 0;
  while (i < set.len) {
    if (df_utf8_next(set.p, set.len, &i) == cp) {
      return true;
    }
  }
  return false;
}

/* mode: 1 = left, 2 = right, 3 = both; the characters of 'set' when bySet, else spaces and non-printable ones */
static df_string df_str_trim_mode(df_string s, int mode, bool bySet, df_string set) {
  uint64_t a = 0;
  uint64_t b = s.len;
  if (mode & 1) {
    while (a < b) {
      uint64_t j = a;
      uint32_t cp = df_utf8_next(s.p, s.len, &j);
      if (!(bySet ? df_in_set(set, cp) : df_cp_is_trim(cp))) {
        break;
      }
      a = j;
    }
  }
  if (mode & 2) {
    while (b > a) {
      uint64_t j = b;
      uint32_t cp = df_utf8_prev(s.p, &j);
      if (!(bySet ? df_in_set(set, cp) : df_cp_is_trim(cp))) {
        break;
      }
      b = j;
    }
  }
  return df_str_slice(s, a, b);
}

static df_string df_str_trim(df_string s) { return df_str_trim_mode(s, 3, false, s); }
static df_string df_str_trim_left(df_string s) { return df_str_trim_mode(s, 1, false, s); }
static df_string df_str_trim_right(df_string s) { return df_str_trim_mode(s, 2, false, s); }
static df_string df_str_trim_set(df_string s, df_string set) { return df_str_trim_mode(s, 3, true, set); }
static df_string df_str_trim_left_set(df_string s, df_string set) { return df_str_trim_mode(s, 1, true, set); }
static df_string df_str_trim_right_set(df_string s, df_string set) { return df_str_trim_mode(s, 2, true, set); }

static df_string df_str_substr(df_string s, uint32_t start, uint32_t len) {
  uint64_t a = df_str_offset(s, start);
  uint64_t i = a;
  uint64_t n = 0;
  while (n < len && i < s.len) {
    df_utf8_next(s.p, s.len, &i);
    n++;
  }
  return df_str_slice(s, a, i);
}

static df_string df_str_map_case(df_string s, bool upper) {
  df_sb b = { 0 };
  uint64_t i = 0;
  while (i < s.len) {
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    df_sb_cp(&b, upper ? df_cp_toupper(cp) : df_cp_tolower(cp));
  }
  return df_sb_done(&b);
}

static df_string df_str_toupper(df_string s) { return df_str_map_case(s, true); }
static df_string df_str_tolower(df_string s) { return df_str_map_case(s, false); }

static df_string df_str_pad(df_string s, uint32_t width, uint32_t pad, bool left) {
  uint64_t n = df_str_chars(s);
  if (n >= width) {
    return s;
  }
  df_sb b = { 0 };
  if (!left) {
    df_sb_bytes(&b, s.p, s.len);
  }
  for (uint64_t k = n; k < width; k++) {
    df_sb_cp(&b, pad);
  }
  if (left) {
    df_sb_bytes(&b, s.p, s.len);
  }
  return df_sb_done(&b);
}

static df_string df_str_pad_left(df_string s, uint32_t width, uint32_t pad) { return df_str_pad(s, width, pad, true); }
static df_string df_str_pad_right(df_string s, uint32_t width, uint32_t pad) { return df_str_pad(s, width, pad, false); }

/* text[pos]: the character at a position; IndexOutOfRangeException past the end */
static uint32_t df_str_char_at(df_string s, uint64_t index, const char* function) {
  uint64_t i = 0;
  uint64_t n = 0;
  while (i < s.len) {
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    if (n == index) {
      return cp;
    }
    n++;
  }
  df_raise(&df_desc_IndexOutOfRangeException, function);
  return 0;
}

/* StringEncoding: 0 UTF8, 1 UTF16, 2 UTF32, 3 UTF16BE, 4 UTF32BE, 5 ASCII, 6 ISO88591; ends with a terminator */
static df_arr_u8 df_str_tocstr(df_string s, int32_t encoding) {
  df_sb b = { 0 };
  uint64_t i = 0;
  while (i < s.len) {
    uint32_t cp = df_utf8_next(s.p, s.len, &i);
    switch (encoding) {
      case 1:
      case 3: {
        uint16_t units[2];
        int count = 1;
        if (cp >= 0x10000) {
          cp -= 0x10000;
          units[0] = (uint16_t)(0xD800 + (cp >> 10));
          units[1] = (uint16_t)(0xDC00 + (cp & 0x3FF));
          count = 2;
        } else {
          units[0] = (uint16_t)cp;
        }
        for (int k = 0; k < count; k++) {
          char pair[2];
          pair[encoding == 1 ? 0 : 1] = (char)(units[k] & 0xFF);
          pair[encoding == 1 ? 1 : 0] = (char)(units[k] >> 8);
          df_sb_bytes(&b, pair, 2);
        }
        break;
      }
      case 2:
      case 4: {
        char quad[4];
        for (int k = 0; k < 4; k++) {
          quad[encoding == 2 ? k : 3 - k] = (char)((cp >> (8 * k)) & 0xFF);
        }
        df_sb_bytes(&b, quad, 4);
        break;
      }
      case 5: {
        char one = cp < 128 ? (char)cp : '?';
        df_sb_bytes(&b, &one, 1);
        break;
      }
      case 6: {
        char one = cp < 256 ? (char)cp : '?';
        df_sb_bytes(&b, &one, 1);
        break;
      }
      default:
        df_sb_cp(&b, cp);
        break;
    }
  }
  int zeros = (encoding == 1 || encoding == 3) ? 2 : (encoding == 2 || encoding == 4) ? 4 : 1;
  for (int k = 0; k < zeros; k++) {
    df_sb_bytes(&b, "", 1);
  }
  df_arr_u8 result = { (uint8_t*)b.p, b.len };
  return result;
}

/* Fisher-Yates on any element size; the generator is the one of Root.Random */
static void df_shuffle(void* base, uint64_t count, uint64_t size) {
  char* items = (char*)base;
  char* temp = (char*)df_alloc(size);
  for (uint64_t i = count; i > 1; i--) {
    uint64_t j = (uint64_t)df_rand_int(0, (int64_t)i - 1);
    if (j != i - 1) {
      memcpy(temp, items + (i - 1) * size, size);
      memcpy(items + (i - 1) * size, items + j * size, size);
      memcpy(items + j * size, temp, size);
    }
  }
}

static df_string df_str_shuffle(df_string s) {
  uint64_t n = df_str_chars(s);
  uint32_t* cps = (uint32_t*)df_alloc(n * sizeof(uint32_t));
  uint64_t i = 0;
  for (uint64_t k = 0; k < n; k++) {
    cps[k] = df_utf8_next(s.p, s.len, &i);
  }
  df_shuffle(cps, n, sizeof(uint32_t));
  df_sb b = { 0 };
  for (uint64_t k = 0; k < n; k++) {
    df_sb_cp(&b, cps[k]);
  }
  return df_sb_done(&b);
}

""";
}
