namespace dfcompiler;

// C runtime for the members of date and time (spec 3.3), added to a program only when it uses them.
// A date is milliseconds since 1970-01-01 UTC (null = minimum i64); a time is milliseconds since midnight (null = -1).
public static class RuntimeDate {
  public const string Source = """
/* ---- dates and times (spec 3.3) ---- */

#define DF_NULL_DATE INT64_MIN
#define DF_NULL_TIME (-1)
#define DF_DAY_MS 86400000LL

static int64_t df_floordiv(int64_t a, int64_t b) {
  int64_t q = a / b;
  if ((a % b != 0) && ((a < 0) != (b < 0))) {
    q--;
  }
  return q;
}

static int64_t df_floormod(int64_t a, int64_t b) {
  return a - df_floordiv(a, b) * b;
}

static int64_t df_days_from_civil(int64_t y, int64_t m, int64_t d) {
  y -= m <= 2;
  int64_t era = (y >= 0 ? y : y - 399) / 400;
  int64_t yoe = y - era * 400;
  int64_t doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1;
  int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
  return era * 146097 + doe - 719468;
}

static void df_civil_from_days(int64_t z, int64_t* year, int64_t* month, int64_t* day) {
  z += 719468;
  int64_t era = (z >= 0 ? z : z - 146096) / 146097;
  int64_t doe = z - era * 146097;
  int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  int64_t y = yoe + era * 400;
  int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  int64_t mp = (5 * doy + 2) / 153;
  *day = doy - (153 * mp + 2) / 5 + 1;
  *month = mp < 10 ? mp + 3 : mp - 9;
  *year = y + (*month <= 2);
}

static bool df_is_leap(int64_t y) {
  return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
}

static int64_t df_month_days(int64_t y, int64_t m) {
  static const int64_t days[] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
  return (m == 2 && df_is_leap(y)) ? 29 : days[m - 1];
}

typedef struct {
  int64_t year, month, day, hour, minute, second, ms;
  int64_t dow;   /* 0 = Monday ... 6 = Sunday */
  int64_t doy;   /* 1 to 366 */
} df_dparts;

static df_dparts df_date_parts(int64_t ms) {
  df_dparts p;
  int64_t days = df_floordiv(ms, DF_DAY_MS);
  int64_t rem = ms - days * DF_DAY_MS;
  df_civil_from_days(days, &p.year, &p.month, &p.day);
  p.hour = rem / 3600000;
  p.minute = rem / 60000 % 60;
  p.second = rem / 1000 % 60;
  p.ms = rem % 1000;
  p.dow = df_floormod(days + 3, 7);   /* 1970-01-01 was a Thursday */
  p.doy = days - df_days_from_civil(p.year, 1, 1) + 1;
  return p;
}

/* 0 year, 1 month, 2 day, 3 hour, 4 minute, 5 second, 6 millisecond, 7 day of week (Monday = 0), 8 day of year; 0 for null */
static int64_t df_date_part(int64_t ms, int32_t which) {
  if (ms == DF_NULL_DATE) {
    return 0;
  }
  df_dparts p = df_date_parts(ms);
  switch (which) {
    case 0: return p.year;
    case 1: return p.month;
    case 2: return p.day;
    case 3: return p.hour;
    case 4: return p.minute;
    case 5: return p.second;
    case 6: return p.ms;
    case 7: return p.dow;
    default: return p.doy;
  }
}

static int32_t df_date_timeofday(int64_t ms) {
  return ms == DF_NULL_DATE ? DF_NULL_TIME : (int32_t)df_floormod(ms, DF_DAY_MS);
}

/* 0 year, 1 month, 2 day, 3 hour, 4 minute, 5 second, 6 millisecond */
static int32_t df_time_part(int32_t t, int32_t which) {
  if (t < 0) {
    return 0;
  }
  switch (which) {
    case 3: return t / 3600000;
    case 4: return t / 60000 % 60;
    case 5: return t / 1000 % 60;
    default: return t % 1000;
  }
}

static int64_t df_date_from_i64(int64_t v) {
  return v;
}

/* n units of 'unit' milliseconds later; null when it does not fit */
static int64_t df_date_add(int64_t ms, int64_t n, int64_t unit) {
  if (ms == DF_NULL_DATE) {
    return DF_NULL_DATE;
  }
  int64_t shift;
  int64_t r;
  if (__builtin_mul_overflow(n, unit, &shift) || __builtin_add_overflow(ms, shift, &r) || r == DF_NULL_DATE) {
    return DF_NULL_DATE;
  }
  return r;
}

static int64_t df_date_add_months(int64_t ms, int64_t n) {
  if (ms == DF_NULL_DATE) {
    return DF_NULL_DATE;
  }
  df_dparts p = df_date_parts(ms);
  int64_t total = p.year * 12 + (p.month - 1) + n;
  int64_t y = df_floordiv(total, 12);
  int64_t m = df_floormod(total, 12) + 1;
  int64_t d = p.day < df_month_days(y, m) ? p.day : df_month_days(y, m);
  int64_t tod = df_floormod(ms, DF_DAY_MS);
  return df_date_add(tod, df_days_from_civil(y, m, d), DF_DAY_MS);
}

static int64_t df_date_add_years(int64_t ms, int64_t n) {
  if (n > INT64_MAX / 12 || n < INT64_MIN / 12) {
    return DF_NULL_DATE;
  }
  return df_date_add_months(ms, n * 12);
}

static int64_t df_date_startofday(int64_t ms) {
  return ms == DF_NULL_DATE ? DF_NULL_DATE : df_floordiv(ms, DF_DAY_MS) * DF_DAY_MS;
}

static int64_t df_date_plus_time(int64_t d, int32_t t) {
  return (d == DF_NULL_DATE || t < 0) ? DF_NULL_DATE : df_date_add(d, t, 1);
}

static int64_t df_date_minus_time(int64_t d, int32_t t) {
  return (d == DF_NULL_DATE || t < 0) ? DF_NULL_DATE : df_date_add(d, -(int64_t)t, 1);
}

/* time - time: a date lapse in milliseconds (spec 3.4) */
static int64_t df_time_diff(int32_t a, int32_t b) {
  if (a < 0 || b < 0) {
    return 0;
  }
  return ((int64_t)a - (int64_t)b) * 8 + 2;
}

/* wraps around midnight */
static int32_t df_time_add(int32_t t, int64_t n, int64_t unit) {
  if (t < 0) {
    return DF_NULL_TIME;
  }
  int64_t per_day = DF_DAY_MS / unit;
  int64_t step = n % per_day;
  return (int32_t)df_floormod((int64_t)t + step * unit, DF_DAY_MS);
}

/* null plus a silent OutOfRangeCastException when it is not a time of day */
static int32_t df_time_from_i64(int64_t v) {
  if (v < 0 || v >= DF_DAY_MS) {
    df_silent_note(&df_desc_OutOfRangeCastException);
    return DF_NULL_TIME;
  }
  return (int32_t)v;
}

static int32_t df_time_from_i32(int32_t v) {
  return df_time_from_i64(v);
}

static df_string df_str_from_time(int32_t t) {
  if (t < 0) {
    return df_str_lit("null", 4);
  }
  char buf[32];
  snprintf(buf, sizeof buf, "%02d:%02d:%02d.%03d", t / 3600000, t / 60000 % 60, t / 1000 % 60, t % 1000);
  return df_str_from_buf(buf);
}

/* ---- parsing ---- */

typedef struct { const char* p; uint64_t len; uint64_t i; } df_cur;

static bool df_cur_digit(const df_cur* c) {
  return c->i < c->len && c->p[c->i] >= '0' && c->p[c->i] <= '9';
}

/* between minD and maxD digits */
static bool df_cur_num(df_cur* c, int minD, int maxD, int64_t* out) {
  int n = 0;
  int64_t v = 0;
  while (n < maxD && df_cur_digit(c)) {
    v = v * 10 + (c->p[c->i] - '0');
    c->i++;
    n++;
  }
  if (n < minD) {
    return false;
  }
  *out = v;
  return true;
}

static bool df_cur_char(df_cur* c, char ch) {
  if (c->i < c->len && c->p[c->i] == ch) {
    c->i++;
    return true;
  }
  return false;
}

static bool df_cur_end(const df_cur* c) {
  return c->i >= c->len;
}

static char df_lower_ascii(char c) {
  return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
}

/* a word, ASCII case-insensitive */
static bool df_cur_word(df_cur* c, const char* word, uint64_t n) {
  if (c->i + n > c->len) {
    return false;
  }
  for (uint64_t k = 0; k < n; k++) {
    if (df_lower_ascii(c->p[c->i + k]) != df_lower_ascii(word[k])) {
      return false;
    }
  }
  c->i += n;
  return true;
}

static const char* const df_month_names[] = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
static const char* const df_day_names[] = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

/* index of the name at the cursor (full, or its first three letters), or -1 */
static int df_cur_name(df_cur* c, const char* const* names, int count, bool abbreviated) {
  for (int k = 0; k < count; k++) {
    uint64_t n = abbreviated ? 3 : strlen(names[k]);
    df_cur copy = *c;
    if (df_cur_word(&copy, names[k], n)) {
      *c = copy;
      return k;
    }
  }
  return -1;
}

typedef struct {
  int64_t year, month, day, hour, minute, second, ms;
  int64_t offset;     /* minutes east of UTC */
  int pm;             /* -1 none, 0 am, 1 pm */
  bool twelve;        /* the hour came from h */
} df_dfields;

static void df_dfields_init(df_dfields* f) {
  f->year = 1970;
  f->month = 1;
  f->day = 1;
  f->hour = f->minute = f->second = f->ms = 0;
  f->offset = 0;
  f->pm = -1;
  f->twelve = false;
}

/* milliseconds of the fields as a date; false when a field is out of range */
static bool df_dfields_date(const df_dfields* f, int64_t* out) {
  int64_t hour = f->hour;
  if (f->twelve) {
    if (hour < 1 || hour > 12) {
      return false;
    }
    hour = hour % 12 + (f->pm == 1 ? 12 : 0);
  } else if (f->pm >= 0 && hour <= 12 && hour >= 1) {
    hour = hour % 12 + (f->pm == 1 ? 12 : 0);
  }
  if (f->month < 1 || f->month > 12 || f->day < 1 || f->day > df_month_days(f->year, f->month)) {
    return false;
  }
  if (hour > 23 || f->minute > 59 || f->second > 59) {
    return false;
  }
  int64_t ms = df_days_from_civil(f->year, f->month, f->day) * DF_DAY_MS
    + hour * 3600000 + f->minute * 60000 + f->second * 1000 + f->ms - f->offset * 60000;
  *out = ms;
  return true;
}

/* .fff...: the first three digits are kept */
static bool df_cur_fraction(df_cur* c, int64_t* ms) {
  int n = 0;
  int64_t v = 0;
  if (!df_cur_digit(c)) {
    return false;
  }
  while (df_cur_digit(c)) {
    if (n < 3) {
      v = v * 10 + (c->p[c->i] - '0');
      n++;
    }
    c->i++;
  }
  while (n < 3) {
    v *= 10;
    n++;
  }
  *ms = v;
  return true;
}

/* HH[:mm[:ss[.fff]]] or h[:mm[:ss[.fff]]] am/pm */
static bool df_cur_clock(df_cur* c, df_dfields* f, bool allowAmPm) {
  if (!df_cur_num(c, 1, 2, &f->hour)) {
    return false;
  }
  if (df_cur_char(c, ':')) {
    if (!df_cur_num(c, 2, 2, &f->minute)) {
      return false;
    }
    if (df_cur_char(c, ':')) {
      if (!df_cur_num(c, 2, 2, &f->second)) {
        return false;
      }
      if (df_cur_char(c, '.') && !df_cur_fraction(c, &f->ms)) {
        return false;
      }
    }
  }
  if (allowAmPm) {
    df_cur copy = *c;
    while (df_cur_char(&copy, ' ')) {
    }
    if (df_cur_word(&copy, "am", 2)) {
      f->pm = 0;
      f->twelve = true;
      *c = copy;
    } else if (df_cur_word(&copy, "pm", 2)) {
      f->pm = 1;
      f->twelve = true;
      *c = copy;
    }
  }
  return true;
}

static void df_cur_trim(df_cur* c) {
  while (c->len > c->i && (c->p[c->len - 1] == ' ' || c->p[c->len - 1] == '\t' || c->p[c->len - 1] == '\n' || c->p[c->len - 1] == '\r')) {
    c->len--;
  }
  while (c->i < c->len && (c->p[c->i] == ' ' || c->p[c->i] == '\t' || c->p[c->i] == '\n' || c->p[c->i] == '\r')) {
    c->i++;
  }
}

/* yyyy-MM-dd or yyyy/MM/dd, then optionally ' ' or 'T' and HH[:mm[:ss[.fff]]], then optionally Z or +hh[:mm] */
static bool df_date_parse_default(df_string text, int64_t* out) {
  df_sv tv = df_view(text);
  df_cur c = { tv.p, tv.len, 0 };
  df_dfields f;
  df_dfields_init(&f);
  df_cur_trim(&c);
  if (!df_cur_num(&c, 4, 4, &f.year)) {
    return false;
  }
  char sep = c.i < c.len ? c.p[c.i] : 0;
  if ((sep != '-' && sep != '/') || !df_cur_char(&c, sep)) {
    return false;
  }
  if (!df_cur_num(&c, 1, 2, &f.month) || !df_cur_char(&c, sep) || !df_cur_num(&c, 1, 2, &f.day)) {
    return false;
  }
  if (!df_cur_end(&c)) {
    if (!df_cur_char(&c, 'T') && !df_cur_char(&c, ' ')) {
      return false;
    }
    if (!df_cur_clock(&c, &f, false)) {
      return false;
    }
    if (df_cur_char(&c, 'Z')) {
      f.offset = 0;
    } else if (c.i < c.len && (c.p[c.i] == '+' || c.p[c.i] == '-')) {
      int sign = c.p[c.i] == '-' ? -1 : 1;
      c.i++;
      int64_t hh;
      int64_t mm = 0;
      if (!df_cur_num(&c, 2, 2, &hh)) {
        return false;
      }
      if (df_cur_char(&c, ':')) {
        if (!df_cur_num(&c, 2, 2, &mm)) {
          return false;
        }
      } else if (df_cur_digit(&c) && !df_cur_num(&c, 2, 2, &mm)) {
        return false;
      }
      f.offset = sign * (hh * 60 + mm);
    }
    if (!df_cur_end(&c)) {
      return false;
    }
  }
  return df_dfields_date(&f, out);
}

static bool df_time_parse_default(df_string text, int32_t* out) {
  df_sv tv = df_view(text);
  df_cur c = { tv.p, tv.len, 0 };
  df_dfields f;
  df_dfields_init(&f);
  df_cur_trim(&c);
  if (!df_cur_clock(&c, &f, true) || !df_cur_end(&c)) {
    return false;
  }
  int64_t ms;
  if (!df_dfields_date(&f, &ms)) {
    return false;
  }
  *out = (int32_t)(ms - df_days_from_civil(1970, 1, 1) * DF_DAY_MS);
  return true;
}

/* date(string): null plus a silent InvalidDateTimeFormatException when it does not parse */
static int64_t df_date_from_str(df_string text) {
  int64_t v;
  if (df_date_parse_default(text, &v)) {
    return v;
  }
  df_silent_note(&df_desc_InvalidDateTimeFormatException);
  return DF_NULL_DATE;
}

static int32_t df_time_from_str(df_string text) {
  int32_t v;
  if (df_time_parse_default(text, &v)) {
    return v;
  }
  df_silent_note(&df_desc_InvalidDateTimeFormatException);
  return DF_NULL_TIME;
}

/* strict parsing with the printing letters; nothing is skipped */
static bool df_parse_format(df_string text, df_string fmtText, bool isTime, int64_t* out) {
  df_sv fmt = df_view(fmtText);
  df_sv tv = df_view(text);
  df_cur c = { tv.p, tv.len, 0 };
  df_dfields f;
  df_dfields_init(&f);
  uint64_t i = 0;
  while (i < fmt.len) {
    uint32_t ch = df_utf8_next(fmt.p, fmt.len, &i);
    if (ch == '\'' || ch == '"') {
      uint32_t quote = ch;
      while (i < fmt.len) {
        ch = df_utf8_next(fmt.p, fmt.len, &i);
        if (ch == quote) {
          break;
        }
        char buf[4];
        int n = df_utf8_encode(ch, buf);
        if (!df_cur_word(&c, buf, (uint64_t)n)) {
          return false;
        }
      }
      continue;
    }
    if (ch == '\\' && i < fmt.len) {
      ch = df_utf8_next(fmt.p, fmt.len, &i);
    } else if (ch == 'a' && i + 1 < fmt.len && (fmt.p[i] == '<' || fmt.p[i] == '>' || fmt.p[i] == '^') && fmt.p[i + 1] >= '0' && fmt.p[i + 1] <= '9') {
      i++;
      while (i < fmt.len && fmt.p[i] >= '0' && fmt.p[i] <= '9') {
        i++;
      }
      continue;
    } else if (ch > 0 && ch < 128 && strchr("dfFghHkmMstyzwqirej", (int)ch) != NULL) {
      int n = 1;
      while (i < fmt.len && (uint32_t)(unsigned char)fmt.p[i] == ch) {
        i++;
        n++;
      }
      int64_t v;
      switch (ch) {
        case 'y':
          if (n == 1) {
            if (!df_cur_num(&c, 1, 2, &v)) return false;
            f.year = v < 30 ? 2000 + v : 1900 + v;
          } else if (n == 2) {
            if (!df_cur_num(&c, 2, 2, &v)) return false;
            f.year = v < 30 ? 2000 + v : 1900 + v;
          } else {
            if (!df_cur_num(&c, n < 4 ? 4 : n, n < 4 ? 4 : n, &v)) return false;
            f.year = v;
          }
          break;
        case 'M':
          if (n >= 3) {
            int k = df_cur_name(&c, df_month_names, 12, n == 3);
            if (k < 0) return false;
            f.month = k + 1;
          } else if (!df_cur_num(&c, n, 2, &f.month)) {
            return false;
          }
          break;
        case 'd':
          if (n >= 3) {
            if (df_cur_name(&c, df_day_names, 7, n == 3) < 0) return false;
          } else if (!df_cur_num(&c, n, 2, &f.day)) {
            return false;
          }
          break;
        case 'H':
        case 'k':
          if (!df_cur_num(&c, n, 2, &f.hour)) return false;
          if (ch == 'k' && f.hour == 24) f.hour = 0;
          break;
        case 'h':
          if (!df_cur_num(&c, n, 2, &f.hour)) return false;
          f.twelve = true;
          break;
        case 'm':
          if (!df_cur_num(&c, n, 2, &f.minute)) return false;
          break;
        case 's':
          if (!df_cur_num(&c, n, 2, &f.second)) return false;
          break;
        case 'f': {
          int64_t frac = 0;
          int digits = 0;
          while (digits < n && df_cur_digit(&c)) {
            if (digits < 3) frac = frac * 10 + (c.p[c.i] - '0');
            c.i++;
            digits++;
          }
          if (digits < n) return false;
          for (int k = digits < 3 ? digits : 3; k < 3; k++) frac *= 10;
          f.ms = frac;
          break;
        }
        case 'F': {
          int64_t frac = 0;
          int digits = 0;
          while (digits < n && df_cur_digit(&c)) {
            if (digits < 3) frac = frac * 10 + (c.p[c.i] - '0');
            c.i++;
            digits++;
          }
          for (int k = digits < 3 ? digits : 3; k < 3; k++) frac *= 10;
          f.ms = frac;
          break;
        }
        case 't':
          if (n == 1) {
            if (df_cur_word(&c, "a", 1)) f.pm = 0;
            else if (df_cur_word(&c, "p", 1)) f.pm = 1;
            else return false;
          } else {
            if (df_cur_word(&c, "am", 2)) f.pm = 0;
            else if (df_cur_word(&c, "pm", 2)) f.pm = 1;
            else return false;
          }
          break;
        case 'z': {
          int sign;
          if (df_cur_char(&c, '+')) sign = 1;
          else if (df_cur_char(&c, '-')) sign = -1;
          else return false;
          int64_t hh;
          int64_t mm = 0;
          if (!df_cur_num(&c, n == 2 || n == 3 ? 2 : 1, 2, &hh)) return false;
          if (n >= 3 && (!df_cur_char(&c, ':') || !df_cur_num(&c, 2, 2, &mm))) return false;
          f.offset = sign * (hh * 60 + mm);
          break;
        }
        case 'g':
          if (!df_cur_word(&c, "A.D.", 4)) return false;
          break;
        default:
          return false;   /* week, quarter, ISO and day-of-year letters are printed only */
      }
      continue;
    }
    char buf[4];
    int n = df_utf8_encode(ch, buf);
    if (!df_cur_word(&c, buf, (uint64_t)n)) {
      return false;
    }
  }
  if (!df_cur_end(&c)) {
    return false;
  }
  int64_t ms;
  if (!df_dfields_date(&f, &ms)) {
    return false;
  }
  *out = isTime ? df_floormod(ms, DF_DAY_MS) : ms;
  return true;
}

/* parse never raises: failure gives the default */
static int64_t df_date_parse(df_string text, int64_t def) {
  int64_t v;
  return df_date_parse_default(text, &v) ? v : def;
}

static int64_t df_date_parse_f(df_string text, int64_t def, df_string fmt) {
  int64_t v;
  return df_parse_format(text, fmt, false, &v) ? v : def;
}

static int64_t df_date_parse_fmt(df_string text, df_string fmt) {
  return df_date_parse_f(text, DF_NULL_DATE, fmt);
}

static int32_t df_time_parse(df_string text, int32_t def) {
  int32_t v;
  return df_time_parse_default(text, &v) ? v : def;
}

static int32_t df_time_parse_f(df_string text, int32_t def, df_string fmt) {
  int64_t v;
  return df_parse_format(text, fmt, true, &v) ? (int32_t)v : def;
}

static int32_t df_time_parse_fmt(df_string text, df_string fmt) {
  return df_time_parse_f(text, DF_NULL_TIME, fmt);
}

/* ---- formatting (spec 8.4) ---- */

static void df_sb_num(df_sb* b, int64_t value, int width, char fill) {
  char buf[32];
  int n = snprintf(buf, sizeof buf, "%lld", (long long)(value < 0 ? -value : value));
  if (value < 0) {
    df_sb_bytes(b, "-", 1);
  }
  for (int k = n; k < width; k++) {
    df_sb_bytes(b, &fill, 1);
  }
  df_sb_bytes(b, buf, (uint64_t)n);
}

static void df_sb_text(df_sb* b, const char* text) {
  df_sb_bytes(b, text, strlen(text));
}

static int64_t df_iso_p(int64_t y) {
  return df_floormod(y + df_floordiv(y, 4) - df_floordiv(y, 100) + df_floordiv(y, 400), 7);
}

static int64_t df_iso_weeks(int64_t y) {
  return 52 + ((df_iso_p(y) == 4 || df_iso_p(y - 1) == 3) ? 1 : 0);
}

static void df_iso_week(const df_dparts* p, int64_t* week, int64_t* year) {
  int64_t w = (p->doy - (p->dow + 1) + 10) / 7;
  int64_t y = p->year;
  if (w < 1) {
    y--;
    w = df_iso_weeks(y);
  } else if (w > df_iso_weeks(y)) {
    w = 1;
    y++;
  }
  *week = w;
  *year = y;
}

static df_string df_date_format(int64_t ms, df_string fmtText) {
  df_sv fmt = df_view(fmtText);
  if (ms == DF_NULL_DATE) {
    return df_str_lit("null", 4);
  }
  df_dparts p = df_date_parts(ms);
  df_sb b = { 0 };
  char align = 0;
  uint32_t width = 0;
  uint64_t i = 0;
  while (i < fmt.len) {
    uint32_t ch = df_utf8_next(fmt.p, fmt.len, &i);
    if (ch == '\'' || ch == '"') {
      uint32_t quote = ch;
      while (i < fmt.len) {
        ch = df_utf8_next(fmt.p, fmt.len, &i);
        if (ch == quote) {
          break;
        }
        df_sb_cp(&b, ch);
      }
      continue;
    }
    if (ch == '\\') {
      if (i < fmt.len) {
        df_sb_cp(&b, df_utf8_next(fmt.p, fmt.len, &i));
      }
      continue;
    }
    if (ch == 'a' && i + 1 < fmt.len && (fmt.p[i] == '<' || fmt.p[i] == '>' || fmt.p[i] == '^') && fmt.p[i + 1] >= '0' && fmt.p[i + 1] <= '9') {
      align = fmt.p[i];
      i++;
      width = 0;
      while (i < fmt.len && fmt.p[i] >= '0' && fmt.p[i] <= '9') {
        width = width * 10 + (uint32_t)(fmt.p[i] - '0');
        i++;
      }
      continue;
    }
    if (ch == 0 || ch >= 128 || strchr("dfFghHkmMstyzwqirej", (int)ch) == NULL) {
      df_sb_cp(&b, ch);
      continue;
    }
    int n = 1;
    while (i < fmt.len && (uint32_t)(unsigned char)fmt.p[i] == ch) {
      i++;
      n++;
    }
    int64_t hour12 = p.hour % 12 == 0 ? 12 : p.hour % 12;
    switch (ch) {
      case 'd':
        if (n == 1) df_sb_num(&b, p.day, 1, '0');
        else if (n == 2) df_sb_num(&b, p.day, 2, '0');
        else if (n == 3) df_sb_bytes(&b, df_day_names[p.dow], 3);
        else df_sb_text(&b, df_day_names[p.dow]);
        break;
      case 'f':
      case 'F': {
        char digits[8];
        snprintf(digits, sizeof digits, "%03d", (int)p.ms);
        int keep = n < 3 ? n : 3;
        int count = keep;
        if (ch == 'F') {
          while (count > 0 && digits[count - 1] == '0') count--;
        }
        df_sb_bytes(&b, digits, (uint64_t)count);
        if (ch == 'f') {
          for (int k = 3; k < n; k++) df_sb_bytes(&b, "0", 1);
        }
        break;
      }
      case 'g':
        df_sb_text(&b, "A.D.");
        break;
      case 'h':
        df_sb_num(&b, hour12, n == 1 ? 1 : 2, '0');
        break;
      case 'H':
        df_sb_num(&b, p.hour, n == 1 ? 1 : 2, '0');
        break;
      case 'k':
        df_sb_num(&b, p.hour == 0 ? 24 : p.hour, n == 1 ? 1 : 2, '0');
        break;
      case 'm':
        df_sb_num(&b, p.minute, n == 1 ? 1 : 2, '0');
        break;
      case 's':
        df_sb_num(&b, p.second, n == 1 ? 1 : 2, '0');
        break;
      case 'M':
        if (n == 1) df_sb_num(&b, p.month, 1, '0');
        else if (n == 2) df_sb_num(&b, p.month, 2, '0');
        else if (n == 3) df_sb_bytes(&b, df_month_names[p.month - 1], 3);
        else df_sb_text(&b, df_month_names[p.month - 1]);
        break;
      case 't':
        if (n == 1) df_sb_text(&b, p.hour < 12 ? "A" : "P");
        else df_sb_text(&b, p.hour < 12 ? "AM" : "PM");
        break;
      case 'y':
        if (n == 1) df_sb_num(&b, df_floormod(p.year, 100), 1, '0');
        else if (n == 2) df_sb_num(&b, df_floormod(p.year, 100), 2, '0');
        else df_sb_num(&b, p.year, n < 4 ? n : n, '0');
        break;
      case 'z':
        df_sb_text(&b, n == 1 ? "+0" : n == 2 ? "+00" : "+00:00");
        break;
      case 'w':
        df_sb_num(&b, (p.doy - 1) / 7 + 1, n == 1 ? 1 : 2, n == 1 ? '0' : ' ');
        break;
      case 'q':
        df_sb_num(&b, (p.month - 1) / 3 + 1, 1, '0');
        break;
      case 'i': {
        int64_t week, year;
        df_iso_week(&p, &week, &year);
        df_sb_num(&b, week, n == 1 ? 1 : 2, n == 1 ? '0' : ' ');
        break;
      }
      case 'r': {
        int64_t week, year;
        df_iso_week(&p, &week, &year);
        df_sb_num(&b, year, n == 1 ? 1 : 4, n == 1 ? '0' : ' ');
        break;
      }
      case 'e':
        df_sb_num(&b, p.doy, 3, n == 1 ? '0' : ' ');
        break;
      case 'j':
        df_sb_num(&b, p.dow + 1, 1, '0');
        break;
    }
  }
  df_string out = df_sb_done(&b);
  uint64_t chars = df_str_chars(out);
  if (align && width > chars) {
    uint64_t gap = width - chars;
    uint64_t left = align == '>' ? gap : align == '^' ? gap / 2 : 0;
    df_sb padded = { 0 };
    for (uint64_t k = 0; k < left; k++) df_sb_bytes(&padded, " ", 1);
    df_sv ov = df_view(out);
    df_sb_bytes(&padded, ov.p, ov.len);
    for (uint64_t k = left; k < gap; k++) df_sb_bytes(&padded, " ", 1);
    out = df_sb_done(&padded);
  }
  return out;
}

static df_string df_time_format(int32_t t, df_string fmt) {
  if (t < 0) {
    return df_str_lit("null", 4);
  }
  return df_date_format((int64_t)t, fmt);
}

""";
}
