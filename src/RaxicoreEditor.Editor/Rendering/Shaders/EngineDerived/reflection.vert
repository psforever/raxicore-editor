#version 450
// GLSL translation of the engine-derived "reflection" vertex technique: classic sphere-map
// texture-coordinate generation from an eye-space reflection vector — used for chrome/glass/
// canopy-style materials. No vertex color output; would pair with a plain texture-sample
// fragment shader (not part of this original set).

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

layout(location = 1) out vec2 vTexCoord0;

layout(set = 0, binding = 0) uniform ReflectionParams {
    mat4 modelView;
    mat4 projection;
    // lightDir/lightColor/ambientColor are part of the original parameter list but unused by
    // this technique's body — kept here only for documentation of the original signature.
    vec3 lightDir;
    vec3 lightColor;
    vec3 ambientColor;
} params;

void main() {
    vec4 v = params.modelView * vec4(inPosition, 1.0);

    // Unit vector from the origin to the eye-space vertex position.
    vec3 u = normalize(v.xyz);

    // Eye-space normal (upper-left 3x3 of the model-view matrix).
    mat3 modelViewRot = mat3(params.modelView);
    vec3 n = modelViewRot * inNormal;

    // Reflection vector.
    vec3 r = u - 2.0 * n * dot(n, u);

    // Sphere-map UV.
    r.z = r.z + 1.0;
    float m = 0.5 * inversesqrt(dot(r, r));
    vTexCoord0 = vec2(r.x * m + 0.5, r.y * m + 0.5);

    gl_Position = params.projection * v;
}
