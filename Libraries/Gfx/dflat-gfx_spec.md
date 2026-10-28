# Gfx — Minimal 2D Graphics Library Specification
Version 0.0.1 2026/10/28 by CPU

## 1.  Scope
Gfx is a minimal 2D graphics library for Dflat, backed by Vulkan. It is an optional library, not part of every build.

- A single static class is the entry point; it only creates a context. In this version a context creates only 2D objects; UI and 3D objects may come later in the same context.
- A context is a container for the Vulkan backend. It creates every other object and owns it; destroying the context releases all of them.
- Objects: a window, textures, sprites, texture fonts and labels. User code reaches them through handles.
- Small data is organized with field groups (`pos { r32 x, r32 y }`) and tuples (structs are not defined yet).
- Sprites and labels support position, scale and rotation around an origin offset.
- The view is 2D orthographic, with the origin at the top-left.
- Vulkan is managed entirely inside the library and never exposed.

Out of scope for this version: cameras, input handling, advanced text rendering, render-to-texture and a scene graph.

## 2.  Namespace and Classes

Namespace: `gfx`
Main class: `Gfx` (`gfx.Gfx`)

Other classes:
- `Context`: holds the Vulkan backend and all the objects it creates
- `Window`: the native window the context draws into
- `Texture2D`: an image in GPU memory
- `Sprite`: a textured quad
- `TextureFont`: a grid of ASCII glyphs on a texture
- `Label`: a line (or lines) of text drawn with a `TextureFont`

Enums: `PixelFormat`, `FilteringMode`, `ResizeMode`.

### 2.1 Handles

`Window`, `Texture2D`, `Sprite`, `TextureFont` and `Label` are handles. The context holds the data of each object in an internal `vec`; a handle holds only a weak reference to the context and the handle of the C side (a `u64`: the generation, the kind and the slot), both private. Every field and method of a handle forwards to the context.

- Dropping a handle does not free the object. Handles can be copied freely; all the copies refer to the same object.
- `release()` frees the object; its slot can be reused, and the generation of the slot changes, so the old handles become invalid.
- The end of the context frees every object it holds.
- Using a handle whose object was released (the generation does not match) or whose context ended (the weak reference is null) raises the silent `UsageOfStaleResourceException`. The library may also clean up what depends on it, for example by making the sprites that use a released texture stale too.
- A handle can only be used with the context that created it.


## 3. Public Types

### 3.1 gfx.Sprite

```
namespace gfx

public class Sprite {
	_Context context                             // weak: the handle never keeps the context alive
	u64 handle                        // the handle of the C side: generation, kind and slot
	public pos { r32 x, r32 y }
	public offset { r32 x, r32 y }               // pivot, from the top-left of the quad
	public scale { r32 x = 1.0, r32 y = 1.0 }
	public r32 rotation                          // radians, around Z
	public i32 order
	public tl { r32 x = 0.0, r32 y = 0.0 }       // source top-left, normalized 0..1
	public br { r32 x = 1.0, r32 y = 1.0 }       // source bottom-right, normalized 0..1
	public bool visible = true
	public readonly _Texture2D texture               // weak: read through enforce

	public fun setTexture(Texture2D texture)
	public fun release()
}

// in Context
public fun Sprite createSprite(Texture2D texture)
```

A sprite is a textured quad, created by the context. It is a handle (2.1).

- `pos`: position of the origin (the pivot) in the world, in pixels.
- `offset`: the pivot, from the top-left corner of the unscaled quad, in pixels. Rotation and scale are applied around it. Default `(0, 0)`.
- `scale`: relative to the original size: `(2.0, 0.5)` doubles the width and halves the height.
- `rotation`: in radians, around the Z axis.
- `order`: draw order (5). The view is orthographic, so `order` has no effect on size.
- `tl`, `br`: the part of the texture shown, as normalized coordinates from 0 to 1. The default is the whole texture. Sprite sheets will be a class of their own, with a better way to pick each tile.
- `visible`: whether the sprite is drawn.
- `texture`: a weak reference to the texture (3.2): the sprite does not own it, so it is read through `enforce` (`_Texture2D t _= s.texture`, then `enforce t { }`). It is null once the `Texture2D` given to the sprite is dropped, even if the texture itself still exists.
- `setTexture(texture)`: changes the texture; `tl` and `br` are kept.
- `createSprite(texture)` takes only the texture; every field starts with its default.

Size of the quad, before scale and rotation:
- width = `(br.x - tl.x) * texture.width`
- height = `(br.y - tl.y) * texture.height`

The top-left corner of the unscaled, unrotated quad is `pos - offset`.

- A sprite whose texture was released is skipped.

### 3.2 Texture2D

