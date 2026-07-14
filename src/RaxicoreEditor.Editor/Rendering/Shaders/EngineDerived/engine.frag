#version 450
// Engine-derived material shading — opaque/cutout fragment half (the translated "simplepixel" technique:
// interpolated vertex colour × texture). Keeps the era-correct alpha-test cutout the generic mesh shader
// uses, so alpha-keyed foliage/grates/fences still punch through; opaque textures decode to alpha 1.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D tex0;

void main() {
    vec4 albedo = texture(tex0, vUv);
    if (albedo.a < 0.5) {
        discard;
    }
    outColor = vec4(vColor.rgb * albedo.rgb, 1.0);
}
