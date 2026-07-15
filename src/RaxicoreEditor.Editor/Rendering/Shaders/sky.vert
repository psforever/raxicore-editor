#version 450
// Fullscreen-triangle sky background. Emits clip-space NDC to the fragment shader, which reconstructs a
// world-space view ray from it. No vertex buffer — the three corners are generated from gl_VertexIndex.
layout(location = 0) out vec2 vNdc;

void main() {
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2); // (0,0) (2,0) (0,2)
    vNdc = uv * 2.0 - 1.0;                                         // (-1,-1) (3,-1) (-1,3)
    gl_Position = vec4(vNdc, 0.0, 1.0);                            // z=0 = far (reversed-Z); depth test is off
}
