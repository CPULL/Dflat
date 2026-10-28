/* the C side of the externc test (spec 15.9) */
#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

typedef struct { int32_t total; } counter;

void* counter_create(void) { return calloc(1, sizeof(counter)); }
void counter_add(void* c, int32_t n) { ((counter*)c)->total += n; }
int32_t counter_total(void* c) { return ((counter*)c)->total; }
void counter_free(void* c) { free(c); }
int64_t text_bytes(const char* text) { return (int64_t)strlen(text); }
uint32_t bytes_sum(uint8_t* data, size_t len) { uint32_t s = 0; for (size_t i = 0; i < len; i++) s += data[i]; return s; }
void bytes_fill(uint8_t* data, size_t len, uint8_t value) { for (size_t i = 0; i < len; i++) data[i] = value; }
double halve(double x) { return x / 2; }
bool is_even(int32_t n) { return n % 2 == 0; }
void* nothing(void) { return NULL; }
