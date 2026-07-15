#version 450
// Textured procedural sky: samples a zone's equirectangular sky-dome panorama (the engine's skydomeNN
// texture — its painted nebula/planets/stars/dust) by the per-pixel world view ray. The ray is unprojected
// from NDC via the inverse translation-stripped view-projection, then converted to spherical UVs. A tint
// (from the zone's ambient lighting) modulates it so the sky matches the continent's time-of-day mood.
layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D skyTex;

layout(push_constant) uniform Push {
    mat4 invRayVp;  // inverse of (view-rotation * projection)
    vec4 tint;      // rgb: colour multiply; w: unused
    vec4 horizon;   // rgb: the zone's atmosphere/fog colour (light3d) — the procedural falloff below the dome
} pc;

const float PI = 3.14159265359;

void main() {
    // The camera projection (OrbitCamera) already carries the Vulkan Y-flip, so the reconstructed ray from
    // the raw NDC is correctly oriented — no extra flip here.
    vec4 wp = pc.invRayVp * vec4(vNdc, 1.0, 1.0);
    vec3 ray = normalize(wp.xyz / wp.w);

    // The engine sky-dome maps the FULL texture height over a hemisphere (horizon→zenith), not a full
    // sphere — the panorama's top row is the horizon, its bottom row the zenith. Matching that uses the
    // whole texture for the visible sky (double the vertical resolution vs a sphere mapping).
    float u = atan(ray.z, ray.x) / (2.0 * PI) + 0.5;
    float v = asin(clamp(ray.y, 0.0, 1.0)) / (PI * 0.5);      // 0 at horizon (top), 1 at zenith (bottom)
    // sampled as UNORM (raw sRGB bytes) → linearize so the sRGB framebuffer's re-encode restores the colour
    vec3 pano = pow(texture(skyTex, vec2(u, v)).rgb, vec3(2.2));

    // Below the horizon the dome has no data. Fall off into the zone's procedural atmosphere gradient — its
    // fog colour fading to a dark ground — a UNIFORM per-column colour, so nothing streaks into the void,
    // and it matches the earlier procedural look. Blend the panorama into it across the horizon.
    vec3 grad = mix(pc.horizon.rgb, pc.horizon.rgb * 0.06, clamp(-ray.y * 5.0, 0.0, 1.0));
    float blend = smoothstep(-0.02, 0.06, ray.y);            // 0 below ~-1°, 1 above ~+3.5°
    vec3 col = mix(grad, pano, blend);

    outColor = vec4(col * pc.tint.rgb, 1.0);
}
