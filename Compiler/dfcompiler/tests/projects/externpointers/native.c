/* the C side of the externpointers test (spec 12.1, 22): pointers cross as the cursor only */
#include <stdint.h>
#include <stddef.h>

typedef struct { const uint8_t* nextIn; uint32_t availIn; uint8_t* nextOut; uint32_t availOut; } Stream;

static int32_t table[4] = { 7, 8, 9, 10 };

int64_t sum_items(const int32_t* p, int32_t n) { int64_t s = 0; for (int32_t i = 0; i < n; i++) s += p[i]; return s; }
void fill_items(int32_t* p, int32_t n, int32_t v) { for (int32_t i = 0; i < n; i++) p[i] = v + i; }
int32_t* get_table(void) { return table; }
int32_t* get_nothing(void) { return NULL; }
int32_t is_null(const int32_t* p) { return p == NULL; }
/* copies the input to the output in upper case, as a tiny "compressor" with z_stream-like fields */
int32_t stream_run(Stream* s) {
  uint32_t n = 0;
  while (s->availIn > 0 && s->availOut > 0) {
    uint8_t c = *s->nextIn++;
    *s->nextOut++ = (c >= 'a' && c <= 'z') ? (uint8_t)(c - 32) : c;
    s->availIn--; s->availOut--; n++;
  }
  return (int32_t)n;
}
