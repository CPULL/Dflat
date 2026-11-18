/* a tiny C library for the tests of =out parameters, C variables and target blocks (spec 22); no #include, so it builds for every target.
   Build: csrc/build.ps1 (static libraries in libs/<target>/) */
#ifdef _WIN32
#define DFO_API __declspec(dllexport)
#else
#define DFO_API
#endif

DFO_API int dfo_counter = 5;
DFO_API int dfo_limit = 100;

static int slots[16];
static int closes = 0;

DFO_API int dfo_split(int value, int* half, int* rest) {
  *half = value / 2;
  *rest = value % 2;
  return value > 0;
}

DFO_API int dfo_open(int id, void** out) {
  if (id <= 0 || id >= 16) {
    *out = 0;
    return -1;
  }
  slots[id] = id;
  *out = &slots[id];
  return 0;
}

DFO_API int dfo_close(void* c) {
  closes++;
  return *(int*)c;
}

DFO_API int dfo_closes(void) { return closes; }

DFO_API int dfo_get_magic(int* out) {
  *out = 42 + dfo_limit;
  return 0;
}
