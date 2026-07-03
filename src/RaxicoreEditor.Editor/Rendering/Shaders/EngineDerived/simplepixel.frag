#version 450
// GLSL translation of the engine-derived "simplepixel" fragment technique — the one Cg pixel
// program in the original set. Modulates the interpolated vertex color by a single texture
// sample; pairs with decal.vert / animated_decal.vert / stealth.vert.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vTexCoord0;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 1) uniform sampler2D tex0;

void main() {
    outColor = vColor * texture(tex0, vTexCoord0);
}
