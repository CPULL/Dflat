#version 450
// the texture times the vertex color (white for sprites, the color of a label)

layout(location = 0) in vec2 fragUV;
layout(location = 1) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

layout(binding = 0) uniform sampler2D tex;

void main() {
    outColor = texture(tex, fragUV) * fragColor;
}
