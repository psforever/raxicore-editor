#version 450

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D tex0;

// Translucent overlay pass (shield domes, energy beams, shoreline foam — the engine-derived "mask"
// materials): unlike mesh.frag's hard alpha-test cutout, this outputs the texture's real alpha so the
// blend-enabled pipeline can composite it over whatever the opaque pass already drew beneath it.
void main() {
    vec4 albedo = texture(tex0, vUv);
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) {
        n = -n;
    }
    vec3 keyDir = normalize(vec3(0.4, 0.8, 0.35));
    float key = max(dot(n, keyDir), 0.0);
    float hemi = 0.5 + 0.5 * n.y;
    float light = 0.35 + 0.55 * key + 0.12 * hemi;
    outColor = vec4(albedo.rgb * light, albedo.a);
}
