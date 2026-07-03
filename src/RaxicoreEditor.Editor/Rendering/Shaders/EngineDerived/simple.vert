#version 450
// GLSL translation of the engine-derived "simple" vertex technique: an unlit textured pass —
// transforms position, passes the UV through, and outputs a constant white vertex color (i.e.
// "full bright", texture-only shading; pairs with a texture-modulate fragment shader such as
// simplepixel.frag).
//
// The original also computed a world-space normal (via a WorldIT inverse-transpose matrix) and a
// world-space position (via a World matrix) — but neither ever fed into HPosition/TexCoord0/COL0,
// so both were dead code. They're dropped here rather than translated: the original declared
// `World` as a non-square `float3x4` (3 rows, affine — no projective row), which doesn't map onto
// GLSL's square `matN` types, and since the value was unused there was nothing worth preserving.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec2 inTexCoord0;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform SimpleParams {
    mat4 worldViewProj;
} params;

void main() {
    gl_Position = params.worldViewProj * vec4(inPosition, 1.0);
    vTexCoord0 = inTexCoord0;
    vColor = vec4(1.0, 1.0, 1.0, 1.0);
}
