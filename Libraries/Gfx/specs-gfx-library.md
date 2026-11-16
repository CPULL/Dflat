# GFX Library
v 0.0.2 2026/11/16

## 1. General
This library allows graphic capabilities for DFlat programs. It is backed by Vulkan and should be available on all target architectures supporting Vulkan.
It is a library giving 4 namespaces:
- **gfx.core** contains the basic items shared by all 3 viewports
- **gfx.g2d** all items needed to create 2d viewports (mostly sprites, canvas, and labels)
- **gfx.g3d** all items for full 3d worlds
- **gfx.ui** specific viewport for the User Interfaces

- It is not yet decided if it is a single library or 4 different libraries
- Portability across OSes is the only constraint
- Namespace names cannot start with a digit (1, identifiers): `g2d` and `g3d`, not `2d` and `3d`

### 1.1 Inputs
- Input is NOT part of gfx (it will be a separate library)
- Windows send events: gfx depends on multitasking (root.tasks is required)
- Vulkan has no input, nodes, cameras, transforms or meshes: only buffers, images, pipelines+shaders, command buffers
- Vulkan has no matrix or color types: a matrix is 16 floats read as a `mat4` by the shaders (column-major), clip space has Y down and depth 0..1; colors are four floats (RGBA) or pixels in the format of the image

### 1.2 Notes
- The Vulkan objects are C handles (`handle`, spec 22) held inside the Dflat classes; the program never sees them
- Ownership follows the tree pattern of 12.7: what the program and the library both hold is counted (`@T`, `&@T` when the program writes it), and the references back to an owner are weak (`%T`)
- Context owns everything. Closing a window cascades to its viewports and their nodes; textures and fonts are counted
- Writing a counted read/write object (`&@T`) happens inside `enforce` (12.7)
- Plan: implement Core + 2D first, then 3D and UI
- Later: 2D clipping, tilemaps
- The node traits (GfxNode, Node2D, Node3D, UINode), Camera, Quaternion, Vec2, Vec3, Matrix4 and Color live in gfx.core
- The math and color structs are `struct<fixed>` (11.1.5.6): the same layout as C and the shaders, so they are given to C by copy
- Their methods that return a new value are `unrestricted` (1.6.2): they can be called on a value lent read-only (a plain parameter)
- The objects are created only by the functions of their owner (`createWindow`, `addSprite`, ...), not by their constructors

## 2. Context, Windows & Viewports
The context needs to be initialized first, all other operations require the context to be available.
```
GfxCtx ctx = gfx.core.GfxCtx()

public class GfxCtx {
  public vec<&@Window> windows
  public vec<@Texture> textures
  public vec<@Font> fonts

  public fun @Texture loadTexture(string path, PixelFormat format, FilteringMode filtering = FilteringMode.Linear) // loads a texture from a file collecting automatically the size. Png images are supported
  public fun @Texture loadTexture(arr<byte> data, PixelFormat format, u32 width, u32 height, FilteringMode filtering = FilteringMode.Linear) // load a texture from an array of bytes.
  public fun @Texture createRenderTexture(u32 width, u32 height, PixelFormat format, FilteringMode filtering = FilteringMode.Linear) // creates an empty texture that can be used to draw items inside or have a camera to render over it
  public fun @Font loadFont(string path, r32 size, string characters) // loads a font (formats have to be specified, TTF is a good candidate), only the specified characters will be generated
  public fun &@Window createWindow(string title, u32 width, u32 height, bool resizable) // creates a window with the specified size and title
  public fun update() // render + swap all windows
  public fun update(@Window window) // render + swap single-window
  public fun flush() // releases the textures and fonts that only the context still uses
}
```

### 2.1 Window
```
public class Window {
  public string title
  public size { u32 width, u32 height }
  public vec<&@Viewport> viewports

  public fun &@Viewport2D createViewport2D()
  public fun &@Viewport3D createViewport3D()
  public fun &@ViewportUI createViewportUI()
}
```


### 2.2 Viewports
- **Core**: context, screens/sizes, windows, viewports, textures, fonts, render textures, text console
- **2D**: sprites, framed sprites, labels, sprite sheets, parent/child, z-order
- **3D**: thin wrapper around Vulkan (meshes, skeletons, shaders)
- **UI**: layout structures + items, simplified HTML+CSS style; no camera

- Core provides viewports; layers draw only into viewports
- Each viewport belongs to a window; created by the window (window.createViewport2D/3D/UI)
- Viewports overlap as layers: UI > 2D > 3D
- Draw order: leaning to the order in Window.viewports (not defined yet)
- No mixing of node types: one viewport class per layer, sharing a Viewport trait

