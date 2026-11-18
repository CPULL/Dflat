# GFX Library
v 0.0.4 2026/11/18

## 1. General
This library allows graphic capabilities for DFlat programs. It is backed by Vulkan and should be available on all target architectures supporting Vulkan.
It is a library giving 4 namespaces:
- **gfx.core** contains the basic items shared by all 3 viewports: context, screens, windows, viewports, textures, fonts, cameras, the node traits and the math and color types (section 4)
- **gfx.g2d** all items needed to create 2d viewports (mostly sprites, canvas, and labels)
- **gfx.g3d** all items for full 3d worlds
- **gfx.ui** specific viewport for the User Interfaces

- Decided: one library `gfx`, split in namespaces. gfx.core is only a namespace holding the core items (context, screens, windows, viewports, textures, fonts, cameras, node traits, math and color types). gfx.g2d, gfx.g3d and gfx.ui are each a namespace and an actual viewport type. Not one library per layer
- Portability across OSes is the only constraint
- Namespace names cannot start with a digit (1, identifiers): `g2d` and `g3d`, not `2d` and `3d`
- The context is created without parameters. Init parameters are probably not needed (open item); candidates, if ever needed: application name, validation layers on/off, GPU preference

### 1.1 Targets
- Windows, Linux, Android: Vulkan natively
- macOS, iOS: no native Vulkan, it goes through MoltenVK (Vulkan over Metal)
- Browsers (WebAssembly): no Vulkan at all. A WebGPU backend would be needed (much closer to the Vulkan model than WebGL, which is a state machine)
- Shaders differ per backend (SPIR-V, GLSL ES, WGSL, MSL): see section 5
- Windows, screens and the OS code are per platform whatever the graphics API
- On Android, iOS and the web a screen is basically one fixed display and the window is the app surface

### 1.2 Inputs, events and Vulkan
- Input is NOT part of gfx (it will be a separate library)
- Events are part of the language (the Event registry, Dflat spec 20.4): gfx has no library dependency for them. Events are local to the process, they do not cross processes
- Vulkan has no input, nodes, cameras, transforms or meshes: only buffers, images, pipelines+shaders, command buffers
- Vulkan has no matrix or color types: a matrix is 16 floats read as a `mat4` by the shaders (column-major), clip space has Y down and depth 0..1; colors are four floats (RGBA) or pixels in the format of the image
- Vulkan does not list monitors in normal use: they come from the OS windowing layer (see 2.1)
- A camera is technically just a matrix applied to all the vertices to find where they are

### 1.3 Notes
- The Vulkan objects are C handles (`handle`, spec 22) held inside the Dflat classes; the program never sees them. A handle cannot be copied, only moved
- Ownership follows the tree pattern of 12.7: what the program and the library both hold is counted (`@T`, `&@T` when the program writes it), and the references back to an owner are weak (`%T`)
- Context owns everything. Closing a window cascades to its viewports and their nodes; textures and fonts are counted
- Writing a counted read/write object (`&@T`) happens inside `enforce` (12.7)
- Plan: implement Core + 2D first, then 3D and UI
- Later: 2D clipping, tilemaps
- The node traits (GfxNode, Node2D, Node3D, UINode), Camera, Quaternion, Vec2, Vec3, Matrix4 and Color live in gfx.core
- The math and color structs are `struct<fixed>` (11.1.5.6): the same layout as C and the shaders, so they are given to C by copy
- Memory alignment of the structs is a priority: `<fixed>` structs use the natural C alignment and the fields are never reordered
- Their methods that return a new value are `unrestricted` (1.6.2): they can be called on a value lent read-only (a plain parameter)
- The objects are created only by the functions of their owner (`createWindow`, `addSprite`, ...), not by their constructors
- Performance is a primary concern, especially the transform cache (3.5)
- Errors: exceptions and silent exceptions, produced when needed

### 1.4 Coordinate system
- Decided: the library uses Vulkan's convention everywhere. No matter which system is picked people will complain, so the Vulkan one is kept
- X positive is right, Y positive is down, Z positive is forward (into the screen, depth 0 near to 1 far); the system is right-handed
- 2D: the origin is the top-left corner
- With Y down, a positive rotation around Z looks clockwise on screen. Quaternions: Hamilton product, right-handed, angles in radians
- Possibility (can be dropped): conversion matrices `FROM_GODOT_MATRIX`, `FROM_UNITY_MATRIX`, `FROM_BLENDER_MATRIX`, `FROM_UNREAL_MATRIX` and the matching `TO_*` ones (the inverses)
  - Godot and Blender to ours are proper rotations (Godot is Y up with -Z forward, Blender is Z up with Y forward)
  - Unity and Unreal are left-handed, so converting from them includes a mirror (the triangle winding flips); Unreal also needs a unit scale (centimeters to meters)
  - Code copied from other engines is not expected to work unchanged, so the matrices are optional

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
  public fun &@Window createWindow(string title, u32 width, u32 height, bool resizable, Screen screen) // creates a window with the specified size and title on the screen (the primary one if not given)
  public fun closeWindow(&@Window window) // removes it from windows, closes its viewports and destroys the OS window (2.2)
  public fun update() // render + swap all windows
  public fun update(@Window window) // render + swap single-window
  public fun flush() // releases the textures and fonts that only the context still uses
}
```

A shader is a `shader` block in its own `.dfsh` file, compiled to SPIR-V with the program (5.1). A material is created for a shader type, `Material<MyShader>()` (5.6).

### 2.1 Screens
- The concept of screen is defined in gfx.core and does not need a context
- Vulkan does not provide the list of the monitors in normal use, so this part is OS specific code (conditional per OS):
  - Windows: `EnumDisplayMonitors` and the display settings calls
  - Linux: X11 RandR or Wayland `wl_output`
  - macOS: `NSScreen`
  - Android: the native window and `DisplayManager`; iOS: `UIScreen`
- Only the `VK_KHR_display` extension lists displays and modes, and that is for rendering without a window system
- A cross-platform helper library (GLFW or SDL style) would do the same through c-interop, at the cost of a dependency

```
public struct Screen {
  string name,
  i32 x,                         // position on the desktop
  i32 y,
  u32 width,
  u32 height,
  u16 refreshRate,
  r32 scale,                     // DPI scale
  bool primary
}

arr<Screen> screens = gfx.core.getScreens()
```
The screen where a window appears is a parameter of `createWindow`.

### 2.2 Window
- For now a window has just a size
- The background color belongs to the window: it is the clear color of its framebuffer, applied at the start of each frame. The default value is not defined yet (black?)
- A window owns its viewports. Closing a window cascades to its viewports and their nodes

```
// in the .dflib of gfx (spec 22): the OS window, freed when the field that owns it ends
handle GlfwWindow addr
native glfwDestroyWindow(GlfwWindow w)
fun destroy([~GlfwWindow w]) -> glfwDestroyWindow(w)

