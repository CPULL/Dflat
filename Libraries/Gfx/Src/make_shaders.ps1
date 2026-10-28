# Compiles shaders\shader.vert and shaders\shader.frag to SPIR-V and writes them as C arrays: gfx_vert.h (gfx_vert_spv) and gfx_frag.h
# (gfx_frag_spv), which gfx.c includes. Run it after a change of the GLSL. It needs glslangValidator from the Vulkan SDK (VULKAN_SDK).

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = if ($env:VULKAN_SDK) { Join-Path $env:VULKAN_SDK 'Bin\glslangValidator.exe' } else { 'glslangValidator' }

& $tool -V --vn gfx_vert_spv -o (Join-Path $here 'gfx_vert.h') (Join-Path $here 'shaders\shader.vert')
if ($LASTEXITCODE -ne 0) { Write-Host 'shader.vert failed' -ForegroundColor Red; exit 1 }
& $tool -V --vn gfx_frag_spv -o (Join-Path $here 'gfx_frag.h') (Join-Path $here 'shaders\shader.frag')
if ($LASTEXITCODE -ne 0) { Write-Host 'shader.frag failed' -ForegroundColor Red; exit 1 }
Write-Host 'gfx_vert.h and gfx_frag.h written'
