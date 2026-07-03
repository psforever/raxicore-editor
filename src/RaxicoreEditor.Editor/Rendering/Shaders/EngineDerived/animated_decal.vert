#version 450
// GLSL translation of the engine-derived "animated decal" vertex technique: identical to
// decal.vert, but the UV is scrolled/scaled by a per-draw offset+scale uniform before use — the
// technique behind scrolling energy/water-style surface effects. Pairs with simplepixel.frag.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec2 inTexCoord0;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform AnimatedDecalParams {
    mat4 worldViewProj;
    vec4 ambientColor;
    // xy = UV offset, zw = UV scale — matches the original TextureOffset uniform's layout.
    vec4 textureOffset;
} params;

void main() {
    gl_Position = params.worldViewProj * vec4(inPosition, 1.0);
    vTexCoord0 = inTexCoord0 * params.textureOffset.zw + params.textureOffset.xy;
    vColor = params.ambientColor;
}
