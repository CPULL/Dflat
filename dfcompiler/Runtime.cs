namespace dfcompiler;

// C runtime embedded in every generated program (standard C library version).
public static class Runtime {
  public const string Source = """
/* ---- Dflat runtime (libc version) ---- */
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#ifdef _WIN32
#include <windows.h>
#endif

typedef struct {
  const char* p;   /* UTF-8 bytes, not NUL-terminated */
  uint64_t len;    /* byte length */
} df_string;

/* ---- panic ---- */

/* critical exit: prints "Panic <code>" on stdout, no cleanup */
static void df_panic(int64_t code) {
  fflush(stdout);
  printf("Panic %lld\n", (long long)code);
  fflush(stdout);
  _Exit((int)code);
}

#define DF_PANIC_UNHANDLED (-1000)

/* out of memory is not catchable yet: message and panic */
static void df_out_of_memory(void) {
  fflush(stdout);
  printf("Out Of Memory Exception\n");
  df_panic(DF_PANIC_UNHANDLED);
}

static void* df_alloc(uint64_t n) {
  void* p = malloc(n ? n : 1);
  if (!p) {
    df_out_of_memory();
  }
  return p;
}

/* ---- exceptions (error register, spec 10.7) ---- */

typedef struct df_descriptor {
  uint64_t id;
  const char* name;
  const struct df_descriptor* parent;
  bool silent;
} df_descriptor;

typedef struct {
  uint64_t id;
  const df_descriptor* descriptor;
  char* message;     /* C-style, max 2 KB */
  char* function;    /* C-style, max 1 KB: minified header of the failing function */
  uint64_t line;     /* 0 in release */
} df_exception;

static const df_descriptor df_desc_Exception = { 10, "Exception", NULL, false };
static const df_descriptor df_desc_NullPointerException = { 11, "NullPointerException", &df_desc_Exception, false };
static const df_descriptor df_desc_OverflowException = { 12, "OverflowException", &df_desc_Exception, true };
static const df_descriptor df_desc_InvalidCastException = { 13, "InvalidCastException", &df_desc_Exception, false };
static const df_descriptor df_desc_DivisionByZeroException = { 14, "DivisionByZeroException", &df_desc_Exception, false };
static const df_descriptor df_desc_OutOfMemoryException = { 15, "OutOfMemoryException", &df_desc_Exception, false };
static const df_descriptor df_desc_ChangingRefCountTypeException = { 16, "ChangingRefCountTypeException", &df_desc_Exception, false };
static const df_descriptor df_desc_OutOfRangeCastException = { 17, "OutOfRangeCastException", &df_desc_Exception, true };
static const df_descriptor df_desc_InvalidCharacterCodePointException = { 18, "InvalidCharacterCodePointException", &df_desc_Exception, true };

/* 0 = no error; otherwise the exception being raised */
static df_exception* df_err = NULL;
/* true when df_err was raised by a throw statement (a throw inside a catch goes to the enclosing level) */
static bool df_err_thrown = false;

/* copies at most maxBytes - 1 bytes plus the terminator, never cutting a UTF-8 character */
static char* df_cstr_truncated(const char* text, uint64_t length, uint64_t maxBytes) {
  uint64_t keep = length;
  if (keep > maxBytes - 1) {
    keep = maxBytes - 1;
    while (keep > 0 && ((unsigned char)text[keep] & 0xC0) == 0x80) {
      keep--;
    }
  }
  char* copy = (char*)df_alloc(keep + 1);
  memcpy(copy, text, keep);
  copy[keep] = 0;
  return copy;
}

static df_exception* df_new_exception(const df_descriptor* descriptor, const char* message, uint64_t messageLength, const char* function) {
  df_exception* exception = (df_exception*)df_alloc(sizeof(df_exception));
  exception->id = descriptor->id;
  exception->descriptor = descriptor;
  exception->message = df_cstr_truncated(message, messageLength, 2048);
  exception->function = df_cstr_truncated(function, strlen(function), 1024);
  exception->line = 0;
  return exception;
}

/* runtime error: the message is the exception name written with spaces */
static void df_raise(const df_descriptor* descriptor, const char* function) {
  char spaced[256];
  uint64_t length = 0;
  for (const char* letter = descriptor->name; *letter && length < sizeof spaced - 2; letter++) {
    if (letter != descriptor->name && *letter >= 'A' && *letter <= 'Z') {
      spaced[length++] = ' ';
    }
    spaced[length++] = *letter;
  }
  spaced[length] = 0;
  df_err = df_new_exception(descriptor, spaced, length, function);
  df_err_thrown = false;
}

/* true when the exception is of that type or a subtype */
static bool df_exc_is(const df_exception* exception, uint64_t id) {
  for (const df_descriptor* descriptor = exception->descriptor; descriptor; descriptor = descriptor->parent) {
    if (descriptor->id == id) {
      return true;
    }
  }
  return false;
}

/* unhandled exception: its message, then Panic -1000 */
static void df_panic_unhandled(void) {
  fflush(stdout);
  printf("%s\n", df_err ? df_err->message : "");
  df_panic(DF_PANIC_UNHANDLED);
}

/* ---- strings (never freed yet: ownership comes later) ---- */

static df_string df_str_lit(const char* p, uint64_t len) {
  df_string s = { p, len };
  return s;
}

static df_string df_str_concat(df_string a, df_string b) {
  char* p = (char*)df_alloc(a.len + b.len);
  memcpy(p, a.p, a.len);
  memcpy(p + a.len, b.p, b.len);
  df_string s = { p, a.len + b.len };
  return s;
}

static df_string df_str_from_buf(const char* buf) {
  uint64_t n = strlen(buf);
  char* p = (char*)df_alloc(n);
  memcpy(p, buf, n);
  df_string s = { p, n };
  return s;
}

static df_string df_str_from_cstr(const char* text) {
  df_string s = { text, strlen(text) };
  return s;
}

static bool df_str_eq(df_string a, df_string b) {
  return a.len == b.len && memcmp(a.p, b.p, a.len) == 0;
}

/* character count (UTF-8 code points) */
static uint64_t df_str_chars(df_string s) {
  uint64_t n = 0;
  for (uint64_t i = 0; i < s.len; i++) {
    if (((unsigned char)s.p[i] & 0xC0) != 0x80) {
      n++;
    }
  }
  return n;
}

static df_string df_str_from_i64(int64_t v) {
  char buf[32];
  snprintf(buf, sizeof buf, "%lld", (long long)v);
  return df_str_from_buf(buf);
}

static df_string df_str_from_u64(uint64_t v) {
  char buf[32];
  snprintf(buf, sizeof buf, "%llu", (unsigned long long)v);
  return df_str_from_buf(buf);
}

static df_string df_str_from_bool(bool v) {
  return v ? df_str_lit("true", 4) : df_str_lit("false", 5);
}

static df_string df_str_from_char(uint32_t c) {
  char buf[4];
  uint64_t n;
  if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
    c = 0;
  }
  if (c < 0x80) {
    buf[0] = (char)c;
    n = 1;
  } else if (c < 0x800) {
    buf[0] = (char)(0xC0 | (c >> 6));
    buf[1] = (char)(0x80 | (c & 0x3F));
    n = 2;
  } else if (c < 0x10000) {
    buf[0] = (char)(0xE0 | (c >> 12));
    buf[1] = (char)(0x80 | ((c >> 6) & 0x3F));
    buf[2] = (char)(0x80 | (c & 0x3F));
    n = 3;
  } else {
    buf[0] = (char)(0xF0 | (c >> 18));
    buf[1] = (char)(0x80 | ((c >> 12) & 0x3F));
    buf[2] = (char)(0x80 | ((c >> 6) & 0x3F));
    buf[3] = (char)(0x80 | (c & 0x3F));
    n = 4;
  }
  char* p = (char*)df_alloc(n);
  memcpy(p, buf, n);
  df_string s = { p, n };
  return s;
}

/* shortest text that reads back as the same value (like C#) */
static df_string df_str_from_real(double v, int maxDigits, bool isFloat) {
  char buf[64];
  if (isnan(v)) {
    return df_str_lit("NaN", 3);
  }
  if (isinf(v)) {
    return v > 0 ? df_str_lit("\xE2\x88\x9E", 3) : df_str_lit("-\xE2\x88\x9E", 4);
  }
  for (int digits = 1; digits <= maxDigits; digits++) {
    snprintf(buf, sizeof buf, "%.*g", digits, v);
    double back = strtod(buf, NULL);
    if (isFloat ? (float)back == (float)v : back == v) {
      break;
    }
  }
  for (char* c = buf; *c; c++) {
    if (*c == 'e') {
      *c = 'E';
    }
  }
  return df_str_from_buf(buf);
}

static df_string df_str_from_r64(double v) {
  return df_str_from_real(v, 17, false);
}

static df_string df_str_from_r32(float v) {
  return df_str_from_real((double)v, 9, true);
}

/* ---- conversions to bool ---- */

static bool df_rbool(double v) {
  return v != 0 && !isnan(v);
}

/* ---- console ---- */

static void df_console_log(df_string s) {
  fwrite(s.p, 1, (size_t)s.len, stdout);
  fputc('\n', stdout);
}

static void df_console_log_error(df_string s) {
  fflush(stdout);
  fwrite(s.p, 1, (size_t)s.len, stderr);
  fputc('\n', stderr);
}

static void df_init(void) {
#ifdef _WIN32
  SetConsoleOutputCP(CP_UTF8);
#endif
}

/* ---- checked integer division ---- */

/* on division by zero: raise DivisionByZeroException and return 0; the caller checks df_err */
#define DF_DIV_SIGNED(T, NAME, MIN) \
  static T df_div_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    if (b == -1) return (T)(0 - (uint64_t)a); \
    return (T)(a / b); \
  } \
  static T df_mod_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    if (b == -1) return 0; \
    return (T)(a % b); \
  }

#define DF_DIV_UNSIGNED(T, NAME) \
  static T df_div_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    return (T)(a / b); \
  } \
  static T df_mod_##NAME(T a, T b, const char* function) { \
    if (b == 0) { df_raise(&df_desc_DivisionByZeroException, function); return 0; } \
    return (T)(a % b); \
  }

DF_DIV_SIGNED(int8_t, i8, INT8_MIN)
DF_DIV_SIGNED(int16_t, i16, INT16_MIN)
DF_DIV_SIGNED(int32_t, i32, INT32_MIN)
DF_DIV_SIGNED(int64_t, i64, INT64_MIN)
DF_DIV_UNSIGNED(uint8_t, u8)
DF_DIV_UNSIGNED(uint16_t, u16)
DF_DIV_UNSIGNED(uint32_t, u32)
DF_DIV_UNSIGNED(uint64_t, u64)

/* ---- saturating casts ---- */

#define DF_SAT_INT(T, NAME, LO, HI) \
  static T df_sat_##NAME##_i(int64_t v) { \
    return v < (int64_t)(LO) ? (T)(LO) : (v > (int64_t)(HI) ? (T)(HI) : (T)v); \
  } \
  static T df_sat_##NAME##_u(uint64_t v) { \
    return v > (uint64_t)(HI) ? (T)(HI) : (T)v; \
  } \
  static T df_sat_##NAME##_r(double v) { \
    if (isnan(v)) return 0; \
    if (v <= (double)(LO)) return (T)(LO); \
    if (v >= (double)(HI)) return (T)(HI); \
    return (T)v; \
  }

DF_SAT_INT(int8_t, i8, INT8_MIN, INT8_MAX)
DF_SAT_INT(int16_t, i16, INT16_MIN, INT16_MAX)
DF_SAT_INT(int32_t, i32, INT32_MIN, INT32_MAX)
DF_SAT_INT(uint8_t, u8, 0, UINT8_MAX)
DF_SAT_INT(uint16_t, u16, 0, UINT16_MAX)
DF_SAT_INT(uint32_t, u32, 0, UINT32_MAX)

static int64_t df_sat_i64_i(int64_t v) { return v; }
static int64_t df_sat_i64_u(uint64_t v) { return v > (uint64_t)INT64_MAX ? INT64_MAX : (int64_t)v; }
static int64_t df_sat_i64_r(double v) {
  if (isnan(v)) return 0;
  if (v <= -9223372036854775808.0) return INT64_MIN;
  if (v >= 9223372036854775807.0) return INT64_MAX;
  return (int64_t)v;
}
static uint64_t df_sat_u64_i(int64_t v) { return v < 0 ? 0 : (uint64_t)v; }
static uint64_t df_sat_u64_u(uint64_t v) { return v; }
static uint64_t df_sat_u64_r(double v) {
  if (isnan(v) || v <= 0) return 0;
  if (v >= 18446744073709551615.0) return UINT64_MAX;
  return (uint64_t)v;
}

/* ---- end of runtime ---- */
""";
}
