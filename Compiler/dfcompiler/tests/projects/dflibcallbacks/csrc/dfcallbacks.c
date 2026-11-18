/* a tiny C library for the tests of callbacks (spec 22): C functions that call back the function they are given.
   No #include, so it builds for every target. Build: csrc/build.ps1 (static libraries in libs/<target>/) */
#if defined(__i386__)
#define DFC_STDCALL __attribute__((stdcall))
#else
#define DFC_STDCALL
#endif

typedef struct { float x; float y; } dfc_point;

/* cdecl: two ints in, one out */
int dfc_apply(int (*fn)(int, int), int a, int b) {
  return fn(a, b);
}

/* stdcall: a C text and a value; C stops when the callback returns 0, and returns how many it visited */
int dfc_each(long long (DFC_STDCALL *fn)(const char*, long long), long long data) {
  static const char* names[] = { "alpha", "beta", "gamma" };
  int visited = 0;
  for (int i = 0; i < 3; i++) {
    visited++;
    if (fn(names[i], data + i) == 0) {
      break;
    }
  }
  return visited;
}

/* no result */
void dfc_notify(void (*fn)(double), double value) {
  fn(value);
  fn(value * 2);
}

/* a struct by value */
float dfc_measure(float (*fn)(dfc_point), float x, float y) {
  dfc_point p = { x, y };
  return fn(p);
}