public class Window {
  public string title
  public size { u32 width, u32 height }
  public Color background                   // clear color of the window
  public vec<Viewport> viewports
  GlfwWindow osWindow                       // not public: the program never sees it

  public fun createViewport2D() // Add one 2D viewport
  public fun createViewport3D() // Add one 3D viewport
  public fun createViewportUI() // Add one UI viewport

  atEnd {
    for vp in viewports {
      enforce vp { vp.close() }             // frees its GPU objects (swapchain, surface) while the OS window still exists
    }
  }                                         // then the fields end: viewports releases its usages, osWindow is destroyed
}
```
Decided: no other window methods for now, as they would have to be replicable on all the targets.
- The viewports are counted: the program can still hold one after its window is gone, so ending the window cannot end them. `close()` (internal to gfx, on the Viewport trait) frees what the viewport has on the GPU and marks it closed; a closed viewport draws nothing, adding to it does nothing, and its object ends with its last usage
- The order matters: each swapchain and surface is destroyed before the OS window, so the viewports are closed in the `atEnd` and the handle ends after it, with the fields
- A window is counted too (the context holds one usage in `windows`, the program others), so `win is atEnd.` is not possible (12.4: only for plain owners) and `win is done.` only releases the program's usage. Decided: `ctx.closeWindow(win)` removes it from `windows` and runs the cascade above; a usage the program still holds sees a closed window, and the object ends with its last usage

### 2.3 Window events
- Window events use the Event registry of the language (spec 20.4): events are identified by a name only, the payload is a struct, there is no automatic sender (the payload carries which window), the payload is passed readonly and events are not cross process
- Decided: a single event for all window events, whose payload carries the kind
- The registry is global by name, so the name has a prefix: `gfx.window`
- The window in the payload is a weak reference (`%Window`): one event goes to several receivers and none of them owns the window
- The code below is an example to be discussed, not the final shape of the spec
- Resize carries the new size; close, focus, minimize and restore carry only the window. Keyboard, mouse and similar events belong to the input library

```
public enum WindowEventType { Close, Minimize, Maximize, Restore, Resize, FocusGained, FocusLost }   // preliminary list

public struct WindowEvent {
  %Window window,              // weak reference
  WindowEventType eventType,
  u32 width,                   // current size, meaningful for Resize and Maximize
  u32 height
}

// In gfx.core, created with the context
Event windowEvent = Event("gfx.window", WindowEvent)

// gfx side: ctx.update() collects the OS window messages and sends them
windowEvent.send(WindowEvent(win, WindowEventType.Resize, 800, 600))

// user side, inside a task
wait on windowEvent as info {
  if info.eventType == WindowEventType.Close { info.window.close() }
}
```
How a task gets the event, both ways are valid:
- The task creates `Event("gfx.window", WindowEvent)` itself, with the same name, and the event mechanism sends and receives it. The task has to know the name and the payload type
- The event is already created in gfx.core and the task borrows it (a field of the context or a core-level accessor). There is one definition, so the tasks never write the name or the payload type

Open: the sketch above still calls `info.window.close()`; a window is closed with `ctx.closeWindow` (2.2), which takes a `&@Window` while the payload holds a weak `%Window`. Also open: whether a Close event only notifies or the window closes by itself when nobody handles it, and how a program that never waits on the event gets rid of its window.

### 2.4 Viewports
- **Core**: context, screens/sizes, windows, viewports, textures, fonts, render textures, text console
- **2D**: sprites, framed sprites, labels, sprite sheets, parent/child, z-order
- **3D**: thin wrapper around Vulkan (meshes, skeletons, shaders)
- **UI**: layout structures + items, simplified HTML+CSS style; no camera

- Core provides viewports; layers draw only into viewports
- Each viewport belongs to a window; created by the window (window.createViewport2D/3D/UI)
- Draw order is per window, in the order of `Window.viewports`: the first entry is drawn in front of everything, the last entry is drawn beyond everything (drawing runs from the last entry to the first)
- No mixing of node types: one viewport class per layer, sharing a Viewport trait, so each holds only the node types it can handle

```
public trait Viewport : ShaderHolder {
  public %Window window                 // weak: the window owns the viewport
  public readonly bool closed           // set by close(): its window is gone
  public Color ambientColor             // ambient light of the viewport, used by the materials inside or by the post shader
  public r32 ambientPower

