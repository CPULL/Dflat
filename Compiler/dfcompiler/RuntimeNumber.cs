namespace dfcompiler;

// C runtime for number formatting (spec 8.1, 8.2) and number parsing (spec 9), added to a program only when it uses them.
// Needs the text part (UTF-8 helpers and the string builder).
//
// Formatting: df_fmt_i64 / df_fmt_u64 / df_fmt_r64 / df_fmt_null take the spec as text. A spec that does not parse gives the
// default text of the value (a literal spec is checked at compile time, so this only happens with a spec built at run time).
// A number is turned into decimal digits (digs: no leading or trailing zeros, value = 0.digs * 10^dp), rounded to the
// decimals asked, then laid out: grouping, decimal symbol, sign, width, fill and alignment.
// Parsing: df_parse_<type>(text, default) skips what is not a number; df_parse_<type>_f(text, default, format) reads
// strictly with a format "<prefix>{spec}[{spec}...]<suffix>".
public static class RuntimeNumber {
  public const string Source = """
/* ---- number formatting and parsing (spec 8.1, 8.2, 9) ---- */

#define DF_ISD(c) ((c) >= '0' && (c) <= '9')
#define DF_NF_MAXDIG 1100
#define DF_NF_NONE 0xFFFFFFFFu

typedef struct {
  int base;               /* 10, 16, 8, 2, 64 */
  bool upper;             /* bH */
  uint32_t decsep;        /* decimal symbol */
  bool dgiven;            /* D was written */
  int dsize;              /* decimals, -1 when not written */
  bool dfixed;            /* D02: exactly that many */
  char dmode;             /* rounding: u b c f t, 0 = u */
  int sign;               /* 0 negatives only, 1 always, 2 parentheses */
  bool signedge;          /* St: the sign at the left edge of the width */
  int width;              /* -1: none */
  uint32_t fill;
  bool fillgiven;
  char align;             /* 0 = right, < > ^ . */
  int ng;                 /* grouping: sizes and the separator after each group, from the right */
  int gsize[8];
  uint32_t gsep[8];
  bool gl2r;              /* grouping from the left */
  bool sci;               /* e */
  char lit[48];           /* text before the exponent, without the sign marker */
  int litlen;
  int litpos;             /* where the exponent sign goes in lit; -1 = at the end */
  bool lalways;           /* exponent sign always */
  int emin;               /* minimum exponent digits */
  char epad;              /* '0', '#' or 0 */
} df_nf;

static uint32_t df_nf_peek(df_string s, uint64_t i) {
  if (i >= s.len) {
    return DF_NF_NONE;
  }
  return df_utf8_next(s.p, s.len, &i);
}

static uint32_t df_nf_get(df_string s, uint64_t* i) {
  if (*i >= s.len) {
    return DF_NF_NONE;
  }
  return df_utf8_next(s.p, s.len, i);
}

static bool df_nf_letter(uint32_t c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

/* a separator or fill character: anything that is not an ASCII letter (the letters are options) */
static bool df_nf_symbol(uint32_t c) {
  return c != DF_NF_NONE && !df_nf_letter(c);
}

static int df_nf_number(df_string s, uint64_t* i) {
  int n = 0;
  int digits = 0;
  while (*i < s.len && DF_ISD(s.p[*i])) {
    if (n < 100000) {
      n = n * 10 + (s.p[*i] - '0');
    }
    (*i)++;
    digits++;
  }
  return digits == 0 ? -1 : n;
}

static bool df_nf_parse(df_string s, df_nf* f) {
  memset(f, 0, sizeof *f);
  f->base = 10;
  f->decsep = '.';
  f->dsize = -1;
  f->width = -1;
  f->fill = ' ';
  uint64_t i = 0;
  while (i < s.len) {
    uint32_t c = df_nf_get(s, &i);
    switch (c) {
      case 'b':
      case 'B': {
        uint32_t n = df_nf_get(s, &i);
        if (n == 'h') {
          f->base = 16;
        } else if (n == 'H') {
          f->base = 16;
          f->upper = true;
        } else if (n == 'd' || n == 'D') {
          f->base = 10;
        } else if (n == 'b' || n == 'B') {
          f->base = 2;
        } else if (n == 'o' || n == 'O') {
          f->base = 8;
        } else if (n == '6' && df_nf_get(s, &i) == '4') {
          f->base = 64;
        } else {
          return false;
        }
        break;
      }
      case 'd':
      case 'D': {
        f->dgiven = true;
        uint32_t n = df_nf_peek(s, i);
        if (n == 'l' || n == 'L') {
          df_nf_get(s, &i);
          f->decsep = '.';
        } else if (df_nf_symbol(n) && !(n < 128 && DF_ISD(n))) {
          df_nf_get(s, &i);
          f->decsep = n;
        }
        uint64_t start = i;
        int size = df_nf_number(s, &i);
        if (size >= 0) {
          f->dsize = size;
          f->dfixed = (i - start) > 1 && s.p[start] == '0';
        }
        n = df_nf_peek(s, i);
        if (n != DF_NF_NONE && strchr("ubcftUBCFT", (int)n) != NULL && n < 128) {
          df_nf_get(s, &i);
          f->dmode = (char)(n | 0x20);
        }
        break;
      }
      case 's':
      case 'S': {
        uint32_t n = df_nf_peek(s, i);
        if (n == 't' || n == 'T') {
          df_nf_get(s, &i);
          f->signedge = true;
          n = df_nf_get(s, &i);
          if (n == '-') {
            f->sign = 0;
          } else if (n == '+') {
            f->sign = 1;
          } else {
            return false;
          }
        } else if (n == '-') {
          df_nf_get(s, &i);
          f->sign = 0;
        } else if (n == '+') {
          df_nf_get(s, &i);
          f->sign = 1;
        } else if (n == '(') {
          df_nf_get(s, &i);
          if (df_nf_get(s, &i) != ')') {
            return false;
          }
          f->sign = 2;
        }
        break;
      }
      case 'w':
      case 'W': {
        int n = df_nf_number(s, &i);
        if (n < 0) {
          return false;
        }
        f->width = n;
        break;
      }
      case 'f':
      case 'F': {
        uint32_t n = df_nf_get(s, &i);
        if (!df_nf_symbol(n)) {
          return false;
        }
        f->fill = n;
        f->fillgiven = true;
        break;
      }
      case 'a':
      case 'A': {
        uint32_t n = df_nf_get(s, &i);
        if (n != '<' && n != '>' && n != '^' && n != '.') {
          return false;
        }
        f->align = (char)n;
        break;
      }
      case 'g':
      case 'G': {
        uint32_t n = df_nf_get(s, &i);
        f->ng = 0;
        if (n == 'L' || n == 'l') {
          f->ng = 1;
          f->gsize[0] = 3;
          f->gsep[0] = ',';
        } else {
          if (!df_nf_symbol(n) || (n < 128 && DF_ISD(n))) {
            return false;
          }
          uint32_t d = df_nf_get(s, &i);
          if (d < '1' || d > '9') {
            return false;
          }
          f->gsize[0] = (int)(d - '0');
          f->gsep[0] = n;
          f->ng = 1;
          while (df_nf_peek(s, i) >= '1' && df_nf_peek(s, i) <= '9') {
            if (f->ng == 8) {
              return false;
            }
            d = df_nf_get(s, &i);
            uint32_t sep = df_nf_get(s, &i);
            if (!df_nf_symbol(sep) || (sep < 128 && DF_ISD(sep))) {
              return false;
            }
            f->gsize[f->ng] = (int)(d - '0');
            f->gsep[f->ng] = sep;
            f->ng++;
          }
          if (df_nf_peek(s, i) == 'r' || df_nf_peek(s, i) == 'R') {
            df_nf_get(s, &i);
            f->gl2r = true;
          }
        }
        break;
      }
      case 'e':
      case 'E': {
        f->sci = true;
        f->lit[0] = 'e';
        f->litlen = 1;
        f->litpos = 1;
        f->lalways = true;
        if (df_nf_peek(s, i) == '[') {
          df_nf_get(s, &i);
          f->litlen = 0;
          f->litpos = -1;
          f->lalways = false;
          for (;;) {
            uint32_t n = df_nf_get(s, &i);
            if (n == DF_NF_NONE) {
              return false;
            }
            if (n == ']') {
              break;
            }
            if (n == '\\') {
              n = df_nf_get(s, &i);
              if (n == DF_NF_NONE) {
                return false;
              }
            } else if (n == '+' || n == '-') {
              f->litpos = f->litlen;
              f->lalways = n == '+';
              continue;
            }
            char buf[4];
            int len = df_utf8_encode(n, buf);
            if (f->litlen + len > (int)sizeof f->lit) {
              return false;
            }
            memcpy(f->lit + f->litlen, buf, (size_t)len);
            f->litlen += len;
          }
        }
        uint32_t n = df_nf_peek(s, i);
        if (n == '0' || n == '#') {
          df_nf_get(s, &i);
          f->epad = (char)n;
          f->emin = df_nf_number(s, &i);
          if (f->emin < 0) {
            return false;
          }
        }
        break;
      }
      default:
        return false;
    }
  }
  if (f->sci) {
    if (!f->dgiven) {
      f->dsize = 6;
    }
    if (f->align == 0) {
      f->align = '<';
    }
  }
  return true;
}

/* ---- decimal digits of a value ---- */

typedef struct {
  int kind;            /* 0 number, 1 NaN, 2 infinity, 3 null */
  bool neg;
  bool real;
  bool single;
  double r;            /* the value of a real */
  uint64_t mag;        /* the magnitude of an integer */
  uint64_t raw;        /* the bits, for the bases other than 10 */
  int nbits;
} df_nv;

/* digits of the value without leading or trailing zeros; value = 0.digs * 10^dp. exact: every digit of the real */
static void df_nf_digits(const df_nv* v, bool exact, char* digs, int* nd, int* dp) {
  if (!v->real) {
    char b[24];
    int n = snprintf(b, sizeof b, "%llu", (unsigned long long)v->mag);
    *dp = n;
    while (n > 1 && b[n - 1] == '0') {
      n--;
    }
    memcpy(digs, b, (size_t)n);
    *nd = n;
    return;
  }
  double x = fabs(v->r);
  if (x == 0) {
    digs[0] = '0';
    *nd = 1;
    *dp = 1;
    return;
  }
  char buf[900];
  if (exact) {
    snprintf(buf, sizeof buf, "%.800e", x);
  } else {
    int maxp = v->single ? 9 : 17;
    int p;
    for (p = 1; p <= maxp; p++) {
      snprintf(buf, sizeof buf, "%.*e", p - 1, x);
      double back = strtod(buf, NULL);
      if (v->single ? (float)back == (float)x : back == x) {
        break;
      }
    }
    if (p > maxp) {
      snprintf(buf, sizeof buf, "%.*e", maxp - 1, x);
    }
  }
  int n = 0;
  char* e = strchr(buf, 'e');
  for (char* c = buf; c < e; c++) {
    if (DF_ISD(*c)) {
      digs[n++] = *c;
    }
  }
  *dp = atoi(e + 1) + 1;
  while (n > 1 && digs[n - 1] == '0') {
    n--;
  }
  *nd = n;
}

/* keeps the first keep digits, rounding the rest with the mode (neg: the value is negative) */
static void df_nf_round(char* digs, int* nd, int* dp, int keep, char mode, bool neg) {
  if (keep >= *nd || (*nd == 1 && digs[0] == '0')) {
    return;
  }
  char d = keep >= 0 ? digs[keep] : '0';
  bool rest = keep < 0;
  for (int k = (keep >= 0 ? keep + 1 : 0); k < *nd && !rest; k++) {
    if (digs[k] != '0') {
      rest = true;
    }
  }
  bool discarded = d != '0' || rest;
  bool tie = d == '5' && !rest;
  bool up;
  switch (mode) {
    case 'b': {
      char last = keep > 0 ? digs[keep - 1] : '0';
      up = d > '5' || (d == '5' && rest) || (tie && ((last - '0') & 1));
      break;
    }
    case 'c':
      up = !neg && discarded;
      break;
    case 'f':
      up = neg && discarded;
      break;
    case 't':
      up = false;
      break;
    default:
      up = d >= '5';
      break;
  }
  if (keep <= 0) {
    if (up) {
      digs[0] = '1';
      *nd = 1;
      *dp = *dp - keep + 1;
    } else {
      digs[0] = '0';
      *nd = 1;
      *dp = 1;
    }
    return;
  }
  *nd = keep;
  if (up) {
    int k = keep - 1;
    while (k >= 0 && digs[k] == '9') {
      digs[k] = '0';
      k--;
    }
    if (k >= 0) {
      digs[k]++;
    } else {
      memmove(digs + 1, digs, (size_t)*nd);
      digs[0] = '1';
      (*nd)++;
      (*dp)++;
    }
  }
  while (*nd > 1 && digs[*nd - 1] == '0') {
    (*nd)--;
  }
}

static uint64_t df_nf_cps(const char* p, uint64_t n) {
  uint64_t count = 0;
  for (uint64_t i = 0; i < n; i++) {
    if (((unsigned char)p[i] & 0xC0) != 0x80) {
      count++;
    }
  }
  return count;
}

static void df_nf_groups(df_sb* body, const df_nf* f, const char* ip, int il) {
  static uint32_t cuts[DF_NF_MAXDIG + 8];
  memset(cuts, 0, sizeof(uint32_t) * (size_t)(il + 1));
  if (f->ng > 0) {
    int pos = f->gl2r ? 0 : il;
    for (int gi = 0;; gi++) {
      int k = gi < f->ng ? gi : f->ng - 1;
      pos += f->gl2r ? f->gsize[k] : -f->gsize[k];
      if (pos <= 0 || pos >= il) {
        break;
      }
      cuts[pos] = f->gsep[k];
    }
  }
  for (int i = 0; i < il; i++) {
    if (cuts[i]) {
      df_sb_cp(body, cuts[i]);
    }
    df_sb_bytes(body, ip + i, 1);
  }
}

static df_string df_nf_render(const df_nf* f, df_nv* v) {
  static char digs[DF_NF_MAXDIG];
  static char ip[DF_NF_MAXDIG + 8];
  static char fp[DF_NF_MAXDIG + 8];
  df_sb body = { 0 };
  df_sb out = { 0 };
  int fl = 0;
  bool neg = v->neg;
  int extra = 0;   /* a.: missing decimal places, to line the decimal symbol up */

  if (v->kind == 3) {
    df_sb_bytes(&body, "null", 4);
    neg = false;
  } else if (v->kind == 1) {
    df_sb_bytes(&body, "NaN", 3);
    neg = false;
  } else if (v->kind == 2) {
    df_sb_bytes(&body, "\xE2\x88\x9E", 3);
  } else if (f->base != 10) {
    static const char* set64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    char rev[80];
    int n = 0;
    uint64_t m = v->raw;
    uint64_t b = (uint64_t)(f->base == 64 ? 64 : f->base);
    if (m == 0) {
      rev[n++] = f->base == 64 ? 'A' : '0';
    }
    while (m != 0) {
      uint64_t digit = m % b;
      if (f->base == 64) {
        rev[n++] = set64[digit];
      } else if (digit < 10) {
        rev[n++] = (char)('0' + digit);
      } else {
        rev[n++] = (char)((f->upper ? 'A' : 'a') + (digit - 10));
      }
      m /= b;
    }
    int il = 0;
    while (n > 0) {
      ip[il++] = rev[--n];
    }
    df_nf_groups(&body, f, ip, il);
    neg = false;
  } else {
    int nd;
    int dp;
    char mode = f->dmode ? f->dmode : 'u';
    if (f->sci) {
      df_nf_digits(v, true, digs, &nd, &dp);
      int size = f->dsize >= 0 ? f->dsize : 6;
      bool zero = nd == 1 && digs[0] == '0';
      if (zero) {
        neg = false;
        df_sb_bytes(&body, "0", 1);
      } else {
        df_nf_round(digs, &nd, &dp, 1 + size, mode, neg);
        df_sb_bytes(&body, digs, 1);
        fl = nd - 1;
        if (fl > 0) {
          memcpy(fp, digs + 1, (size_t)fl);
        }
        if (f->dgiven && f->dfixed) {
          while (fl < size) {
            fp[fl++] = '0';
          }
        }
        if (fl > 0) {
          df_sb_cp(&body, f->decsep);
          df_sb_bytes(&body, fp, (uint64_t)fl);
        }
        int e = dp - 1;
        int litpos = f->litpos < 0 ? f->litlen : f->litpos;
        df_sb_bytes(&body, f->lit, (uint64_t)litpos);
        if (e < 0) {
          df_sb_bytes(&body, "-", 1);
        } else if (f->lalways) {
          df_sb_bytes(&body, "+", 1);
        }
        df_sb_bytes(&body, f->lit + litpos, (uint64_t)(f->litlen - litpos));
        char eb[16];
        int en = snprintf(eb, sizeof eb, "%d", e < 0 ? -e : e);
        for (int k = en; k < f->emin; k++) {
          df_sb_bytes(&body, f->epad == '0' ? "0" : " ", 1);
        }
        df_sb_bytes(&body, eb, (uint64_t)en);
      }
    } else {
      int size = f->dsize;
      df_nf_digits(v, size >= 0, digs, &nd, &dp);
      if (size >= 0) {
        df_nf_round(digs, &nd, &dp, dp + size, mode, neg);
      }
      if (nd == 1 && digs[0] == '0') {
        neg = false;
      }
      int il = 0;
      if (dp <= 0) {
        ip[il++] = '0';
      } else {
        for (int k = 0; k < dp; k++) {
          ip[il++] = k < nd ? digs[k] : '0';
        }
      }
      if (dp < 0) {
        for (int k = 0; k < -dp; k++) {
          fp[fl++] = '0';
        }
      }
      for (int k = dp > 0 ? dp : 0; k < nd; k++) {
        fp[fl++] = digs[k];
      }
      if (nd == 1 && digs[0] == '0') {
        fl = 0;
      }
      if (size >= 0 && f->dfixed) {
        while (fl < size) {
          fp[fl++] = '0';
        }
      }
      df_nf_groups(&body, f, ip, il);
      if (fl > 0) {
        df_sb_cp(&body, f->decsep);
        df_sb_bytes(&body, fp, (uint64_t)fl);
      }
      if (size > 0 && !f->dfixed) {
        extra = fl == 0 ? size + 1 : size - fl;
      }
    }
  }

  /* sign, width, fill and alignment */
  const char* lead = "";
  const char* trail = "";
  if (neg) {
    if (f->sign == 2) {
      lead = "(";
      trail = ")";
    } else {
      lead = "-";
    }
  } else if (f->sign == 1 && v->kind != 1 && v->kind != 3 && f->base == 10) {
    lead = "+";
  }
  uint64_t len = df_nf_cps(lead, strlen(lead)) + df_nf_cps(body.p ? body.p : "", body.len) + df_nf_cps(trail, strlen(trail));
  uint64_t pad = f->width > 0 && (uint64_t)f->width > len ? (uint64_t)f->width - len : 0;
  uint64_t left = pad;
  uint64_t right = 0;
  switch (f->align) {
    case '<':
      left = 0;
      right = pad;
      break;
    case '^':
      left = pad / 2;
      right = pad - left;
      break;
    case '.':
      right = (uint64_t)extra < pad ? (uint64_t)extra : pad;
      left = pad - right;
      break;
    default:
      break;
  }
  if (f->signedge) {
    df_sb_bytes(&out, lead, strlen(lead));
    lead = "";
  }
  for (uint64_t k = 0; k < left; k++) {
    df_sb_cp(&out, f->fill);
  }
  df_sb_bytes(&out, lead, strlen(lead));
  df_sb_bytes(&out, body.p, body.len);
  df_sb_bytes(&out, trail, strlen(trail));
  for (uint64_t k = 0; k < right; k++) {
    df_sb_cp(&out, f->fill);
  }
  return df_sb_done(&out);
}

static df_string df_fmt_i64(int64_t x, int bits, df_string spec) {
  df_nf f;
  if (!df_nf_parse(spec, &f)) {
    return df_str_from_i64(x);
  }
  df_nv v = { 0 };
  v.neg = x < 0;
  v.mag = x < 0 ? (uint64_t)0 - (uint64_t)x : (uint64_t)x;
  v.raw = bits < 64 ? (uint64_t)x & (((uint64_t)1 << bits) - 1) : (uint64_t)x;
  v.nbits = bits;
  return df_nf_render(&f, &v);
}

static df_string df_fmt_u64(uint64_t x, int bits, df_string spec) {
  df_nf f;
  if (!df_nf_parse(spec, &f)) {
    return df_str_from_u64(x);
  }
  df_nv v = { 0 };
  v.mag = x;
  v.raw = x;
  v.nbits = bits;
  return df_nf_render(&f, &v);
}

static df_string df_fmt_r64(double x, bool single, df_string spec) {
  df_nf f;
  if (!df_nf_parse(spec, &f)) {
    return single ? df_str_from_r32((float)x) : df_str_from_r64(x);
  }
  df_nv v = { 0 };
  v.real = true;
  v.single = single;
  v.r = x;
  v.neg = signbit(x) != 0;
  if (isnan(x)) {
    v.kind = 1;
  } else if (isinf(x)) {
    v.kind = 2;
  }
  if (single) {
    float fx = (float)x;
    uint32_t b32;
    memcpy(&b32, &fx, 4);
    v.raw = b32;
    v.nbits = 32;
  } else {
    memcpy(&v.raw, &x, 8);
    v.nbits = 64;
  }
  return df_nf_render(&f, &v);
}

static df_string df_fmt_null(df_string spec) {
  df_nf f;
  if (!df_nf_parse(spec, &f)) {
    return df_str_lit("null", 4);
  }
  df_nv v = { 0 };
  v.kind = 3;
  return df_nf_render(&f, &v);
}

/* ---- parsing ---- */

typedef struct {
  bool neg;
  bool raw;          /* read from a base other than 10: the bits */
  bool overflow;     /* more than 64 bits of digits */
  uint64_t mag;
  bool nan;
  bool inf;
  int nl;
  char num[DF_NF_MAXDIG + 40];   /* the number as text, for strtod */
} df_pv;

static void df_pv_digit(df_pv* r, int d) {
  if (r->mag > (UINT64_MAX - (uint64_t)d) / 10) {
    r->overflow = true;
  } else {
    r->mag = r->mag * 10 + (uint64_t)d;
  }
}

static void df_pv_add(df_pv* r, char c) {
  if (r->nl < DF_NF_MAXDIG) {
    r->num[r->nl++] = c;
  }
}

/* no format: skips what is not a number, reads the first number */
static bool df_np_default(df_string t, bool real, df_pv* r) {
  memset(r, 0, sizeof *r);
  uint64_t n = t.len;
  uint64_t j;
  for (j = 0; j < n; j++) {
    unsigned char c = (unsigned char)t.p[j];
    if (DF_ISD(c)) {
      break;
    }
    if (c == '.' && j + 1 < n && DF_ISD(t.p[j + 1])) {
      break;
    }
    if (real) {
      bool minus = j > 0 && t.p[j - 1] == '-';
      if (j + 3 <= n && memcmp(t.p + j, "NaN", 3) == 0) {
        r->nan = true;
        return true;
      }
      if (j + 3 <= n && memcmp(t.p + j, "INF", 3) == 0) {
        r->inf = true;
        r->neg = minus;
        return true;
      }
      if (j + 3 <= n && memcmp(t.p + j, "\xE2\x88\x9E", 3) == 0) {
        r->inf = true;
        r->neg = minus;
        return true;
      }
    }
  }
  if (j >= n) {
    return false;
  }
  r->neg = j > 0 && t.p[j - 1] == '-';
  uint64_t k = j;
  if (!DF_ISD(t.p[k])) {
    df_pv_add(r, '0');
  }
  while (k < n && DF_ISD(t.p[k])) {
    df_pv_digit(r, t.p[k] - '0');
    df_pv_add(r, t.p[k]);
    k++;
  }
  if (k + 1 < n && t.p[k] == '.' && DF_ISD(t.p[k + 1])) {
    df_pv_add(r, '.');
    k++;
    while (k < n && DF_ISD(t.p[k])) {
      df_pv_add(r, t.p[k]);
      k++;
    }
  }
  if (real && k < n && (t.p[k] == 'e' || t.p[k] == 'E')) {
    uint64_t m = k + 1;
    char sign = 0;
    if (m < n && (t.p[m] == '+' || t.p[m] == '-')) {
      sign = t.p[m];
      m++;
    }
    if (m < n && DF_ISD(t.p[m])) {
      df_pv_add(r, 'e');
      if (sign) {
        df_pv_add(r, sign);
      }
      while (m < n && DF_ISD(t.p[m])) {
        df_pv_add(r, t.p[m]);
        m++;
      }
    }
  }
  return true;
}

static void df_np_skipfill(const df_nf* f, df_string t, uint64_t* pos) {
  if (!(f->fillgiven || f->width >= 0 || f->align)) {
    return;
  }
  while (*pos < t.len) {
    uint64_t k = *pos;
    if (df_nf_get(t, &k) != f->fill) {
      break;
    }
    *pos = k;
  }
}

static int df_np_digitval(unsigned char c, int base) {
  int v;
  if (c >= '0' && c <= '9') {
    v = c - '0';
  } else if (base == 64) {
    if (c >= 'A' && c <= 'Z') {
      v = c - 'A';
    } else if (c >= 'a' && c <= 'z') {
      v = 26 + c - 'a';
    } else if (c >= '0' && c <= '9') {
      v = 52 + c - '0';
    } else if (c == '+') {
      v = 62;
    } else if (c == '/') {
      v = 63;
    } else {
      return -1;
    }
    return v;
  } else if (c >= 'a' && c <= 'f') {
    v = 10 + c - 'a';
  } else if (c >= 'A' && c <= 'F') {
    v = 10 + c - 'A';
  } else {
    return -1;
  }
  return v < base ? v : -1;
}

/* one placeholder at *pos */
static bool df_np_one(const df_nf* f, df_string t, uint64_t* pos, df_pv* r) {
  memset(r, 0, sizeof *r);
  uint64_t n = t.len;
  uint64_t k = *pos;
  bool paren = false;
  if (f->base == 10 || f->base != 10) {
    if (f->signedge) {
      if (k < n && (t.p[k] == '-' || (f->sign == 1 && t.p[k] == '+'))) {
        r->neg = t.p[k] == '-';
        k++;
      } else if (f->sign == 1) {
        return false;
      }
    }
    df_np_skipfill(f, t, &k);
    if (!f->signedge && f->base == 10) {
      if (f->sign == 2) {
        if (k < n && t.p[k] == '(') {
          paren = true;
          r->neg = true;
          k++;
        }
      } else if (k < n && (t.p[k] == '-' || (f->sign == 1 && t.p[k] == '+'))) {
        r->neg = t.p[k] == '-';
        k++;
      } else if (f->sign == 1) {
        return false;
      }
    }
  }
  int digits = 0;
  if (f->base != 10) {
    r->raw = true;
    while (k < n) {
      int d = df_np_digitval((unsigned char)t.p[k], f->base);
      if (d < 0) {
        break;
      }
      if (r->mag > (UINT64_MAX >> (f->base == 64 ? 6 : f->base == 16 ? 4 : f->base == 8 ? 3 : 1))) {
        r->overflow = true;
      } else {
        r->mag = r->mag * (uint64_t)f->base + (uint64_t)d;
      }
      k++;
      digits++;
    }
    if (digits == 0) {
      return false;
    }
  } else {
    while (k < n) {
      if (DF_ISD(t.p[k])) {
        df_pv_digit(r, t.p[k] - '0');
        df_pv_add(r, t.p[k]);
        k++;
        digits++;
        continue;
      }
      if (f->ng > 0 && digits > 0) {
        uint64_t k2 = k;
        uint32_t c = df_nf_get(t, &k2);
        bool isSep = false;
        for (int g = 0; g < f->ng; g++) {
          isSep |= c == f->gsep[g];
        }
        if (isSep && k2 < n && DF_ISD(t.p[k2])) {
          k = k2;
          continue;
        }
      }
      break;
    }
    if (digits == 0) {
      return false;
    }
    int fracdigits = 0;
    if (f->dsize != 0 && k < n) {
      uint64_t k2 = k;
      uint32_t c = df_nf_get(t, &k2);
      if (c == f->decsep && k2 < n && DF_ISD(t.p[k2])) {
        df_pv_add(r, '.');
        k = k2;
        while (k < n && DF_ISD(t.p[k]) && (f->dsize < 0 || fracdigits < f->dsize)) {
          df_pv_add(r, t.p[k]);
          k++;
          fracdigits++;
        }
      }
    }
    if (f->dfixed && f->dsize > 0 && fracdigits != f->dsize) {
      return false;
    }
    if (f->sci) {
      int litpos = f->litpos < 0 ? f->litlen : f->litpos;
      if (k + (uint64_t)litpos > n || memcmp(t.p + k, f->lit, (size_t)litpos) != 0) {
        return false;
      }
      k += (uint64_t)litpos;
      char sign = 0;
      if (k < n && (t.p[k] == '-' || (f->lalways && t.p[k] == '+'))) {
        sign = t.p[k];
        k++;
      } else if (f->lalways) {
        return false;
      }
      int after = f->litlen - litpos;
      if (k + (uint64_t)after > n || memcmp(t.p + k, f->lit + litpos, (size_t)after) != 0) {
        return false;
      }
      k += (uint64_t)after;
      if (f->epad == '#') {
        while (k < n && t.p[k] == ' ') {
          k++;
        }
      }
      int edigits = 0;
      df_pv_add(r, 'e');
      if (sign == '-') {
        df_pv_add(r, '-');
      }
      while (k < n && DF_ISD(t.p[k])) {
        df_pv_add(r, t.p[k]);
        k++;
        edigits++;
      }
      if (edigits == 0 || (f->epad == '0' && edigits < f->emin)) {
        return false;
      }
    }
  }
  if (paren) {
    if (k >= n || t.p[k] != ')') {
      return false;
    }
    k++;
  }
  df_np_skipfill(f, t, &k);
  *pos = k;
  return true;
}

/* a format "<prefix>{spec}{spec}<suffix>": the first placeholder that matches the whole text wins */
static bool df_np_format(df_string t, df_string fmt, df_pv* r) {
  uint64_t n = fmt.len;
  uint64_t i = 0;
  df_sb pre = { 0 };
  df_sb suf = { 0 };
  struct { const char* p; uint64_t n; } alts[8];
  int na = 0;
  while (i < n) {
    char c = fmt.p[i];
    if (c == '\\' && i + 1 < n && (fmt.p[i + 1] == '{' || fmt.p[i + 1] == '}' || fmt.p[i + 1] == '\\')) {
      df_sb_bytes(&pre, fmt.p + i + 1, 1);
      i += 2;
    } else if (c == '{') {
      break;
    } else if (c == '}') {
      return false;
    } else {
      df_sb_bytes(&pre, &c, 1);
      i++;
    }
  }
  while (i < n && fmt.p[i] == '{') {
    uint64_t j = i + 1;
    while (j < n && fmt.p[j] != '}') {
      if (fmt.p[j] == '{') {
        return false;
      }
      j++;
    }
    if (j >= n || na == 8) {
      return false;
    }
    alts[na].p = fmt.p + i + 1;
    alts[na].n = j - i - 1;
    na++;
    i = j + 1;
  }
  if (na == 0) {
    return false;
  }
  while (i < n) {
    char c = fmt.p[i];
    if (c == '\\' && i + 1 < n && (fmt.p[i + 1] == '{' || fmt.p[i + 1] == '}' || fmt.p[i + 1] == '\\')) {
      df_sb_bytes(&suf, fmt.p + i + 1, 1);
      i += 2;
    } else if (c == '{' || c == '}') {
      return false;
    } else {
      df_sb_bytes(&suf, &c, 1);
      i++;
    }
  }
  df_nf specs[8];
  for (int a = 0; a < na; a++) {
    df_string s = { alts[a].p, alts[a].n };
    if (!df_nf_parse(s, &specs[a])) {
      return false;
    }
  }
  for (int a = 0; a < na; a++) {
    if (t.len < pre.len || (pre.len && memcmp(t.p, pre.p, pre.len) != 0)) {
      return false;
    }
    uint64_t pos = pre.len;
    df_pv tmp;
    if (!df_np_one(&specs[a], t, &pos, &tmp)) {
      continue;
    }
    if (t.len - pos != suf.len || (suf.len && memcmp(t.p + pos, suf.p, suf.len) != 0)) {
      continue;
    }
    *r = tmp;
    return true;
  }
  return false;
}

static bool df_np_run(df_string t, df_string fmt, df_pv* r, bool real) {
  if (fmt.len == 0) {
    return df_np_default(t, real, r);
  }
  return df_np_format(t, fmt, r);
}

/* the value of a number read, as the type asks: saturating, with a silent note when it did not fit */
static int64_t df_np_signed(const df_pv* r, int bits) {
  int64_t max = bits == 64 ? INT64_MAX : (int64_t)(((uint64_t)1 << (bits - 1)) - 1);
  int64_t min = -max - 1;
  if (r->raw) {
    if (r->overflow || (bits < 64 && (r->mag >> bits) != 0)) {
      df_silent_note(&df_desc_OutOfRangeCastException);
      return max;
    }
    uint64_t m = r->mag;
    if (bits < 64 && ((m >> (bits - 1)) & 1)) {
      m |= ~(uint64_t)0 << bits;
    }
    return (int64_t)m;
  }
  if (!r->neg) {
    if (r->overflow || r->mag > (uint64_t)max) {
      df_silent_note(&df_desc_OutOfRangeCastException);
      return max;
    }
    return (int64_t)r->mag;
  }
  if (r->overflow || r->mag > (uint64_t)max + 1) {
    df_silent_note(&df_desc_OutOfRangeCastException);
    return min;
  }
  return r->mag == (uint64_t)max + 1 ? min : -(int64_t)r->mag;
}

static uint64_t df_np_unsigned(const df_pv* r, int bits) {
  uint64_t max = bits == 64 ? UINT64_MAX : (((uint64_t)1 << bits) - 1);
  if (r->neg && !r->raw && (r->mag != 0 || r->overflow)) {
    df_silent_note(&df_desc_OverflowException);
    return 0;
  }
  if (r->overflow || r->mag > max) {
    df_silent_note(&df_desc_OutOfRangeCastException);
    return max;
  }
  return r->mag;
}

static double df_np_real(const df_pv* r) {
  if (r->nan) {
    return NAN;
  }
  if (r->inf) {
    return r->neg ? -INFINITY : INFINITY;
  }
  double v;
  if (r->raw) {
    v = (double)r->mag;
  } else {
    char buf[DF_NF_MAXDIG + 41];
    memcpy(buf, r->num, (size_t)r->nl);
    buf[r->nl] = 0;
    v = strtod(buf, NULL);
  }
  return r->neg ? -v : v;
}

/* an integer read with an exponent: the number as text goes through strtod */
static void df_np_int_fix(df_pv* r) {
  if (r->raw || !strchr(r->num, 'e')) {
    return;
  }
  char buf[DF_NF_MAXDIG + 41];
  memcpy(buf, r->num, (size_t)r->nl);
  buf[r->nl] = 0;
  double v = strtod(buf, NULL);
  r->overflow = v >= 18446744073709551616.0;
  r->mag = r->overflow ? 0 : (uint64_t)v;
}

#define DF_PARSE_SIGNED(T, N, BITS) \
static T df_parse_##N##_f(df_string text, T def, df_string fmt) { \
  df_pv r; \
  if (!df_np_run(text, fmt, &r, false)) { \
    return def; \
  } \
  df_np_int_fix(&r); \
  return (T)df_np_signed(&r, BITS); \
} \
static T df_parse_##N(df_string text, T def) { \
  df_string none = { "", 0 }; \
  return df_parse_##N##_f(text, def, none); \
} \
static T df_parse_##N##_fmt(df_string text, df_string fmt) { \
  return df_parse_##N##_f(text, 0, fmt); \
}

#define DF_PARSE_UNSIGNED(T, N, BITS) \
static T df_parse_##N##_f(df_string text, T def, df_string fmt) { \
  df_pv r; \
  if (!df_np_run(text, fmt, &r, false)) { \
    return def; \
  } \
  df_np_int_fix(&r); \
  return (T)df_np_unsigned(&r, BITS); \
} \
static T df_parse_##N(df_string text, T def) { \
  df_string none = { "", 0 }; \
  return df_parse_##N##_f(text, def, none); \
} \
static T df_parse_##N##_fmt(df_string text, df_string fmt) { \
  return df_parse_##N##_f(text, 0, fmt); \
}

#define DF_PARSE_REAL(T, N) \
static T df_parse_##N##_f(df_string text, T def, df_string fmt) { \
  df_pv r; \
  if (!df_np_run(text, fmt, &r, true)) { \
    return def; \
  } \
  return (T)df_np_real(&r); \
} \
static T df_parse_##N(df_string text, T def) { \
  df_string none = { "", 0 }; \
  return df_parse_##N##_f(text, def, none); \
} \
static T df_parse_##N##_fmt(df_string text, df_string fmt) { \
  return df_parse_##N##_f(text, 0, fmt); \
}

DF_PARSE_SIGNED(int8_t, i8, 8)
DF_PARSE_SIGNED(int16_t, i16, 16)
DF_PARSE_SIGNED(int32_t, i32, 32)
DF_PARSE_SIGNED(int64_t, i64, 64)
DF_PARSE_UNSIGNED(uint8_t, u8, 8)
DF_PARSE_UNSIGNED(uint16_t, u16, 16)
DF_PARSE_UNSIGNED(uint32_t, u32, 32)
DF_PARSE_UNSIGNED(uint64_t, u64, 64)
DF_PARSE_REAL(float, r32)
DF_PARSE_REAL(double, r64)

/* tryParse (spec 9): true and the value in *result, or false (result unchanged). A number that does not fit its type is a failure
   here (C# TryParse), not a saturated value: the silent notes of parse are dropped. */
#define DF_TRYPARSE(T, N, REAL, CONV) \
static bool df_tryparse_##N##_f(df_string text, T* result, df_string fmt) { \
  df_pv r; \
  if (!df_np_run(text, fmt, &r, REAL)) { \
    return false; \
  } \
  if (!REAL) { \
    df_np_int_fix(&r); \
  } \
  const df_descriptor* saved = df_silent_desc; \
  df_silent_desc = NULL; \
  T value = (T)(CONV); \
  bool fits = df_silent_desc == NULL; \
  df_silent_desc = saved; \
  if (fits) { \
    *result = value; \
  } \
  return fits; \
} \
static bool df_tryparse_##N(df_string text, T* result) { \
  df_string none = { "", 0 }; \
  return df_tryparse_##N##_f(text, result, none); \
}

DF_TRYPARSE(int8_t, i8, false, df_np_signed(&r, 8))
DF_TRYPARSE(int16_t, i16, false, df_np_signed(&r, 16))
DF_TRYPARSE(int32_t, i32, false, df_np_signed(&r, 32))
DF_TRYPARSE(int64_t, i64, false, df_np_signed(&r, 64))
DF_TRYPARSE(uint8_t, u8, false, df_np_unsigned(&r, 8))
DF_TRYPARSE(uint16_t, u16, false, df_np_unsigned(&r, 16))
DF_TRYPARSE(uint32_t, u32, false, df_np_unsigned(&r, 32))
DF_TRYPARSE(uint64_t, u64, false, df_np_unsigned(&r, 64))
DF_TRYPARSE(float, r32, true, df_np_real(&r))
DF_TRYPARSE(double, r64, true, df_np_real(&r))


""";
}
