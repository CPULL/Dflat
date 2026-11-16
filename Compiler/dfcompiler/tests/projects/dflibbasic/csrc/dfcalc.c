/* a tiny C library for the tests of import lib (spec 22); no #include, so it builds for every target.
   Build: csrc/build.ps1 (static libraries in libs/<target>/) */
#ifdef _WIN32
#define DFC_API __declspec(dllexport)
#else
#define DFC_API
#endif

#if defined(__i386__)
#define DFC_STDCALL __attribute__((stdcall))
#else
#define DFC_STDCALL
#endif

DFC_API int dfc_add(int a, int b) { return a + b; }

DFC_API int DFC_STDCALL dfc_mul(int a, int b) { return a * b; }

DFC_API int dfc_sum(const int* values, unsigned long long count) {
  int total = 0;
  for (unsigned long long i = 0; i < count; i++) {
    total += values[i];
  }
  return total;
}

DFC_API const char* dfc_name(void) { return "dfcalc"; }

DFC_API int dfc_div(int a, int b, int* rest) {
  *rest = a % b;
  return a / b;
}
