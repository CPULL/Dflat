/* a tiny C library for the tests of variadic natives (spec 22); no #include (clang's built-in va_list), so it builds for every target.
   Build: csrc/build.ps1 (static libraries in libs/<target>/) */
#ifdef _WIN32
#define DFV_API __declspec(dllexport)
#else
#define DFV_API
#endif

static char text[256];
static int pos;

static void put(char c) {
  if (pos < 255) {
    text[pos++] = c;
  }
}

static void putnum(long long v) {
  char digits[24];
  int n = 0;
  if (v < 0) {
    put('-');
    v = -v;
  }
  do {
    digits[n++] = (char)('0' + v % 10);
    v /= 10;
  } while (v > 0);
  while (n > 0) {
    put(digits[--n]);
  }
}

/* each letter of kinds says the type of the next argument: i int, l long long, u unsigned long long, d double, s text, p pointer */
DFV_API const char* dfv_format(const char* kinds, ...) {
  __builtin_va_list ap;
  __builtin_va_start(ap, kinds);
  pos = 0;
  for (const char* k = kinds; *k; k++) {
    if (k != kinds) {
      put(' ');
    }
    switch (*k) {
      case 'i': putnum(__builtin_va_arg(ap, int)); break;
      case 'l': putnum(__builtin_va_arg(ap, long long)); break;
      case 'u': putnum((long long)__builtin_va_arg(ap, unsigned long long)); break;
      case 'd': {
        double d = __builtin_va_arg(ap, double);
        long long hundredths = (long long)(d * 100 + (d < 0 ? -0.5 : 0.5));
        if (hundredths < 0) {
          put('-');
          hundredths = -hundredths;
        }
        putnum(hundredths / 100);
        put('.');
        put((char)('0' + hundredths % 100 / 10));
        put((char)('0' + hundredths % 10));
        break;
      }
      case 's': {
        const char* s = __builtin_va_arg(ap, const char*);
        while (*s) {
          put(*s++);
        }
        break;
      }
      case 'p': put(__builtin_va_arg(ap, void*) ? 'P' : '0'); break;
      default: put('?');
    }
  }
  __builtin_va_end(ap);
  put(0);
  return text;
}

DFV_API int dfv_sum(int count, ...) {
  __builtin_va_list ap;
  __builtin_va_start(ap, count);
  int total = 0;
  for (int i = 0; i < count; i++) {
    total += __builtin_va_arg(ap, int);
  }
  __builtin_va_end(ap);
  return total;
}
