#version 450

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vDetailUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D tex0;
// The tiled detail texture (materials.adb's mat_detail -- terrain, some doors/trees). Untextured
// submeshes bind a neutral 50%-grey 1×1, so the modulate2x blend below is a no-op (grey * 2 = 1.0) with
// no shader branch needed to distinguish "has a detail texture" from "doesn't".
layout(set = 0, binding = 1) uniform sampler2D tex1;

// Two-sided lighting × texture albedo. The viewport draws with culling OFF (engine-derived sections lack a
// reliable per-face winding), so we shade whichever side faces the camera (gl_FrontFacing flips the
// normal) with an ambient floor. Untextured submeshes bind a 1×1 white texture, so the result collapses
// to plain shaded geometry (the previous look) with no special-casing here.
void main() {
    vec4 albedo = texture(tex0, vUv);
    // Alpha test (era-correct cutout): engine-derived foliage/flags/grates/fences use alpha-keyed
    // textures. Without this the transparent texels render as opaque garbage. Order-independent
    // (no blending needed). Opaque textures decode to alpha 1, so they always pass.
    if (albedo.a < 0.5) {
        discard;
    }
    // Detail-texture blend (era-correct D3D8 "modulate2x" stage op): doubling the detail sample around
    // its 0.5 grey midpoint lets it add or remove contrast/grain without darkening the whole surface.
    vec3 detail = texture(tex1, vDetailUv).rgb;
    albedo.rgb *= detail * 2.0;
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) {
        n = -n;
    }
    vec3 keyDir = normalize(vec3(0.4, 0.8, 0.35));
    float key = max(dot(n, keyDir), 0.0);
    float hemi = 0.5 + 0.5 * n.y;
    float light = 0.35 + 0.55 * key + 0.12 * hemi;
    outColor = vec4(albedo.rgb * light, 1.0);
}
