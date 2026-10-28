# Gfx — the C core

The C side of the Dflat `gfx` library (spec: [../dflat-gfx_spec.md](../dflat-gfx_spec.md)). The Dflat classes call it through `extern fun`
(Dflat spec 15.9), so the API in `gfx.h` takes only numbers, `bool`, `void*` (the context), `uint64_t` (the handles), UTF-8 strings and
arrays as pointer + length.

## Files

- `gfx.h`, `gfx.c`: the API and its implementation (Vulkan 1.1, GLFW 3.3).
- `shaders/shader.vert`, `shaders/shader.frag`: the GLSL.
- `gfx_vert.h`, `gfx_frag.h`: the shaders as SPIR-V arrays, made by `make_shaders.ps1` (it needs `glslangValidator` of the Vulkan SDK). They
  are generated files: run the script again after a change of the GLSL.

## Building

The Dflat compiler builds it by itself when a program imports `gfx` (Compiler_Options.md, "Native libraries"): it compiles every `*.c` of
this folder and links the Vulkan SDK and GLFW. It finds them through `vulkan-sdk=` / `glfw=` in the project file, else `VULKAN_SDK` /
`C:\VulkanSDK\<version>` and `GLFW_DIR`, else the system packages on Linux.

## How it works

- Handles: `uint64_t`, the generation in the high 32 bits, then the kind (8 bits) and the slot (24 bits); 0 is never valid. Every object
  lives in a pool of slots of the context; a released slot gets a new generation, so the old handles become stale (`gfx_valid`), and any
  call on a stale handle does nothing and returns 0 / false.
- The context owns the Vulkan instance and the device (made at once, so textures can exist before the window); the window owns the
  surface, the swapchain, the render pass, the pipeline and the per-frame resources (one vertex buffer per frame in flight, one
  render-finished semaphore per swapchain image).
- A frame: the visible sprites and labels sorted by `order`, then creation; each quad is scaled, rotated around its pivot and moved on the
  CPU; consecutive quads with the same texture are one draw. The bars of the scaling modes are black, the world starts from the clear color.
- Resize modes: `KeepPixelSize` (one world unit is one pixel), `Stretch`, `Fit`, `FitWidth`, `FitHeight` scale the world from its size at
  creation; `integerScale` rounds the scale down to a whole number; `None` makes the window not resizable.
- Releasing a texture or changing its data or its filter waits for the GPU first (simple, not fast).

## Tested

2026-10-28, Windows, clang of Visual Studio, Vulkan SDK 1.4.363.0, GLFW 3.5.1 (glfw3_mt.lib), NVIDIA driver: gfx.c compiles with no
warning (-Wall -Wextra, without -Wmissing-field-initializers: the Vulkan structs are zero-initialized on purpose). A C test program (a
texture before the window, a sprite moving and rotating, a sprite with a source rectangle, a label, the Fit mode, 180 frames, then
release, stale handles, a reused slot, a released texture with a sprite still using it) runs with the Khronos validation layer active and
reports no error. The picture itself was not checked by eye yet.