```
public trait Viewport {
  public %Window window                 // weak: the window owns the viewport
}

public class Viewport2D : Viewport {
  public Camera camera                  // owned, attached directly
  public vec<&@Node2D> children

  public fun &@Sprite addSprite(@Texture texture)
  public fun &@Label addLabel(@Font font, string text)
}

public class Viewport3D : Viewport {
  public %Camera refCamera              // the active one among the cameras in the world
  public vec<&@Node3D> children
}

public class ViewportUI : Viewport {
  public vec<&@UINode> children         // no camera
}
```

### 2.3 Textures
- Core, shared by all viewports
- Created through the context
- Format via PixelFormat enum (includes compressed formats)
- Visible flag: writable (render textures = writable)
- Canvas will use writable textures to allow drawing
- They are counted (`@Texture`); the context holds one usage of each, so a texture nobody else uses stays alive until `flush` on the context releases it

```
public enum PixelFormat { Rgba8, Bgra8, Gray8, Bc7, Etc2, Default }
public enum FilteringMode { NearestNeighbour, Linear }

public class Texture {
  public readonly u32 width
  public readonly u32 height
  public readonly PixelFormat format
  public FilteringMode filtering
  public readonly bool isRendertexture
}
```


### 2.4 Fonts
- Live in Core, used by labels, console and UI
- Rendered as texture atlas (pre-rendered; dynamic mode depends on target power)
- Font size is defined by the font; Label scale can tweak it
- Kerning must be possible in the future, variable size of characters should be implemented from day 1
- They are counted (`@Font`) and released by `flush`, as textures

```
public class Font {
  public readonly string fontName
  public readonly r32 size
  public readonly @Texture atlas
  public readonly map<char, FontCoords> coords
}

public struct<fixed> FontCoords {
  r32 top,
  r32 left,
  r32 width,
  r32 height
}
```

### 2.5 Cameras
- One Camera class (a Node3D) for both 2D and 3D; the 2D viewport can tilt it
- 2D viewport: one camera attached directly to the viewport
- 3D viewport: several cameras in the world, one active (refCamera)
- Risk: zindex sorting vs depth when a 2D camera is tilted
- render texture to render directly on a writable texture

```
public enum Projection { Perspective, Orthographic }

public class Camera : Node3D {
  public Projection projection
  public r32 fov
  public r32 orthoHeight
  public r32 near
  public r32 far
  public bool pixelPerfect              // ortho: 1 world unit = 1 pixel, orthoHeight ignored, position snapped
  public @Texture targetTexture         // null = viewport; must be writable
}
```

## 3. Graphic nodes
- A node is counted and read/write (`&@`): its parent and the program both hold it; the parent reference is weak
- `getChild<T>` gives null when the child at the index is not a T

```
public trait GfxNode {
  public %GfxNode parent                // weak: the parent owns the node
  public vec<&@GfxNode> children
  public bool visible = true
}

public trait Node2D : GfxNode {
  public pos { r32 x, r32 y }
  public r32 rotation
  public scale { r32 x = 1, r32 y = 1 }
  public pivot { r32 offx, r32 offy }
  public i32 zindex
  public Color tint

  public fun &@T addChild<T : Node2D>(&@T node)
  public fun &@T removeChild<T : Node2D>(&@T node)
  public fun &@Node2D removeChild(uns index)
  public fun &@T getChild<T : Node2D>(uns index)
}

public trait Node3D : GfxNode {
  public pos { r32 x, r32 y, r32 z }
  public Quaternion rotation
  public scale { r32 x = 1, r32 y = 1, r32 z = 1 }
}

public trait UINode : GfxNode {
  // Not yet defined
}
```

### 3.1 Sprites
```
public class Sprite : Node2D {
  public @Texture texture
}

public class FramedSprite : Node2D {
  public @Texture texture
  public texel { r32 uvWidth, r32 uvHeight }   // UV coordinates 0.5, 0.5 will produce 4 frames
  public u32 frame
}
```

### 3.2 2D Labels
```
public class Label : Node2D {
  public string text
  public @Font font
}
```

### 3.3 Quaternion
- Unit quaternion for 3D rotation, by value, 4 x r32 (x, y, z, w), layout compatible with C and shaders (`struct<fixed>`)
- Conventions: Hamilton product, right-handed, angles in radians, w is the scalar part