  internal fun close()                  // frees its GPU objects; called by the window (2.2)
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
Possibilities and open points:
- Viewport2D could also have `addChild<T : Node2D>(&@T node)` like the nodes (3.1)
- Screen shader: a viewport may have a shader attached (a screen shader) that applies a post processing to the view. A viewport with a shader renders into an offscreen texture first, then a full-screen pass runs the shader over it; without a shader it draws straight to the window, so the extra pass only costs when used. It is the same mechanism as the camera `targetTexture` and the render texture. The output of a shaded viewport has to be composited with the layers below it. The viewport is a `ShaderHolder` (5.6): the parameters of the shader (a strength value...) are typed fields, like in a material. A null `shader` means no screen shader

### 2.5 Textures
- Core, shared by all viewports
- Created through the context
- Format via PixelFormat enum (includes compressed formats). In Vulkan compression is just another format, so no separate field is needed
- Visible flag: writable (render textures = writable). The usage of a Vulkan image is fixed at creation (sampled, transfer destination, color attachment), so the mark must exist at creation. A compressed format cannot be writable or a render target
- Canvas will use writable textures to allow drawing
- They are counted (`@Texture`); the context holds one usage of each, so a texture nobody else uses stays alive until `flush` on the context releases it
- A render texture needs layout transitions between "being rendered to" and "being sampled": handled internally by the library
- Color space: textures say which space their pixels are in. Vulkan has sRGB image formats (the hardware converts to linear when sampling), so PixelFormat will probably get sRGB variants (possibility, e.g. `Rgba8Srgb`)

```
public enum PixelFormat { Rgba8, Bgra8, Gray8, Bc7, Etc2, Default }
public enum FilteringMode { NearestNeighbour, Linear }

public class Texture {
  public readonly u32 width
  public readonly u32 height
  public readonly PixelFormat format
  public FilteringMode filtering
  public readonly bool writable
}
```

### 2.6 Canvas
Preliminary: a lot of items need to be specified better or completed.

- A canvas is a writable texture (a render texture of Vulkan) with a drawing API. Shapes are drawn into the canvas, which is then shown by a sprite
- The canvas draws items right away and does not allow to read from it. Every call is a GPU draw: the calls are recorded and run in order on the GPU, so the result is visible by the next frame
- Because of this there is no CPU copy to keep in sync. `point()` plays the role of `setPixel`, as it is a draw and not a CPU write
- Draw calls can be batched (not sure yet)
- The stroke is defined by a color and a width at least

```
public struct Stroke {
  Color color,
  r32 width = 1.0
  // possible later: cap (butt, round, square), join (miter, round, bevel), dash pattern
}

public class Canvas {
  public Stroke stroke                  // current stroke, used by the calls that stroke
  // Every call that strokes (strokeRect, strokeCircle, strokeEllipse, line, lineTo, bezierCurveTo,
  // quadraticCurveTo, arc, arcTo, strokePath) has an optional last parameter `Stroke stroke` that
  // overrides the canvas stroke for that call
  public Color clearColor               // used by clear()
  public r32 alpha                      // global alpha for all calls
  public u32 width { R: ... }
  public u32 height { R: ... }

  public fun clear()
  public fun clear(Color color)

  // Primitives
  public fun point(r32 x, r32 y, Color color)                       // setPixel
  public fun line(r32 x1, r32 y1, r32 x2, r32 y2)
  public fun fillRect(r32 x, r32 y, r32 width, r32 height, Color color)
  public fun strokeRect(r32 x, r32 y, r32 width, r32 height)
  public fun fillCircle(r32 x, r32 y, r32 radius, Color color)
  public fun strokeCircle(r32 x, r32 y, r32 radius)
  public fun fillEllipse(r32 x, r32 y, r32 radiusX, r32 radiusY, Color color)
  public fun strokeEllipse(r32 x, r32 y, r32 radiusX, r32 radiusY)

  // Paths
  public fun startPath()
  public fun closePath()                                            // used just when we need to fill a closed path
  public fun moveTo(r32 x, r32 y)
  public fun lineTo(r32 x, r32 y)
  public fun rect(r32 x, r32 y, r32 width, r32 height)              // adds a rectangle to the path
  public fun bezierCurveTo(r32 cp1x, r32 cp1y, r32 cp2x, r32 cp2y, r32 x, r32 y)
  public fun quadraticCurveTo(r32 cpx, r32 cpy, r32 x, r32 y)
  public fun arc(r32 x, r32 y, r32 radius, r32 startAngle, r32 endAngle, bool counterclockwise)
  public fun arcTo(r32 x1, r32 y1, r32 x2, r32 y2, r32 radius)
  public fun strokePath()                                           // strokes the current path
  public fun fill(Color color)

  // State and transform
  public fun pushState()                                            // saves stroke and transform
  public fun popState()
  public fun translate(r32 x, r32 y)
  public fun rotate(r32 angle)
  public fun scale(r32 x, r32 y)

  // Clipping
  public fun setClip(r32 x, r32 y, r32 width, r32 height)
  public fun clearClip()

  // Images and text
  public fun drawImage(@Texture texture, r32 x, r32 y, r32 scale, r32 angle, r32 cx, r32 cy)
  public fun drawImage(@Texture texture, r32 sourceX, r32 sourceY, r32 sourceWidth, r32 sourceHeight,
                       r32 x, r32 y, r32 scale, r32 angle, r32 cx, r32 cy)   // with the source box
  public fun text(string text, r32 x, r32 y, r32 width, r32 height, @Font font)

  // The only way to read: a GPU to CPU readback (slow, waits for the GPU, the image needs the transfer-source usage)
  public fun arr<byte> data, u32 width, u32 height, PixelFormat format getTexture()
}
```
Details still to define:
- `text` with a box: wrapping, alignment, what happens on overflow (clip or spill); it needs a color as the font only provides the glyph shapes
- Alpha blending rules over what is already drawn
- Curves and arcs are flattened into line segments on the GPU. Filling a closed path needs triangulation: easy for convex shapes, concave ones need ear clipping or a stencil pass
- Y is down, so the direction of an arc is judged as seen on screen
- `getTexture()` returns the pixels with the size and the pixel format (named results); the rows are returned tightly packed (Vulkan readbacks can have padded rows)
- The `Canvas` class itself (the texture it wraps, ownership) is not defined yet

Earlier proposal (not needed with the write-only canvas, kept for reference): a CPU copy and a GPU copy of the texture with three states, `Cpu`, `Gpu` and `Both`; an action from the side that owns the latest data does nothing, from the other side it copies first:
- CPU read: `Both` or `Cpu` do nothing, `Gpu` copies down and becomes `Both`
- CPU write: ends in `Cpu` (copying down first if the state was `Gpu`)
- GPU read or draw: `Both` or `Gpu` do nothing, `Cpu` copies up and becomes `Both`
- GPU write (shape, blit): ends in `Gpu` (copying up first if the state was `Cpu`)

SVG:
- SVG is vector, so it needs a rasterizer. Vulkan has no vector path drawing
- SVG goes in a separate sub-library, loaded only when needed. It brings its own rasterizer and writes into a writable texture, or builds on the path API of the canvas, so gfx itself stays small
- A subset (shapes, paths, fills, strokes, gradients) is realistic; full SVG (CSS, filters, text, animation) is huge. Loading rasterizes once at a chosen size into a texture

### 2.7 Fonts
- Live in Core, used by labels, console and UI
- In this library fonts are basic: advanced fonts will be in a sub-library. It is not even sure that a "read ttf file" will be added to gfx
- Rendered as texture atlas (pre-rendered; dynamic mode depends on target power: not all machines can render dynamic fonts)
- The mode depends on the target. Pre-rendered raster is the base
- The atlas texture of a font belongs to the font and is not shared (it would be useless to share it)
- Font size is defined by the font, because it is a pre-rendered texture; Label scale can tweak it
- Kerning must be possible in the future, variable size of characters should be implemented from day 1
- They are counted (`@Font`) and released by `flush`, as textures
- Open: how an atlas is created without reading a TTF, as the characters and their positions have to be described. Two ways: a grid (the cell size and the characters in order give the positions) or an explicit table, one `FontCoords` per character, like the BMFont `.fnt` text file

```
public class Font {
  public readonly string fontName
  public readonly r32 size
  public readonly Texture atlas         // owned by the font, never shared
  public readonly map<char, FontCoords> coords
}

public struct<fixed> FontCoords {
  r32 top,
  r32 left,
  r32 width,
  r32 height
}
```

### 2.8 Cameras
- One Camera class (a Node3D) for both 2D and 3D; the 2D viewport can tilt it (rotating the camera axis)
- 2D viewport: one camera attached directly to the viewport
- 3D viewport: several cameras in the world, one active (refCamera, a reference held by the viewport, so there cannot be two active ones)
- Risk: zindex sorting vs depth when a 2D camera is tilted
- render texture to render directly on a writable texture
- Camera and Node3D (with Quaternion) are in gfx.core, so gfx.g2d does not depend on gfx.g3d

```
public enum Projection { Perspective, Orthographic }

public class Camera : Node3D {
  public Projection projection
  public r32 fov                        // Perspective: vertical field of view
  public r32 orthoHeight                // Orthographic: visible height in world units
  public r32 near
  public r32 far
  public bool pixelPerfect              // ortho: 1 world unit = 1 pixel, orthoHeight ignored, position snapped
  public @Texture targetTexture         // null = viewport; must be writable

  // Matrices (read only, calculated)
  public Matrix4 viewMatrix { R: globalMatrix.inverse() }
  public Matrix4 projectionMatrix { R: ... }      // from the fields above; the aspect comes from the window, or from targetTexture if set
  public Matrix4 viewProjectionMatrix { R: projectionMatrix.multiply(viewMatrix) }
}
```
The aspect ratio comes from the window (or from `targetTexture` when set) and is not stored.

## 3. Graphic nodes
- All the node traits are in gfx.core
- A node is counted and read/write (`&@`): its parent and the program both hold it; the parent reference is weak
- `getChild<T>` gives null when the child at the index is not a T
- Each node has a local position and rotation (relative to the parent, centered on the parent value) and a global position and rotation. At the end position, rotation and scale combine into a 4x4 matrix (`Matrix4`)
- The global values are properties: they are calculated on read and the write sets the local field from the wanted global value. The `calculate` below is pseudo code
- The matrices are read only properties, cached (3.5)
- Setting a global scale is the awkward case with a rotated parent (a shear appears and cannot be fully read back as position, rotation and scale)

### 3.1 Node traits
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

