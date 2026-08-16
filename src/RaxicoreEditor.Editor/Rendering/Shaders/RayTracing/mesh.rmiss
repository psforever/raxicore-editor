#version 460
#extension GL_EXT_ray_tracing : require

layout(location = 0) rayPayloadInEXT vec3 hitColor;

void main() {
    // Matches the rasterizer's viewport clear colour (MeshViewportRenderer.Render) so RT and raster
    // backgrounds agree.
    hitColor = vec3(0.04, 0.04, 0.05);
}
