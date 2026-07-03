#version 450
// GLSL translation of the engine-derived "shadow" vertex technique: a projected-texture blob
// shadow. The vertex's WORLD-SPACE position (not its own UV) is projected through a separate
// projector matrix to generate the UV that samples a shadow/blob texture — the classic technique
// for casting a decal-style shadow onto the ground beneath an object.

layout(location = 0) in vec3 inPosition;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec4 vTexCoord0;

layout(set = 0, binding = 0) uniform ShadowParams {
    mat4 worldViewProj;
    mat4 world;
    mat4 texTransform;
    vec4 ambientColor;
} params;

void main() {
    vec4 tempPos = vec4(inPosition, 1.0);
    vec4 worldPos = params.world * tempPos;

    gl_Position = params.worldViewProj * tempPos;
    vTexCoord0 = params.texTransform * worldPos;
    vColor = params.ambientColor;
}
