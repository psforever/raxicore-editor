#version 450
// GLSL translation of the engine-derived "decal" vertex technique: transforms position, passes
// the UV through untouched, and outputs a constant per-draw tint (no per-vertex color/lighting).
// Pairs with simplepixel.frag in the original material set.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec2 inTexCoord0;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform DecalParams {
    mat4 worldViewProj;
    vec4 ambientColor;
} params;

void main() {
    gl_Position = params.worldViewProj * vec4(inPosition, 1.0);
    vTexCoord0 = inTexCoord0;
    vColor = params.ambientColor;
}
