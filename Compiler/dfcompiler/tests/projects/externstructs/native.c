/* the C side of the externstructs test (spec 11.1.5.6) */
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

typedef struct { float x; float y; } Vec2;
typedef struct { uint8_t kind; int32_t ready; double weight; } Fixed;   /* a 4-byte bool, as BOOL in Win32 */
#pragma pack(push, 1)
typedef struct { uint8_t kind; uint32_t size; bool last; } Header;
#pragma pack(pop)
typedef struct { uint32_t id; char name[8]; Vec2 at; } Device;

float vec_len2(Vec2 v) { return v.x * v.x + v.y * v.y; }
void vec_scale(Vec2* v, float k) { v->x *= k; v->y *= k; }
float vec_sum(const Vec2* v) { return v->x + v->y; }
Vec2 vec_make(float x, float y) { Vec2 v = { x, y }; return v; }
int32_t fixed_ready(Fixed f) { return f.ready ? 1 : 0; }
void fixed_set(Fixed* f) { f->ready = 1; f->weight = 2.5; }
uint32_t header_size(Header h) { return h.size + (h.last ? 1000 : 0); }
uint64_t sizes(void) { return sizeof(Vec2) * 1000000 + sizeof(Fixed) * 10000 + sizeof(Header) * 100 + sizeof(Device); }
uint64_t offsets(void) { return offsetof(Fixed, weight) * 10000 + offsetof(Header, size) * 100 + offsetof(Device, at); }
uint32_t device_letters(const Device* d) { uint32_t n = 0; while (n < 8 && d->name[n] != 0) n++; return n; }