  // Hierarchy
  public fun &@T addChild<T : Node2D>(&@T node)
  public fun &@T removeChild<T : Node2D>(&@T node)
  public fun &@Node2D removeChild(uns index)
  public fun &@T getChild<T : Node2D>(uns index)
  public uns childCount { R: children.length }

  // Local changes (relative to the parent)
  public fun move(Vec2 offset)
  public fun rotate(r32 radians)

  // Global values, derived from the parent chain
  public Vec2 globalPosition { R: calculate(field), W: field = calculate(value) }
  public r32 globalRotation { R: calculate(field), W: field = calculate(value) }
  public Vec2 globalScale { R: calculate(field), W: field = calculate(value) }

  // Matrices (cached, read only)
  public Matrix4 localMatrix { R: ... }     // position, rotation, scale, with the pivot as origin
  public Matrix4 globalMatrix { R: ... }    // parent global matrix x local matrix
}

public trait Node3D : GfxNode {
  public pos { r32 x, r32 y, r32 z }
  public Quaternion rotation
  public scale { r32 x = 1, r32 y = 1, r32 z = 1 }
  public pivot { r32 offx, r32 offy, r32 offz }

  // Hierarchy
  public fun &@T addChild<T : Node3D>(&@T node)
  public fun &@T removeChild<T : Node3D>(&@T node)
  public fun &@Node3D removeChild(uns index)
  public fun &@T getChild<T : Node3D>(uns index)
  public uns childCount { R: children.length }

  // Local changes
  public fun move(Vec3 offset)
  public fun rotate(Quaternion rotation)
  public fun lookAt(Vec3 target, Vec3 up)

  // Global values
  public Vec3 globalPosition { R: calculate(field), W: field = calculate(value) }
  public Quaternion globalRotation { R: calculate(field), W: field = calculate(value) }
  public Vec3 globalScale { R: calculate(field), W: field = calculate(value) }

  // Matrices
  public Matrix4 localMatrix { R: ... }     // position, rotation, scale, with the pivot as origin
  public Matrix4 globalMatrix { R: ... }
}

public trait UINode : GfxNode {
  // Not yet defined
}
```
- A field group is set as a whole from a tuple or a `Vec2`, or one field at a time:
```
node.pos = (12.5, 0.0)
node.pos = Vec2(5.0, 5.0)
node.pos.x = 99
```
- Tint is for 2D only: there is no tint on Node3D
- `addChild` has a bound on the type: adding a node of the wrong layer is a compile error; if the check is done at runtime it throws an exception
- Decided: a child is created by its parent, `node.addChild(Sprite, texture)`: the type of the node, then the parameters of its constructor. The parent creates the node already complete, so there is never a detached node (no node without context and viewport). The language needs to forward the constructor arguments by type
- `addChild(&@T node)` stays for reparenting an existing node

### 3.2 Sprites
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
- Sprite is only the plain sprite. The sprite sheet is a separate thing: `FramedSprite` only picks a frame (it does not animate: playback is not here), the frame count comes from the texture size divided by texel
- `FramedSprite` is the chosen name (SpriteSheet would read as the texture, MultiSprite as several sprites)
- A background is just a sprite, shapes are drawn in a canvas and shown by a sprite

### 3.3 2D Labels
```
public class Label : Node2D {
  public string text
  public @Font font
}
```
- A label is its own Node2D; the color is the `tint`
- Font size is defined by the font; `scale` can tweak it (scaling an atlas blurs or pixelates at large factors)
- Open: alignment, wrapping or a maximum width

### 3.4 Quaternion
- Unit quaternion for 3D rotation, by value, 4 x r32 (x, y, z, w), layout compatible with C and shaders (`struct<fixed>`)
- Conventions: Hamilton product, right-handed, angles in radians, w is the scalar part
- `Quaternion()` is the identity (no rotation), from the start values of the fields: there is no `identity()` method
- The methods that create a value are static

```
public struct<fixed> Quaternion {
  r32 x = 0,
  r32 y = 0,
  r32 z = 0,
  r32 w = 1                                   // default = identity (no rotation)

