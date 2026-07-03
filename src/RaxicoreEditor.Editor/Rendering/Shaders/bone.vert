#version 450

// Skeleton-overlay line renderer: positions are already fully composed in view-space by
// SkeletalAnimator (bind pose or animated, per bone), so the only transform left is the
// camera's model-view-projection.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;

layout(location = 0) out vec3 vColor;

layout(push_constant) uniform Push {
    mat4 mvp;
} pc;

void main() {
    gl_Position = pc.mvp * vec4(inPosition, 1.0);
    vColor = inColor;
}
