#version 460
#extension GL_EXT_ray_tracing : require
#extension GL_EXT_buffer_reference2 : require
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

layout(location = 0) rayPayloadInEXT vec3 hitColor;
hitAttributeEXT vec2 attribs;

// Same stride-14 interleaved vertex layout the rasterizer uses (see MeshViewportRenderer's
// VertexInputAttributeDescription table): pos.xyz(0-2), normal.xyz(3-5), uv.xy(6-7), color.rgba(8-11),
// detailUv.xy(12-13, unused here -- V1 shades from baked vertex colour, not sampled textures).
// std430 packs a flat float/uint array with no cross-element padding, so this matches the host buffer
// byte-for-byte with no layout qualifier games needed.
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer VertexBuf { float v[]; };
layout(buffer_reference, std430, buffer_reference_align = 4) readonly buffer IndexBuf { uint i[]; };

struct InstanceDesc { uint64_t vertexAddr; uint64_t indexAddr; };
layout(set = 0, binding = 2, std430) readonly buffer InstanceDescs { InstanceDesc descs[]; };

vec3 fetchPos(VertexBuf vb, uint vi) { return vec3(vb.v[vi * 14 + 0], vb.v[vi * 14 + 1], vb.v[vi * 14 + 2]); }
vec3 fetchNormal(VertexBuf vb, uint vi) { return vec3(vb.v[vi * 14 + 3], vb.v[vi * 14 + 4], vb.v[vi * 14 + 5]); }
vec4 fetchColor(VertexBuf vb, uint vi) { return vec4(vb.v[vi * 14 + 8], vb.v[vi * 14 + 9], vb.v[vi * 14 + 10], vb.v[vi * 14 + 11]); }

void main() {
    InstanceDesc d = descs[nonuniformEXT(gl_InstanceCustomIndexEXT)];
    VertexBuf vb = VertexBuf(d.vertexAddr);
    IndexBuf ib = IndexBuf(d.indexAddr);

    uint i0 = ib.i[gl_PrimitiveID * 3 + 0];
    uint i1 = ib.i[gl_PrimitiveID * 3 + 1];
    uint i2 = ib.i[gl_PrimitiveID * 3 + 2];

    vec3 bary = vec3(1.0 - attribs.x - attribs.y, attribs.x, attribs.y);

    vec3 n = fetchNormal(vb, i0) * bary.x + fetchNormal(vb, i1) * bary.y + fetchNormal(vb, i2) * bary.z;
    vec4 albedo = fetchColor(vb, i0) * bary.x + fetchColor(vb, i1) * bary.y + fetchColor(vb, i2) * bary.z;

    // World-space normal: instance transforms are identity (see MeshViewportRenderer's TLAS build), so
    // object-space normals are already world-space -- no normal matrix needed.
    n = normalize(n);
    if (dot(n, gl_WorldRayDirectionEXT) > 0.0) {
        n = -n; // two-sided, matching the rasterizer's gl_FrontFacing flip (culling is off there too)
    }

    // Same key/hemi lighting formula as mesh.frag, using baked vertex colour instead of a sampled texture
    // (V1 scope -- texture sampling in the hit shader is a fast-follow, not yet wired).
    vec3 keyDir = normalize(vec3(0.4, 0.8, 0.35));
    float key = max(dot(n, keyDir), 0.0);
    float hemi = 0.5 + 0.5 * n.y;
    float light = 0.35 + 0.55 * key + 0.12 * hemi;

    hitColor = albedo.rgb * light;
}