  // Creation (static)
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

### 3.5 Transform cache (open design, to be planned well: huge impact on performance)
- Each node has a local matrix and a global matrix (position, rotation, scale, pivot). They are cached and must be updated when a local value or an ancestor changes
- Possibility 1, dirty flags: writing a local value marks the node (local) and its descendants (global) dirty; recompute only when read. The local fields have to be properties with a write accessor, so the write can set the flags
- Possibility 2, version counters: each node bumps a counter on change; a cached global matrix is rebuilt when the counters of the chain differ. Nothing is marked on write, but every read walks up the chain
- Possibility 3, one pass per frame: `ctx.update()` walks the tree top-down and rebuilds the global matrices, with no per-node cache logic. Values read between updates can be stale unless a rebuild is forced
- Setting a global value writes the local field, so it goes through the same marking
- What drives the cost: the workload (mostly static nodes favor dirty flags, mostly moving nodes favor the single pass), the memory layout (a tree of references means pointer chasing, a flat array of global matrices in traversal order is cache friendly), deep trees (marking all the descendants on every write), and the GPU upload (every drawn node needs its matrix in a GPU buffer each frame, so a flat array has to exist anyway)
- Hybrid is possible: dirty flags to skip static subtrees plus a flat array of global matrices that can be uploaded to the GPU in one buffer
- The choice is to be made after measuring on a prototype

### 3.6 Draw order (zindex)
- `zindex` is absolute: it is not relative to the parent, so a child can be drawn below its parent
- Equal values are resolved by tree order: a parent before its children, siblings in the order of the `children` list. The order is then fully defined: sort by `zindex` with a stable sort over a depth-first walk of the tree. The renderer cannot reorder equal values for batching

### 3.7 Meshes
- In gfx.g3d. Vulkan has no mesh object: a mesh is two `VkBuffer`s, one for the vertices and one for the indices. The references to the buffers live inside `MeshData`
- `MeshData` is just the definition of a 3D shape. `Mesh` (what Godot calls `MeshInstance3D`) uses it: it adds the material and places it in the viewport
- `MeshData` is shared, so several `Mesh` nodes can use it, each with its own transform and material. It is counted (`@MeshData`): the buffers are freed when the last usage goes, after the GPU is done with the frames still in flight
- The optional arrays (normals, colors, uvs) are empty when not used; when used they have the same length as `points`. A vertex that needs two UVs or two normals (a hard edge, a texture seam) has to be duplicated
- Decided: lazy loading. A `MeshData` is created without the context. Any change sets `dirty`; the first `Mesh` that renders a dirty `MeshData` uploads it (through a staging buffer into a GPU-local buffer) and marks it clean. The upload goes into a new buffer or waits for the GPU, as the old buffers can still be read by frames in flight. The arrays need write accessors that set the flag, like the transform cache fields (3.5)
- Decided: the bounding sphere (center and radius) is calculated when the data is dirty, and used for culling. A sphere is cheaper to test than a box
- The vertex inputs of a shader follow Godot's names in camelCase (`vertex`, `normal`, `uv`, `color`), see 5.8

```
public class MeshData {
  public arr<Vec3> points
  public arr<u32, u32, u32> triangles    // indices into points
  public arr<Vec3> normals               // optional
  public arr<Color> colors               // optional
  public arr<Vec2> uvs                   // optional
  public readonly bool dirty             // set by any change, cleared after the upload
  public readonly Vec3 boundsCenter      // bounding sphere, calculated when dirty
  public readonly r32 boundsRadius
  handle vertexBuffer                    // VkBuffer, created at the first upload
  handle indexBuffer                     // VkBuffer
}

public class Mesh : Node3D {
  public @MeshData data
  public @Material material             // shared
}
```
The bounding sphere:
```
// pass 1: min and max of the points on each axis -> boundsCenter = (min + max) / 2
// pass 2: the farthest vertex, compared by squared distance
r32 farthestSquared = 0
for point in points {
  r32 distanceSquared = point.subtract(boundsCenter).dot(point.subtract(boundsCenter))
  if distanceSquared > farthestSquared { farthestSquared = distanceSquared }
}
boundsRadius = sqrt(farthestSquared)   // the only square root
```
Undecided:
- The depth test and the transparent meshes. The depth test already hides what is behind opaque pixels, per pixel, with the transparent meshes drawn after the opaque ones with depth write off. The usual "over" blend needs the transparent meshes sorted farthest first (by the distance of the sphere center to the camera). Filtering by the closest opaque mesh was proposed and not adopted for now: it would drop transparent meshes behind it that are still visible beside it
- The default material of a `Mesh` (too early)
- Applying the global matrix to the bounding sphere (with a non uniform scale the largest axis would be used)

Out of the first version: skeletons and bone weights, glTF loading (a JSON file plus binary buffers, Y up, with meshes, materials, textures, nodes, skeletons, animations).

### 3.8 Lights
- In gfx.g3d. Vulkan has no lights: a light is only data (position, direction, color, range) that the shaders read from a buffer. The shading, the falloff and the shape are the shader code
- Two kinds for now: a point light (comes from a point and fades with the distance, a lightbulb) and a directional light (strong, uniform, comes from a direction, the sun). The directional light has no falloff and only its rotation matters
- The falloff of a point light is not defined by the fields: it is a fixed formula in the shader, usually inverse square cut off at `range`
- Shadows are not part of the basic library: if they are calculated, it is defined in the shader
- Possibility: the light values reach the shaders the way Godot does, with reserved names in camelCase (a `light()` run per light with `lightColor`, `attenuation`...), bound automatically by the library
- Possible later: cone (spot) and other kinds

```
public enum LightType { Point, Directional }

public class Light : Node3D {
  public LightType type
  public Color color
  public r32 intensity
  public r32 range        // Point: the distance where it fades to zero, ignored by Directional
}
```
- The ambient light is on the viewport trait (2.4): `ambientColor` and `ambientPower`

## 4. Core math and color types
All by value, in gfx.core.

### 4.1 Vec2, Vec3 and Vec4
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

public struct<fixed> Vec4 {
  r32 x = 0,
  r32 y = 0,
  r32 z = 0,
  r32 w = 0

  unrestricted fun Vec4 add(Vec4 other)
  unrestricted fun Vec4 subtract(Vec4 other)
  unrestricted fun Vec4 scale(r32 factor)
  unrestricted fun r32 dot(Vec4 other)
  unrestricted fun r32 length()
  unrestricted fun Vec4 normalize()
  unrestricted fun r32 distance(Vec4 other)
  unrestricted fun Vec4 lerp(Vec4 target, r32 amount)
}
```
- `Vec4` is the CPU side of the shader `vec4` (5.4); `Color` also maps to `vec4`

### 4.2 Matrix4
- Based on an inline array of 16 r32 (11.1.5.4), column-major: maps directly to a `mat4` in the shaders
- Built for Vulkan clip space (Y down, depth 0..1)
- `Matrix4()` is the identity (same as the quaternion), from the start value of `values`: there is no `identity()` method
- The methods that create a value are static

```
public struct<fixed> Matrix4 {
  arr<r32>[16] values = [1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1]   // column-major, default = identity

  // Creation (static)
  public static fun Matrix4 fromTranslation(Vec3 offset)
  public static fun Matrix4 fromScale(Vec3 factor)
  public static fun Matrix4 fromQuaternion(Quaternion rotation)
  public static fun Matrix4 fromTransform(Vec3 position, Quaternion rotation, Vec3 scale)