```
public struct<fixed> Quaternion {
  r32 x = 0,
  r32 y = 0,
  r32 z = 0,
  r32 w = 1                                   // default = identity (no rotation)

  // Creation
  public static fun Quaternion identity()
  public static fun Quaternion fromAxisAngle(Vec3 axis, r32 radians)
  public static fun Quaternion fromEuler(r32 pitch, r32 yaw, r32 roll)   // x, y, z axes

  // Operations
  unrestricted fun Quaternion multiply(Quaternion other)   // this then other (composes rotations)
  unrestricted fun Quaternion conjugate()                  // (-x, -y, -z, w)
  unrestricted fun Quaternion inverse()                    // for a unit quaternion = conjugate
  unrestricted fun Quaternion normalize()
  unrestricted fun r32 length()
  unrestricted fun r32 dot(Quaternion other)
  unrestricted fun Vec3 rotate(Vec3 vector)                // apply the rotation to a vector
  unrestricted fun Quaternion slerp(Quaternion target, r32 amount)   // amount 0..1, shortest path

  // Conversion
  unrestricted fun Matrix4 toMatrix()
  unrestricted fun Vec3 toEuler()
}
```

## 4. Core math and color types
All by value, in gfx.core.

### 4.1 Vec2 and Vec3
```
public struct<fixed> Vec2 {
  r32 x = 0,
  r32 y = 0

  unrestricted fun Vec2 add(Vec2 other)
  unrestricted fun Vec2 subtract(Vec2 other)
  unrestricted fun Vec2 scale(r32 factor)
  unrestricted fun r32 dot(Vec2 other)
  unrestricted fun r32 length()
  unrestricted fun Vec2 normalize()
  unrestricted fun r32 distance(Vec2 other)
  unrestricted fun Vec2 lerp(Vec2 target, r32 amount)    // amount 0..1
}

public struct<fixed> Vec3 {
  r32 x = 0,
  r32 y = 0,
  r32 z = 0

  unrestricted fun Vec3 add(Vec3 other)
  unrestricted fun Vec3 subtract(Vec3 other)
  unrestricted fun Vec3 scale(r32 factor)
  unrestricted fun r32 dot(Vec3 other)
  unrestricted fun Vec3 cross(Vec3 other)
  unrestricted fun r32 length()
  unrestricted fun Vec3 normalize()
  unrestricted fun r32 distance(Vec3 other)
  unrestricted fun Vec3 lerp(Vec3 target, r32 amount)
}
```

### 4.2 Matrix4
- Based on an inline array of 16 r32 (11.1.5.4), column-major: maps directly to a `mat4` in the shaders

```
public struct<fixed> Matrix4 {
  arr<r32>[16] values       // column-major

  // Creation
  public static fun Matrix4 identity()
  public static fun Matrix4 fromTranslation(Vec3 offset)
  public static fun Matrix4 fromScale(Vec3 factor)
  public static fun Matrix4 fromQuaternion(Quaternion rotation)
  public static fun Matrix4 fromTransform(Vec3 position, Quaternion rotation, Vec3 scale)

  // Camera (built for Vulkan clip space: Y down, depth 0..1)
  public static fun Matrix4 perspective(r32 fov, r32 aspect, r32 near, r32 far)
  public static fun Matrix4 orthographic(r32 width, r32 height, r32 near, r32 far)
  public static fun Matrix4 lookAt(Vec3 eye, Vec3 target, Vec3 up)

  // Operations
  unrestricted fun Matrix4 multiply(Matrix4 other)
  unrestricted fun Matrix4 transpose()
  unrestricted fun Matrix4 inverse()
  unrestricted fun Vec3 transformPoint(Vec3 point)
  unrestricted fun Vec3 transformDirection(Vec3 direction)   // ignores translation
}
```

### 4.3 Color
- Four r32 (shaders and clear colors use 32-bit floats), default opaque white so a default tint changes nothing
- fromHtml and fromBytes take the values as given, no sRGB conversion (not settled, see open items)

```
public struct<fixed> Color {
  r32 red = 1,
  r32 green = 1,
  r32 blue = 1,
  r32 alpha = 1

  public static fun Color fromBytes(u8 red, u8 green, u8 blue, u8 alpha)   // 0..255 mapped to 0..1
  public static fun Color fromHtml(string text)                            // "#RGB", "#RRGGBB" or "#RRGGBBAA"
  unrestricted fun Color lerp(Color target, r32 amount)
}
```

# Open items
- UINode contents
- 3D content (meshes, skeletons, shaders, materials)
- Node3D pivot
- Error handling
- Init parameters
- Color: linear or sRGB values inside Color
- Color: framebuffer format (UNORM or sRGB)
- Color: whether fromHtml and fromBytes convert
- Color: named colors and alpha blending rules
- 2D coordinate system: origin, Y direction, units
- World transforms: how a node position combines with its parents, and whether zindex is relative or global
- Window methods and events: close, resize, title; viewport close
- Screens/monitors query
- Writing pixels into a writable texture, and Canvas on top of it
- Text console class
- Sprite sheet (FramedSprite stands in for now)
- Label alignment and wrapping
- Camera helpers (view and projection matrices)
- Matrix4 default: zeroed (11.1.5.1) or identity
