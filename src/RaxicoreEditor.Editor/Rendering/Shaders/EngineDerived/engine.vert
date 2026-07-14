#version 450
// Engine-derived material shading — the vertex half of the viewport's optional "engine shaders" mode.
// It fuses two of the translated techniques and picks between them per vertex from the data itself:
//
//   * pre-lit geometry (the "position" + "simplepixel" path) arrives with a baked vertex colour and a
//     ZERO normal — its lighting is already in the colour, so the colour passes straight through;
//   * normal-bearing geometry (the "vertexlight" path) arrives with a real normal and a white colour —
//     it gets one directional light plus a flat ambient term, computed per vertex.
//
// Matching the app's existing pipeline interface (push-constant mvp/model, sampler at set 0 binding 0)
// so it reuses the same pipeline layout; only the shading differs from the generic mesh shader.

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inColor;

layout(push_constant) uniform Push {
    mat4 mvp;
    mat4 model;
} pc;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

void main() {
    gl_Position = pc.mvp * vec4(inPos, 1.0);
    vUv = inUv;

    if (dot(inNormal, inNormal) < 1e-4) {
        // Pre-lit: the baked vertex colour already encodes the lighting.
        vColor = inColor;
    } else {
        // vertexlight: one directional light + ambient floor (the ambient keeps back-facing tris off
        // pure black, since the viewport draws two-sided with culling off).
        vec3 n = normalize(mat3(pc.model) * inNormal);
        vec3 lightDir = normalize(vec3(0.4, 0.8, 0.35));
        float diff = max(dot(n, lightDir), 0.0);
        vColor = vec4(inColor.rgb * (vec3(0.35) + vec3(0.9) * diff), inColor.a);
    }
}