```
namespace gfx

public enum PixelFormat { RGBA8 }
public enum FilteringMode { Nearest, Linear }

public class Texture2D {
	_Context context                  // weak: the handle never keeps the context alive
	u64 handle                        // the handle of the C side: generation, kind and slot
	public readonly i32 width, height
	public readonly PixelFormat pixelFormat
	public FilteringMode filtering

	public fun setData(arr<byte> data)
	public fun release()
}

// in Context
public fun Texture2D createTexture(arr<byte> data, i32 width, i32 height,
                                   PixelFormat format = PixelFormat.RGBA8,
                                   FilteringMode filter = FilteringMode.Nearest)
```

A texture is an image in GPU memory. It is a handle (2.1).

- `createTexture` copies the pixels to the GPU. It only reads `data`: the caller keeps the array and can drop it.
- `width` and `height` are in pixels. `width`, `height` and `pixelFormat` never change after creation.
- `RGBA8`: four bytes per pixel, in the order R, G, B, A, rows from the top. `data` must hold at least `width * height * 4` bytes; only that part is read and the rest is ignored.
- `filtering` can be changed at any time; `Nearest` is the default. File loading and image decoding are not part of Gfx.
- `setData(data)` replaces the pixels; the size and the format stay the same, and `data` follows the same rules as in `createTexture`.
- `release()` frees the GPU memory at a safe point, after the GPU has finished using it.
- A sprite or label whose texture was released is skipped.

Exceptions:
- `InvalidTextureDefinitionException`: from `createTexture` and `setData`, when `width` or `height` is ≤ 0 or `data` is shorter than `width * height * 4`.

### 3.3 Window

```
namespace gfx

public enum ResizeMode { None, KeepPixelSize, Stretch, Fit, FitWidth, FitHeight }

public class Window {
	_Context context                  // weak: the handle never keeps the context alive
	u64 handle                        // the handle of the C side: generation, kind and slot
	public i32 width, height
	public ResizeMode resizeMode = ResizeMode.KeepPixelSize
	public bool integerScale
}

// in Context
public fun Window createWindow(i32 width, i32 height)

public class ImpossibleToCreateWindowException : Exception { }
```

The window is the native window the context draws into. It is a handle (2.1).

- `createWindow(width, height)` opens the window with that client area, in pixels. It raises `ImpossibleToCreateWindowException` when the window cannot be made: the context already has a window, `width` or `height` is ≤ 0, or the system or Vulkan refuses it.
- `width`, `height`: the pixels of the viewport (the client area of the window).
- The scaling modes scale the world from the size given when the window was created.
- The window owns the Vulkan surface and the presentation resources internally.
- In this version a context has one window. Later, more windows may each show a view of the whole world (a scene system comes after that).

`resizeMode`, what happens when the window is resized (default `KeepPixelSize`):
- `None`: the window cannot be resized.
- `KeepPixelSize`: one world unit stays one pixel; a bigger window shows more of the world.
- `Stretch`: the world fills the window; it is distorted when the aspect ratio differs.
- `Fit`: the world is scaled as much as possible keeping its aspect ratio, so it is always fully visible; the space left becomes bars. It acts as `FitHeight` when the window is wider than the world (bars left and right) and as `FitWidth` when it is taller (bars above and below).
- `FitWidth`: the width of the world fills the width of the window; on the height more or less of the world is shown.
- `FitHeight`: the same, on the height.

`integerScale` (for pixel art) rounds the scale factor down to a whole number (×1, ×2, ×3...):
- With `Fit`, `FitWidth` and `FitHeight`, the space left becomes bars.
- With `Stretch`, each axis is rounded on its own (for example ×3 by ×2).
- With `None` and `KeepPixelSize` the scale is always ×1, so it has no effect.

### 3.4 TextureFont

```
namespace gfx

public class TextureFont {
	_Context context                  // weak: the handle never keeps the context alive
	u64 handle                        // the handle of the C side: generation, kind and slot
	public readonly _Texture2D texture               // weak: read through enforce
	public readonly i32 glyphWidth, glyphHeight

	public fun release()
}

// in Context
public fun TextureFont createTextureFont(Texture2D texture, i32 glyphWidth, i32 glyphHeight)
```

A texture font is a texture holding a grid of fixed-size glyphs. It is a handle (2.1); many labels can use the same font.

- `texture`: a weak reference to the texture of the glyphs, read through `enforce` (as `Sprite.texture`, 3.1).
- The glyphs are the ASCII characters 32..126 (space to `~`), laid out left to right and top to bottom.
- The number of columns is `texture.width / glyphWidth`.
- The name `Font` is kept for a real font later (variable widths, kerning, sizes). When it comes, `Label.font` can become a trait type that both implement.

### 3.5 Label