  // Camera (static, built for Vulkan clip space: Y down, depth 0..1)
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
- `fromTransform` is the one a node uses to build its model matrix
- For a 2D node the matrix is built the same way with z = 0 and the rotation around Z
- The global position, rotation and scale are read back from the global matrix; with non-uniform parent scale plus rotation a shear appears and cannot be fully read back

### 4.3 Color
- Decided: `Color` holds linear values, with automatic conversion to sRGB when needed
- Four r32 (shaders and clear colors use 32-bit floats), default opaque white so a default tint changes nothing
- `fromHtml` and `fromBytes` take sRGB values and convert them to linear: `#ff0080` is stored as (1.0, 0.0, 0.216); only the blue changes as 0 and 1 are the same in both spaces. The matching `toHtml` and `toBytes` convert back
- With an sRGB framebuffer the hardware converts to sRGB on write, a UNORM framebuffer would convert in the shader
- A GLSL `vec4` has no color space, it is linear in practice only because of the formats around it: sampling an sRGB texture gives linear values, writing to an sRGB framebuffer converts them back
- The negative values trick (a sign to mark non-linear colors) was discarded: `0` has no negative form, any math would have to preserve the sign, and the value would not mean 0..1 at a glance

```
public struct<fixed> Color {
  r32 red = 1,
  r32 green = 1,
  r32 blue = 1,
  r32 alpha = 1

  public static fun Color fromBytes(u8 red, u8 green, u8 blue, u8 alpha)   // sRGB 0..255 converted to linear 0..1
  public static fun Color fromHtml(string text)                            // "RGB", "RRGGBB" or "RRGGBBAA", a leading # is accepted but not required; converted to linear
  unrestricted fun Color lerp(Color target, r32 amount)
  unrestricted fun string toHtml()                                         // possibility: converts back to sRGB
  unrestricted fun u8 red, u8 green, u8 blue, u8 alpha toBytes()           // possibility: converts back to sRGB
}
```
Open: named colors, alpha blending rules, and the format of the framebuffer (sRGB or UNORM).

## 5. Shaders and materials
Preliminary: this part is mostly proposals.

### 5.1 Language and build
- Shaders are written in Dflat: a `shader` block, a class-like declaration mapped directly to a Dflat class
- A shader lives in its own file, with the `.dfsh` extension (e.g. `MyShader.dfsh`)
- Shader code is a subset of Dflat syntax, close to GLSL (not strictly 1:1). Only what translates to GLSL is allowed; anything else is a compile error
- Only functions inside shader blocks run on the GPU. A normal Dflat function is not usable in a shader
- Build: at compile time dfcompiler emits GLSL (Vulkan GLSL) and calls glslc to get SPIR-V. The SPIR-V is part of the program
- SPIR-V is not GPU specific: when the program runs, the driver compiles it to the code of the local card when a pipeline is created, and a pipeline cache keeps that result per machine. That final result is kept only locally (a cache): it is not shipped
- Other targets are reached with a "transpiler": SPIR-V → SPIRV-Cross (GLSL ES, HLSL, MSL) or naga/tint (WGSL)

### 5.2 Shader block
```
shader MyShader {
  public vec4 tint
  public r32 intensity
  fun VertexOutput vertex(VertexInput input) { ... }
  fun FragmentOutput fragment(VertexOutput input) { ... }
}
```
- Stage functions have fixed names: `vertex`, `fragment`, `compute`. The name identifies the stage. One function per stage per block
- Stages now: vertex, fragment, compute. Other modern stages later
- A block is either compute-only or graphics (vertex and/or fragment), never both
- `vertex` is optional: a fragment-only block uses the built-in default vertex stage from gfx. Its output struct is defined by gfx, and the fragment parameter must match it. A screen shader (2.4) only needs the fragment part
- When both are present, the compiler checks that the `vertex` return type and the `fragment` parameter type are the same struct
- The vertex input struct defines the vertex buffer layout (derived). The shader declares no vertex buffer; gfx binds it
- The vertex output struct must contain the field `position` (`vec4`), the screen position
- `fragment` returns a `vec4` for one render target, or a struct (one field per target) for several

```
public class Shader {
  public readonly arr<byte> spirv       // made by dfcompiler, part of the program
}
```
- Each `shader` block becomes a class derived from `Shader`; its `public` fields are its parameters (5.6)

### 5.3 Compute
```
shader MyCompute {
  public SomeType inputPar
  public &ReturnType outputRes
  layout(x=3, y=4, z=1)          // group size
  fun ReturnType compute() {
    ... uses inputPar ...
    return result
  }
}
```
- `layout(x=.., y=.., z=..)` declares the group size (threads per group). Omitted axes default to 1. `layout` is used only for this
- Group size and group count are each 3 numbers (x, y, z); an unused axis is 1. Threads per axis = group size × group count
- The group count is given by the CPU at the call: `res = wait sh.compute(count_x, count_y, count_z)` (async and `wait` are not yet decided in the language, Specs/20)
- `compute` takes no parameters. Its inputs are the `public` fields (set from the CPU on the shader object before the call) and the built-in variables
- Each thread writes its own place in the writable (`&`) resources. The call returns what the threads wrote, after completion, so the CPU knows where to read results

Compute on a buffer:
```
public &arr<vec4>[1024] buffer
layout(x=16, y=16, z=1)
fun arr<vec4> compute() {
  i32 pos = 4 * (globalId.x + globalId.y * 16)
  for i=0..4 {
    buffer[pos + i].r = buffer[pos + i].g
    buffer[pos + i].g = buffer[pos + i].b
  }
  return buffer
}
```
Used as `res = wait sh.compute(1, 1, 1)`.

### 5.4 Types
Type settings: all settings of a type go in `<...>` right after the type, comma-separated. `public` and `&` (writable) are prefixes.
```
public sampler2d<nearest, clamp> mask
public &image2d<rgba8> output
i32<flat> materialId
vec2<noperspective> screenUv
```
This part is under discussion. Another possibility is to use the tags as prefixes and use the <type> to specify the actual types
```
public nearest clamp sampler2d mask
public &image2d<rgba8> output // In this case it is a type so it can stay here
flat i32 materialId
noperspective vec2 screenUv
```

Interpolation (vertex output to fragment input), settings on fields of the shared struct:
```
struct VertexOutput {
  vec4 position
  i32<flat> materialId
  vec2<noperspective> screenUv
}
```
- `smooth`: perspective-correct interpolation (default)
- `flat`: no interpolation, value from one vertex. Required for integer fields (GLSL rule)
- `noperspective`: linear interpolation in screen space
- `centroid`, `sample`: sampling-location variants (multisampling)

Scalar types:
```
r16 r32 r64          float 16 / 32 / 64
i8 i16 i32 i64       signed ints
u8 u16 u32 u64       unsigned ints
bool                 not allowed in buffers (no memory layout)
```
- 32-bit ints and floats and `bool` are available on every device; 8, 16 and 64-bit types need optional device features
- Compiler, knowing the targets can automatically remove or promote types to available ones (see conflicts)
- No `i128`+, `dec`, `char`, `string`

Vector and matrix types:
- GLSL names: `vec2 vec3 vec4`, `ivec2/3/4`, `uvec2/3/4`, `bvec2/3/4`, `mat2 mat3 mat4` (also `mat2x3` etc.)
- They are the same types as the Dflat-style ones in `gfx.core` (4); the GLSL names are the shader-side names. CPU side uses the `gfx.core` types (e.g. `material.tint = Vec4(1, 1.1, 2, 3.5)`)
- Operators work per component; `*` between matrices is matrix multiplication, matrix * vector is supported
- Constructors as in GLSL (`vec4(vec3Value, 1.0)`)
- Swizzles with `xyzw` and `rgba` (no mixing in one swizzle): `a.rgb`, `a.xyz`, `a.bgra`
  - Reads may repeat or reorder components (`a.xx`)
  - Assignment may not repeat a component (`a.xy = ...` valid, `a.xx = ...` error)
  - The result type follows the component count (`a.rgb` is `vec3`, `a.x` is scalar)
- Bit reinterpretation uses Dflat transmute `[type]x`

Arrays: `arr<T>[n]` as in the language (`arr<vec4>[8]`, `public &arr<vec4>[1024] buffer`).

### 5.5 Inputs and resources
All external inputs are `public` fields (no `uniform` keyword).
```
public vec4 tint                       // value
public arr<vec4>[64] colors            // read-only buffer
public &arr<vec4>[1024] results        // writable buffer
public sampler2d albedo                // sampled texture, read-only
public &image2d<rgba8> output          // image, writable, format in the type
public sampler2d<nearest, clamp> mask  // sampler override
```
- Sampler settings have a default on the CPU side (gfx); the shader can override them as above
- Other texture kinds as in GLSL (`sampler3d`, `samplerCube`, arrays)
- Only images declare a format. Valid image formats (GLSL list):
```
float:  rgba32f rgba16f rg32f rg16f r11f_g11f_b10f r32f r16f
unorm:  rgba16 rgb10_a2 rgba8 rg16 rg8 r16 r8
snorm:  rgba16_snorm rgba8_snorm rg16_snorm rg8_snorm r16_snorm r8_snorm
int:    rgba32i rgba16i rgba8i rg32i rg16i rg8i r32i r16i r8i
uint:   rgba32ui rgba16ui rgb10_a2ui rgba8ui rg32ui rg16ui rg8ui r32ui r16ui r8ui
```
- Structs used in buffers use the existing `<fixed>` tag; no new GPU-layout tag (see open items)
- The compiler generates the uniform block, the set and binding numbers from the `public` fields

### 5.6 Materials and shader holders
- A material is just a container of the shader parameters. It follows the Godot and Unity philosophy: a shared shader plus its own values. No predefined properties (no albedo, emissive...)
- Most of the time the material is a default one (for example sprites), a custom one can be added. Sprites, meshes and viewports hold a material
- The parameters are generated automatically by the compiler: every `public` field of the shader becomes a typed field of the `ShaderHolder` class that owns the shader. Assignments are checked at compile time
- All the parameters get default values: the initial value of the field in the shader, or the zero value of the type
- What cannot be checked at compile time (for example the vertex data of a mesh against the vertex input struct, or the driver failing the final compilation) raises a runtime exception (names to define)

```
public trait ShaderHolder<S : Shader> {
  public @S shader                      // shared, null = no shader
  // + one typed field for each public field of S, generated by the compiler
}

public class Material<S : Shader> : ShaderHolder<S> {
  ...
}
```
```
Material<MyShader> m = Material<MyShader>()
m.tint = Vec4(1, 1.1, 2, 3.5)           // checked at compile time against MyShader.tint
```
- `ShaderHolder` is defined once and applied to `Material` and to the viewports (2.4)
- Colors: setting a parameter from a `Color` stores it as is (it is already linear); setting it from raw floats is never converted. The texture color space comes from the texture format. So no `source_color` hint is needed
- The built-in default shaders define their own parameter names (like `mainTexture` and `color`) as a plain naming convention. Model loaders (glTF, later) would have to map their material data onto the names a shader exposes
- Possible later: a per-object override on top of a shared material (Godot instance uniforms, Unity property blocks)

### 5.7 Reuse and built-in functions
- A shader can define `public static` functions that other shaders call: `Colors.gammaCorrect(color)`. Both `public` and `static` are required

Compiler built-ins, each valid only in the stages that allow it. Names follow the existing Dflat math names, which already mostly match GLSL; the few differences are mapped by the compiler. `g` = `r32` or vector (per component).
```
trig:      radians(g) degrees(g) sin cos tan asin acos atan(g) atan(y, x)
           sinh cosh tanh asinh acosh atanh
exp:       pow(g, g) exp exp2 log log2 sqrt inversesqrt
common:    abs sign floor ceil trunc round roundEven fract
           mod(g, g|r32) modf min(g, g|r32) max(g, g|r32) clamp(g, lo, hi)
           mix(g, g, g|r32|bool) step(edge, g) smoothstep(lo, hi, g)
           fma(g, g, g) isnan isinf
geometry:  length distance(g, g) dot(g, g) cross(vec3, vec3) normalize
           faceforward(n, i, nref) reflect(i, n) refract(i, n, eta)
matrix:    matrixCompMult(m, m) outerProduct(v, v) transpose(m)
           determinant(m) inverse(m)
compare:   lessThan lessThanEqual greaterThan greaterThanEqual equal notEqual
           any(bvec) all(bvec) not(bvec)
integer:   bitCount findLSB findMSB bitfieldExtract bitfieldInsert
           bitfieldReverse uaddCarry usubBorrow umulExtended
texture:   texture textureLod textureGrad textureProj textureOffset
           texelFetch textureSize textureQueryLod textureGather
image:     imageLoad imageStore imageSize
derivs:    dFdx dFdy fwidth (+ Coarse / Fine variants)
atomics:   atomicAdd atomicMin atomicMax atomicAnd atomicOr atomicXor
           atomicExchange atomicCompSwap (also imageAtomicAdd...)
sync:      barrier memoryBarrier groupMemoryBarrier
```
Stage restrictions (GLSL rules): `dFdx dFdy fwidth` and `texture` (implicit detail level) in `fragment` only; `barrier memoryBarrier groupMemoryBarrier` in `compute` only.

### 5.8 Built-in variables and inputs
Fixed camelCase names, no prefix. The compiler recognizes them in the source and gfx binds them automatically; the material parameters stay separate.
```
vertex:    vertexIndex, instanceIndex
fragment:  fragCoord, frontFacing
compute:   globalId, localId, workGroupId      (each 3 numbers: x, y, z)
all:       time, modelMatrix, viewMatrix, projectionMatrix, viewportSize
           texture, uv, color (the node tint), vertex, normal (the attributes of a mesh)
```
- The inputs follow the names of Godot (in camelCase), so people can use their existing experience. The coordinate system is Vulkan's (Y down, Z forward) while Godot's 3D is Y up with -Z forward, so some names match but the values do not behave exactly the same in 3D
- Alternative (not chosen): a standard header prepended to every shader with fixed blocks (per frame and camera in set 0, the material in set 1, per node in push constants)

`time`:
- `time` is an `r64` on the CPU side and a 32-bit float in the shader (a 32-bit float has no more precision than that, and 64-bit float uniforms are not available on every GPU)
- It is derived from `Root.Time.clock()`, a `u64` monotonic counter with a resolution of 0.1 ns that never returns 0. Seconds are (ticks - ticks at context creation) / 10^10, so it starts at 0 and stays small
- Decided: it wraps like Godot's, but with simple math: the ticks are masked with 2^45 - 1, which wraps every 58.6 minutes (3518 s), then one multiply by 10^-10 gives seconds. A mask and a multiply, no division or modulo. The precision of a 32-bit float at that size is about 0.24 ms (the step between two values just before the wrap)
- Anything using `time` jumps at the wrap, unless it is periodic with a period that divides the wrap

```
// pseudo code
u64 ticks = (Root.Time.clock() - startTicks) & ((1 << 45) - 1)   // startTicks is taken when the context is created
r64 time = ticks * 0.0000000001
```

### 5.9 Sketches (as written by the user)
Shader with uniforms and one stage (as in the latest upload):
```
shader MyShader {
  public vec4 tint
  public r32 intensity

  fun vec4 fragment() { ... }
}
```
Same sketch, original wording:
```
shader MyShader {
  public vec4 tint
  public r32 intensity
  fragment() {
    ...
  }
}
```
All stages in one block:
```
shader MyShader {
  public type someuniformvar
  fun sharedstruct vertex(input) { ... }
  fun output fragment(sharedstruct) { ... }
}
```
Shared struct between vertex and fragment:
```
struct vin { ... }
struct vout { ... }
fun vout fragment(vin in) { // vertex not used
  ...
  return vout
}
```
Stage signatures; the fragment-only form is legit (vertex not used):
```
fun sharedstruct vertex(inputstruct)
fun outputstruct fragment(sharedstruct)

fun outstruct fragment(inputstruct)
```
CPU side:
```
material.tint = Vec4(1, 1.1, 2, 3.5)
```
Compute with a public field and group count (latest upload):
```
shader test {
  public sometype inputPar
  layout(x=3, y=4, z=1) // group size
  fun compute() {
    ... use inputPar ...
  }
}
test.inputPar = abc
test.compute(3, 2, 1) // groupcount
```
Compute with a public field and group count, original wording:
```
shader test {
  public sometype inputPar
  layout(x=3, y=4, z=1) // group size
  compute() {
    ... use inputPar ...
  }
}
test.inputPar = abc
test.compute(groupCount)
```
Compute on a buffer, original sketch:
```
public buffer<vec4>[1024]
layout(x=16, y=16, z=1)
fun vec4[] compute() {
  i32 pos = globalId * 4
  for i=0..4 {
    buffer[pos + i].r = buffer[pos + i].g
    buffer[pos + i].g = buffer[pos + i].b
  }
  return buffer
}
```
Used as `res = wait sh.compute(1, 1, 1)`.

### 5.10 Conflicts and open items
CONFLICTS (both versions kept, not resolved):
1. Arrays of unknown length (only as buffer resources): `vec4[]` (earlier upload and original compute sketch) or another form, such as `arr<vec4>` with no size. Fixed length uses `arr<T>[n]` (5.4)
2. Group count at the call: `compute(count_x, count_y, count_z)`, `compute(1, 1, 1)`, `compute(3, 2, 1)` vs `compute(groupCount)` (original sketch)
3. Compute return type: `fun ReturnType compute()`, `fun arr<vec4> compute()`, `fun vec4[] compute()` (earlier upload, original sketch) vs `fun compute()` with no return type, `compute()` (sketches)
4. Sketch `fun vout fragment(vin in)`: fragment takes `vin` and returns `vout`, whereas 5.2 says a fragment takes the vertex output struct
5. Type settings notation: `<...>` after the type vs tags as prefixes (under discussion). The other sections use only `<...>`
6. Scalar types: "compiler can remove or promote types to available ones" (earlier upload only) vs "8, 16 and 64-bit types need optional device features" and the open items on layout and alignment (promotion changes size in buffers)
7. Compute example declares `outputRes` but returns `result`
8. Index computation in the compute buffer example: `i32 pos = 4 * (globalId.x + globalId.y * 16)` vs `i32 pos = globalId * 4` (original sketch)

OPEN ITEMS:
- Built-in names that clash: the input `vertex` with the stage function `vertex`, the input `texture` with the function `texture`
- Where the parameter values live: on the holder for graphics (`Material<MyShader>`), on the shader object for compute (`test.inputPar = abc`)
- The common type for the holders: a `Mesh` holds `@Material`, a viewport is a `ShaderHolder`; with `Material<S>` and `ShaderHolder<S>` they need a non-generic base or a generic node and viewport
- Names of the runtime exceptions for a shader that does not match
- GPU buffers must be clearly defined (storage buffers vs textures used as data buffers)
- Layout of `<fixed>` structs in shader buffers: std140 vs std430 (check or specify)
- Alignment and padding on CPU and GPU (vectors, matrices, arrays, nested structs) must be fully specified
- Compute: how the return type of `compute` is declared and tied to the returned resource; how a returned buffer reaches the CPU (readback, gfx); synchronization between computes and draws
- Exact shader subset (types, control flow, what is forbidden)
- Integer interface field without `<flat>`: compile error or implicit flat
- Error reporting (compiler errors and glslc errors mapped to source lines)
- Default vertex output struct (gfx)
- Other stages (geometry, tessellation, ray tracing, mesh)

## 6. Text console
- Just an idea, not sure it is needed: when you just need a "shell window", maybe for low hardware that has only text modes
- It would live in Core and use the fonts

# Open items
- UINode contents and the UI layout and styling. Notes, to be specified:
  - A tree of nodes like the other viewports, similar to the DOM of a browser
  - Containers (hbox, grid) and executors (button, label, slider), with some style defined for them
  - Events linkable directly to callbacks (a click on a button calls a function)
  - Rendered as a regular framebuffer and overlapped to the other viewports
  - The main problem is detecting the interaction with the user
- 3D content: undecided depth test and transparent meshes, default material of a `Mesh`, the light values in the shaders, applying the global matrix to the bounding sphere (3.7, 3.8); out of the first version: skeletons, glTF loading, shadows
- Init parameters (probably none)
- Canvas class and its parameters, the readback, batching
- Color: framebuffer format (UNORM or sRGB), named colors, alpha blending rules
- 2D units
- Transform cache choice (3.5)
- Window: the default background color
- Window event: close semantics
- Shaders: see 5.10
- Fonts: how an atlas is created without TTF reading (for example from an image and a character grid)
- Sprite sheet (FramedSprite stands in for now), Label alignment and wrapping, text console
- Rendering internals: frame cycle, batching, sorting, culling
- Threading: which tasks may call gfx, and where the OS messages are pumped
- Lifetime: `flush`, shutdown
- The WebGPU backend: how it is selected and packaged inside the single library
