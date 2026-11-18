/* a tiny C library for the tests of handles (spec 22): it counts what is opened and freed; no #include, so it builds for every target.
   Build: csrc/build.ps1 (static libraries in libs/<target>/) */
#ifdef _WIN32
#define DFH_API __declspec(dllexport)
#else
#define DFH_API
#endif

static int closed = 0;
static int last = 0;
static int bufsFreed = 0;
static int filesClosed = 0;
static int slots[64];

DFH_API void* dfh_open(int id) {
  if (id <= 0 || id >= 64) {
    return 0;
  }
  slots[id] = id;
  return &slots[id];
}

DFH_API int dfh_id(void* r) { return r == 0 ? -1 : *(int*)r; }

DFH_API int dfh_close(void* r) {
  closed++;
  last = *(int*)r;
  return 100 + last;
}

DFH_API int dfh_closed(void) { return closed; }
DFH_API int dfh_last(void) { return last; }

DFH_API unsigned long long dfh_buf(unsigned long long n) { return n; }
DFH_API void dfh_buf_free(unsigned long long b) { bufsFreed++; last = (int)b; }
DFH_API int dfh_bufs_freed(void) { return bufsFreed; }

DFH_API int dfh_file(int ok) { return ok ? 3 : -1; }
DFH_API void dfh_file_close(int f) { filesClosed++; last = f; }
DFH_API int dfh_files_closed(void) { return filesClosed; }
