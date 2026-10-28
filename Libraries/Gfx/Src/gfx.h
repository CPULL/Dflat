#ifndef GFX_H
#define GFX_H

/*
 * Gfx — the C core of the Dflat gfx library (spec Libraries/Gfx/dflat-gfx_spec.md, version 0.0.1).
 * Vulkan 1.1 + GLFW 3.3. The Dflat side calls these functions through `extern fun` (Dflat spec 15.9), so the API takes only
 * numbers, bool, void* (the context), uint64_t (the handles), const char* (UTF-8) and arrays as pointer + length.
 *
 * Handles: every object (window, texture, sprite, texture font, label) is a uint64_t: the generation in the high 32 bits, then the kind
 * (8 bits) and the slot (24 bits). 0 is never a valid handle. A released object leaves its slot with a new generation, so an old handle is
 * stale: gfx_valid() tells, and every other call on a stale handle does nothing and returns 0 / false.
 *
 * Coordinates: top-left origin, +X right, +Y down, one world unit = one pixel of the world. Draw order: sprites and labels together, by
 * increasing order, then creation order.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---- the system (the static class Gfx) ---- */
bool        gfx_is_available(void);                       /* Vulkan and a usable GPU are present */
int32_t     gfx_display_count(void);
int32_t     gfx_display_x(int32_t index);
int32_t     gfx_display_y(int32_t index);
int32_t     gfx_display_width(int32_t index);
int32_t     gfx_display_height(int32_t index);
bool        gfx_display_primary(int32_t index);
int32_t     gfx_display_name(int32_t index, uint8_t* buffer, size_t size);   /* UTF-8 bytes written (no terminator), at most size */

/* ---- the context ---- */
void*       gfx_context_create(void);                     /* NULL when Vulkan or a usable GPU is not present */
void        gfx_context_destroy(void* ctx);               /* releases every object of the context */
bool        gfx_valid(void* ctx, uint64_t handle);
void        gfx_release(void* ctx, uint64_t handle);      /* any kind; a stale handle does nothing */
void        gfx_set_clear_color(void* ctx, float r, float g, float b, float a);
bool        gfx_update(void* ctx);                        /* renders and presents one frame; false when the user closed the window */

/* ---- the window (one per context in this version) ---- */
enum { GFX_RESIZE_NONE, GFX_RESIZE_KEEP_PIXEL_SIZE, GFX_RESIZE_STRETCH, GFX_RESIZE_FIT, GFX_RESIZE_FIT_WIDTH, GFX_RESIZE_FIT_HEIGHT };
uint64_t    gfx_window_create(void* ctx, int32_t width, int32_t height);   /* 0 on failure or when the context has a window */
int32_t     gfx_window_width(void* ctx, uint64_t window);                  /* the pixels of the viewport */
int32_t     gfx_window_height(void* ctx, uint64_t window);
void        gfx_window_set_resize_mode(void* ctx, uint64_t window, int32_t mode);
void        gfx_window_set_integer_scale(void* ctx, uint64_t window, bool integer_scale);

/* ---- textures: RGBA8, rows from the top ---- */
enum { GFX_FORMAT_RGBA8 };
enum { GFX_FILTER_NEAREST, GFX_FILTER_LINEAR };
uint64_t    gfx_texture_create(void* ctx, uint8_t* data, size_t length, int32_t width, int32_t height, int32_t format, int32_t filter);
                                                          /* 0 when width or height is <= 0 or length < width * height * 4 */
bool        gfx_texture_set_data(void* ctx, uint64_t texture, uint8_t* data, size_t length);   /* same rules; false when too short */
void        gfx_texture_set_filter(void* ctx, uint64_t texture, int32_t filter);
int32_t     gfx_texture_width(void* ctx, uint64_t texture);
int32_t     gfx_texture_height(void* ctx, uint64_t texture);

/* ---- sprites ---- */
uint64_t    gfx_sprite_create(void* ctx, uint64_t texture);
void        gfx_sprite_set_texture(void* ctx, uint64_t sprite, uint64_t texture);
/* everything that moves, in one call: position of the pivot, the pivot (offset from the top-left of the unscaled quad, in pixels),
   scale, rotation (radians, around Z), draw order, source rectangle tl / br (normalized 0..1) and visibility */
void        gfx_sprite_set_state(void* ctx, uint64_t sprite, float pos_x, float pos_y, float offset_x, float offset_y,
                                 float scale_x, float scale_y, float rotation, int32_t order,
                                 float tl_x, float tl_y, float br_x, float br_y, bool visible);

/* ---- texture fonts: a grid of fixed-size glyphs for ASCII 32..126, left to right, top to bottom ---- */
uint64_t    gfx_font_create(void* ctx, uint64_t texture, int32_t glyph_width, int32_t glyph_height);

/* ---- labels ---- */
uint64_t    gfx_label_create(void* ctx, uint64_t font, const char* text);
void        gfx_label_set_text(void* ctx, uint64_t label, const char* text);
void        gfx_label_set_font(void* ctx, uint64_t label, uint64_t font);
void        gfx_label_set_state(void* ctx, uint64_t label, float pos_x, float pos_y, float offset_x, float offset_y,
                                float scale_x, float scale_y, float rotation, int32_t order, bool visible,
                                float r, float g, float b, float a);

#ifdef __cplusplus
}
#endif

#endif /* GFX_H */
