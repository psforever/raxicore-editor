#version 450
// GLSL translation of the engine-derived "stealth" vertex technique. Functionally identical to
// decal.vert (transform + UV passthrough + a constant per-draw tint) — the original was written
// self-contained against the Cg NV_vertex_program20 built-in profile types instead of the shared
// common.cg structs, but the logic is the same. Likely the cloak/tint effect for stealthed
// units — the actual transparency would have come from D3D8 render/blend state, not the shader.

layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec2 inTexCoord0;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform StealthParams {
    mat4 worldViewProj;
    vec4 ambientColor;
} params;

void main() {
    gl_Position = params.worldViewProj * vec4(inPosition, 1.0);
    vTexCoord0 = inTexCoord0;
    vColor = params.ambientColor;
}
