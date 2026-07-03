#version 450
// GLSL translation of the engine-derived "position" vertex technique: the simplest of the set —
// a plain transform plus vertex-color/UV passthrough, no lighting applied at all. The original
// declared lightDir/lightColor/ambientColor parameters but never referenced them in its body
// (kept here as comments only, matching the source's unused-parameter signature).

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec4 inColor0;
layout(location = 3) in vec2 inTexCoord0;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform PositionParams {
    mat3 objectMatrix;   // unused by this technique's body — preserved for signature fidelity
    mat4 objViewProjMatrix;
    // lightDir, lightColor, ambientColor: declared in the original, never read.
} params;

void main() {
    gl_Position = params.objViewProjMatrix * vec4(inPosition, 1.0);
    vColor = inColor0;
    vTexCoord0 = inTexCoord0;
}
