#version 450
// GLSL translation of the engine-derived "vertexlight" technique: per-vertex Lambertian diffuse
// lighting from a single directional light, plus a flat ambient term. Untextured — output is a
// lit vertex color only (light/ambient colors are assumed pre-multiplied by their coefficients,
// matching the original's documented assumption).

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inColor0;

layout(location = 0) out vec4 vColor;

layout(set = 0, binding = 0) uniform VertexLightParams {
    mat3 objectMatrix;
    mat4 objViewProjMatrix;
    vec3 lightDir;
    vec3 lightColor;
    vec3 ambientColor;
} params;

void main() {
    gl_Position = params.objViewProjMatrix * vec4(inPosition, 1.0);

    vec3 worldNormal = params.objectMatrix * inNormal;
    float diffuse = max(dot(worldNormal, params.lightDir), 0.0);

    vColor = vec4(inColor0.rgb * diffuse * params.lightColor + params.ambientColor, inColor0.a);
}
