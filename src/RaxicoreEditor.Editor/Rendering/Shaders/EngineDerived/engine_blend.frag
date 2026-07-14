#version 450
// Engine-derived material shading — translucent-overlay fragment half (shield domes, energy beams,
// shoreline foam). Same "simplepixel" modulation as engine.frag, but outputs the texture's real alpha so
// the blend-enabled pipeline composites it over the opaque pass instead of alpha-testing it away.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D tex0;

void main() {
    vec4 albedo = texture(tex0, vUv);
    outColor = vec4(vColor.rgb * albedo.rgb, albedo.a);
}
