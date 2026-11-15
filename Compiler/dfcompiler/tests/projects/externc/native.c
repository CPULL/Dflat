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
const char* greeting(void) { return "h\xc3\xa9llo"; }
char* make_text(int32_t n) { char* t = (char*)malloc(n + 1); memset(t, 'x', n); t[n] = 0; return t; }

/* C strings with an encoding (spec 4.4.6): string(addr, encoding), string(->addr, encoding), string(addr!, encoding) */
const uint16_t* wide_greeting(void) { static const uint16_t w[] = { 'h', 0xE9, 'l', 'l', 'o', ' ', 0xD83D, 0xDE00, 0 }; return w; }
uint16_t* make_wide(int32_t n) { uint16_t* w = (uint16_t*)malloc((n + 1) * 2); for (int32_t i = 0; i < n; i++) w[i] = 'w'; w[n] = 0; return w; }
const uint32_t* utf32_text(void) { static const uint32_t t[] = { 'a', 0x20AC, 0x1F600, 0 }; return t; }
const unsigned char* utf32be_text(void) { static const unsigned char t[] = { 0, 0, 0, 'O', 0, 0, 0, 'K', 0, 0, 0, 0 }; return t; }
const unsigned char* utf16be_text(void) { static const unsigned char t[] = { 0, 'b', 0, 'e', 0, 0 }; return t; }
const char* latin1_text(void) { return "caf\xe9"; }
const char* ascii_text(void) { return "a\xe9z"; }
static char scratch_buf[8] = "aaaaa";
const char* scratch(void) { return scratch_buf; }
void scratch_change(void) { memcpy(scratch_buf, "bbbbb", 5); }
