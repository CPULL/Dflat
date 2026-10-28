#version 450
// the vertices are already in world coordinates (gfx.c transforms the quads); the push constant maps the world to the viewport

layout(location = 0) in vec2 inPos;
layout(location = 1) in vec2 inUV;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 fragUV;
layout(location = 1) out vec4 fragColor;

layout(push_constant) uniform Push {
    mat4 ortho;
} pc;

void main() {
    gl_Position = pc.ortho * vec4(inPos, 0.0, 1.0);
    fragUV = inUV;
    fragColor = inColor;
}