```
namespace gfx

public class Label {
	_Context context                  // weak: the handle never keeps the context alive
	u64 handle                        // the handle of the C side: generation, kind and slot
	public pos { r32 x, r32 y }
	public offset { r32 x, r32 y }
	public scale { r32 x = 1.0, r32 y = 1.0 }
	public r32 rotation               // radians, around Z
	public i32 order
	public bool visible = true
	public string text
	public color { r32 r = 1.0, r32 g = 1.0, r32 b = 1.0, r32 a = 1.0 }
	public readonly _TextureFont font               // weak: read through enforce; changed with setFont

	public fun setFont(TextureFont font)
	public fun release()
}

// in Context
public fun Label createLabel(TextureFont font, string text)
```

A label is text drawn with a `TextureFont`: one quad per character. It is a handle (2.1).

- `font`: a weak reference to the font, read through `enforce` (as `Sprite.texture`, 3.1); `setFont(font)` changes it.
- `pos`, `offset`, `scale`, `rotation`, `order` and `visible` work as in `Sprite` (3.1). The quad of the label is the whole text block; its top-left is the top-left of the first character.
- A newline moves to the start of the next line, `glyphHeight` lower.
- Characters outside 32..126 are skipped.
- `color` multiplies the glyph texture.
- A label whose font or font texture was released is skipped.


## 4.  Main classes `gfx.Gfx` and `gfx.Context`

### 4.1 gfx.Gfx

```
namespace gfx

public class Gfx {
	public static readonly string version
	public static fun bool isAvailable()
	public static fun Context createContext()
	public static fun arr<(i32 x, i32 y, i32 width, i32 height, bool primary, string name)> getDisplays()
}
```

`Gfx` is the entry point of the library. It has only static members and holds no state.

- `version`: the version of the library.
- `isAvailable()`: true when Vulkan and a usable GPU are present. It lets a program check before creating a context.
- `createContext()`: creates a new context. The caller owns it (4.2). Raises `VulkanNotAvailableException` when Vulkan or a usable GPU is not present.
- `getDisplays()`: an array of tuples, one per display connected to the system; `x`, `y` are its position on the desktop and `width`, `height` its size, all in pixels; `primary` marks the main display; `name` is the name given by the system.

### 4.2 gfx.Context

```
namespace gfx

public class Context {
	public clearColor { r32 r = 0.0, r32 g = 0.0, r32 b = 0.0, r32 a = 1.0 }

	public fun Window createWindow(i32 width, i32 height)
	public fun Texture2D createTexture(arr<byte> data, i32 width, i32 height,
	                                   PixelFormat format = PixelFormat.RGBA8,
	                                   FilteringMode filter = FilteringMode.Nearest)
	public fun Sprite createSprite(Texture2D texture)
	public fun TextureFont createTextureFont(Texture2D texture, i32 glyphWidth, i32 glyphHeight)
	public fun Label createLabel(TextureFont font, string text)

	public fun bool update()
	public fun close()
}
```

A context is a container for the Vulkan backend. Every other object of the library is created from a context and owned by it; user code holds handles (2.1).

- The caller of `Gfx.createContext()` owns the context. The context is destroyed when its owner goes out of scope, or earlier with `close()`.
- Destroying the context releases every object it created, with their GPU resources.
- In this version a context has one window and creates only 2D objects. UI and 3D objects may come later in the same context, so 2D, UI and 3D can be mixed in one window.
- More than one context can exist at the same time.
- `clearColor`: the color each frame starts from.
- `update()` renders and presents one frame (5). It returns `false` when the user closed the window, `true` otherwise. It is the only call needed per frame; changes to the objects show in the next frame.


## 5.  Rendering

- Coordinates: `(0, 0)` is the top-left of the world, X grows to the right and Y downwards. With `KeepPixelSize` one world unit is one pixel. The projection is orthographic.
- Each quad is scaled, then rotated around its pivot (`offset`), then moved to `pos`.
- Draw order: sprites and labels are drawn together, by increasing `order`; higher values are on top. Equal values are drawn in creation order.
- Each frame starts by clearing the window to `clearColor`.
- Standard alpha blending: transparent pixels show what is behind them.
- Hidden objects (`visible = false`) and objects whose texture or font was released are skipped.
- The implementation may batch quads to reduce draw calls, as long as the draw order and the blending stay the same.


## 6.  Example

```
namespace app

import gfx

fun main() {
	Context ctx = Gfx.createContext()
	Window win = ctx.createWindow(1280, 1024)
	Texture2D t1 = ctx.createTexture(bytes1, 64, 64)
	Texture2D t2 = ctx.createTexture(bytes2, 64, 256)
	Sprite s = ctx.createSprite(t1)

	bool gamePlaying = true
	while gamePlaying {
		s.pos.x++
		gamePlaying = ctx.update()
	}
}
```

`bytes1` and `bytes2` hold RGBA8 pixels, for example from an image decoder of another library. The context ends with `main` and releases everything.


## 7.  Later versions

- Multiple windows, each a view of the whole world; a scene system after that.
- Sprite sheets, as a class of their own.
- `Font`: real fonts (variable widths, kerning, sizes).
- Window title, cameras, input handling.
- Structs instead of field groups and tuples, when the language defines them.
