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
    free(b->p);
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

/* the string built; the buffer is freed */
static df_string df_sb_done(df_sb* b) {
  df_string s = df_str_make(b->p ? b->p : "", b->len);
  free(b->p);
  b->p = NULL;
  b->len = b->cap = 0;
  return s;
}

/* a new string with the bytes from..to of a text */
static df_string df_str_slice(df_sv s, uint64_t from, uint64_t to) {
  return df_str_make(s.p + from, to - from);
}

/* byte offset of a character position; the end of the string when the position is past it */
static uint64_t df_str_offset(df_sv s, uint64_t chars) {
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
static bool df_match_at(df_sv h, uint64_t at, df_sv n, bool cs, uint64_t* end) {
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
static uint64_t df_find(df_sv h, df_sv n, uint64_t from, bool cs, uint64_t* end) {
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

static int64_t df_str_indexof(df_string text, df_string value, bool cs) {
  df_sv s = df_view(text);
  uint64_t end;
  uint64_t at = df_find(s, df_view(value), 0, cs, &end);
  if (at == DF_NOT_FOUND) {
    return -1;
  }
  return (int64_t)df_utf8_count(s.p, at);
}

static int64_t df_str_lastindexof(df_string text, df_string value, bool cs) {
  df_sv s = df_view(text);
  df_sv v = df_view(value);
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
  return (int64_t)df_utf8_count(s.p, last);
}

static int64_t df_str_indexof_c(df_string s, uint32_t c, bool cs) {
  df_string one = df_str_from_char(c);
  int64_t at = df_str_indexof(s, one, cs);
  df_str_free(one);
  return at;
}

static int64_t df_str_lastindexof_c(df_string s, uint32_t c, bool cs) {
  df_string one = df_str_from_char(c);
  int64_t at = df_str_lastindexof(s, one, cs);
  df_str_free(one);
  return at;
}

static bool df_str_contains(df_string text, df_string value, bool cs) {
  uint64_t end;
  return df_find(df_view(text), df_view(value), 0, cs, &end) != DF_NOT_FOUND;
}

static bool df_str_startswith(df_string text, df_string value, bool cs) {
  uint64_t end;
  return df_match_at(df_view(text), 0, df_view(value), cs, &end);
}

static bool df_str_endswith(df_string text, df_string value, bool cs) {
  df_sv s = df_view(text);
  uint64_t n = df_str_chars(value);
  uint64_t h = df_str_chars(text);
  if (n > h) {
    return false;
  }
  uint64_t end;
  uint64_t at = df_str_offset(s, h - n);
  return df_match_at(s, at, df_view(value), cs, &end) && end == s.len;
}

static df_string df_str_replace(df_string text, df_string toFind, df_string replacement, bool cs) {
  df_sv s = df_view(text);
  df_sv find = df_view(toFind);
  df_sv repl = df_view(replacement);
  if (find.len == 0) {
    return df_str_dup(text);
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
    return df_str_dup(text);
  }
  df_sb_bytes(&b, s.p + pos, s.len - pos);
  return df_sb_done(&b);
}

static df_arr_string df_str_split(df_string text, df_string separator) {
  df_sv s = df_view(text);
  df_sv sep = df_view(separator);
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

static bool df_in_set(df_sv set, uint32_t cp) {
  uint64_t i = 0;
  while (i < set.len) {
    if (df_utf8_next(set.p, set.len, &i) == cp) {
      return true;
    }
  }
  return false;
}

/* mode: 1 = left, 2 = right, 3 = both; the characters of 'chars' when bySet, else spaces and non-printable ones */
static df_string df_str_trim_mode(df_string text, int mode, bool bySet, df_string chars) {
  df_sv s = df_view(text);
  df_sv set = df_view(chars);
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

static df_string df_str_trim(df_string s) { return df_str_trim_mode(s, 3, false, NULL); }
static df_string df_str_trim_left(df_string s) { return df_str_trim_mode(s, 1, false, NULL); }
static df_string df_str_trim_right(df_string s) { return df_str_trim_mode(s, 2, false, NULL); }
static df_string df_str_trim_set(df_string s, df_string set) { return df_str_trim_mode(s, 3, true, set); }
static df_string df_str_trim_left_set(df_string s, df_string set) { return df_str_trim_mode(s, 1, true, set); }
static df_string df_str_trim_right_set(df_string s, df_string set) { return df_str_trim_mode(s, 2, true, set); }

static df_string df_str_substr(df_string text, uint32_t start, uint32_t len) {
  df_sv s = df_view(text);
  uint64_t a = df_str_offset(s, start);
  uint64_t i = a;
  uint64_t n = 0;
  while (n < len && i < s.len) {
    df_utf8_next(s.p, s.len, &i);
    n++;
  }
  return df_str_slice(s, a, i);
}

static df_string df_str_map_case(df_string text, bool upper) {
  df_sv s = df_view(text);
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

static df_string df_str_pad(df_string text, uint32_t width, uint32_t pad, bool left) {
  df_sv s = df_view(text);
  uint64_t n = df_str_chars(text);
  if (n >= width) {
    return df_str_dup(text);
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
static uint32_t df_str_char_at(df_string text, uint64_t index, const char* function) {
  const uint8_t* h = df_str_res(text);
  if (df_is_block(h)) {
    if (index < ((const df_sroot*)h)->chars) {
      return df_bs_char_at((const df_sroot*)h, index);
    }
    df_raise(&df_desc_IndexOutOfRangeException, function);
    return 0;
  }
  df_sv s = df_view(text);
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

/* a new string: the bytes of s before 'from', then 'middle', then the bytes of s from 'to' */
static df_string df_str_splice(df_sv s, uint64_t from, uint64_t to, df_sv middle) {
  df_sb b = { 0 };
  df_sb_bytes(&b, s.p, from);
  df_sb_bytes(&b, middle.p, middle.len);
  df_sb_bytes(&b, s.p + to, s.len - to);
  return df_sb_done(&b);
}

/* insert, remove, replaceAt, append, prepend (spec 4.3): positions in characters; a position past the end is the end */
static df_string df_str_insert(df_string text, uint32_t pos, df_string add) {
  df_sv s = df_view(text);
  uint64_t at = df_str_offset(s, pos);
  return df_str_splice(s, at, at, df_view(add));
}

static df_string df_str_remove(df_string text, uint32_t pos, uint32_t len) {
  df_sv s = df_view(text);
  uint64_t from = df_str_offset(s, pos);
  uint64_t to = from;
  for (uint32_t k = 0; k < len && to < s.len; k++) {
    df_utf8_next(s.p, s.len, &to);
  }
  df_sv none = { "", 0 };
  return df_str_splice(s, from, to, none);
}

static df_string df_str_replace_at(df_string text, uint32_t pos, uint32_t len, df_string add) {
  df_sv s = df_view(text);
  uint64_t from = df_str_offset(s, pos);
  uint64_t to = from;
  for (uint32_t k = 0; k < len && to < s.len; k++) {
    df_utf8_next(s.p, s.len, &to);
  }
  return df_str_splice(s, from, to, df_view(add));
}

static df_string df_str_append_new(df_string text, df_string add) {
  return df_str_concat(text, add);
}

static df_string df_str_prepend(df_string text, df_string add) {
  return df_str_concat(add, text);
}

/* text[pos] = c, in place; IndexOutOfRangeException past the end */
static void df_str_set_char(df_string* slot, bool* owned, uint64_t index, uint32_t c, const char* function) {
  uint8_t* h = (uint8_t*)df_str_res(*slot);
  if (df_is_block(h)) {
    df_sroot* r = (df_sroot*)h;
    if (index >= r->chars) {
      df_raise(&df_desc_IndexOutOfRangeException, function);
      return;
    }
    char code[4];
    int size = df_utf8_encode(c, code);
    uint64_t start, startByte;
    df_sblock* b = df_bs_seek(r, index, &start, &startByte);
    uint64_t at = df_block_offset(b, index - start);
    uint64_t next = at + 1;
    while (next < b->used && ((unsigned char)DF_BLOCK_TEXT(b)[next] & 0xC0) == 0x80) {
      next++;
    }
    if ((uint64_t)size == next - at) {
      memcpy(DF_BLOCK_TEXT(b) + at, code, (size_t)size);   /* same size: written where it is */
      return;
    }
    df_bs_splice(r, index, 1, code, (uint64_t)size);
    return;
  }
  df_sv s = df_view(*slot);
  uint64_t i = 0;
  uint64_t n = 0;
  uint64_t from = UINT64_MAX;
  uint64_t to = 0;
  while (i < s.len) {
    uint64_t start = i;
    df_utf8_next(s.p, s.len, &i);
    if (n == index) {
      from = start;
      to = i;
      break;
    }
    n++;
  }
  if (from == UINT64_MAX) {
    df_raise(&df_desc_IndexOutOfRangeException, function);
    return;
  }
  char buf[4];
  int width = df_utf8_encode(c, buf);
  uint8_t* t = (uint8_t*)df_str_res(*slot);
  if ((uint64_t)width == to - from && df_str_cap(t) > 0) {
    memcpy(df_str_text(t) + from, buf, (size_t)width);   /* same size: written where it is */
    return;
  }
  df_sv middle = { buf, (uint64_t)width };
  df_str_take(slot, owned, df_str_splice(s, from, to, middle));
}

/* StringEncoding: 0 UTF8, 1 UTF16, 2 UTF32, 3 UTF16BE, 4 UTF32BE, 5 ASCII, 6 ISO88591; ends with a terminator */
static df_arr_u8 df_str_tocstr(df_string text, int32_t encoding) {
  df_sv s = df_view(text);
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

static df_string df_str_shuffle(df_string text) {
  df_sv s = df_view(text);
  uint64_t n = df_str_chars(text);
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
