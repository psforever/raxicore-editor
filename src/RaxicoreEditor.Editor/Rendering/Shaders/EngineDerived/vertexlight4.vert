#version 450
// GLSL translation of the engine-derived "vertexlight4" technique: per-vertex Lambertian diffuse
// lighting from 4 directional lights, plus a flat ambient term. Untextured. Preserved exactly as
// authored: the 4 per-light diffuse terms are COMBINED MULTIPLICATIVELY (not summed) before the
// ambient term is added — that's the original source's actual math, not a translation error.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inColor0;

layout(location = 0) out vec4 vColor;

layout(set = 0, binding = 0) uniform VertexLight4Params {
    mat3 objectMatrix;
    mat4 objViewProjMatrix;
    vec3 lightDir[4];
    vec3 lightColor[4];
    vec3 ambientColor;
} params;

// Mirrors the original's own `diffuse(normal, lightv, color)` helper.
vec3 diffuseTerm(vec3 normal, vec3 lightDir, vec3 color) {
    return max(dot(normal, lightDir), 0.0) * color;
}

void main() {
    gl_Position = params.objViewProjMatrix * vec4(inPosition, 1.0);

    vec3 worldNormal = params.objectMatrix * inNormal;

    vec3 lit = inColor0.rgb
        * diffuseTerm(worldNormal, params.lightDir[0], params.lightColor[0])
        * diffuseTerm(worldNormal, params.lightDir[1], params.lightColor[1])
        * diffuseTerm(worldNormal, params.lightDir[2], params.lightColor[2])
        * diffuseTerm(worldNormal, params.lightDir[3], params.lightColor[3])
        + params.ambientColor;

    vColor = vec4(lit, inColor0.a);
}
