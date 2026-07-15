using System;

namespace RaxicoreEditor.Editor
{
    /// <summary>Which level-of-detail tier of a model to build/render.</summary>
    public enum ModelDetail
    {
        /// <summary>The near/full-detail model (the engine's lod-14 tier, or the finest tier present).</summary>
        Detailed,

        /// <summary>The coarsest LOD tier (distant impostor / billboard).</summary>
        Low,
    }

    /// <summary>
    /// Process-wide render preferences read while a mesh document builds its geometry. Kept as a simple
    /// static so both the menu (which sets it) and <c>MeshDocument</c> (which reads it during assembly) can
    /// reach it without threading a setting through every call. Changing it takes effect the next time a
    /// document is (re)built. (Named to avoid Avalonia's own <c>RenderOptions</c>.)
    /// </summary>
    public static class RenderSettings
    {
        /// <summary>Selected model detail tier. Default: the detailed model (LODs hidden).</summary>
        public static ModelDetail Detail { get; set; } = ModelDetail.Detailed;

        /// <summary>
        /// When true, the viewport draws with the engine-derived material shaders (GLSL translations of the
        /// original per-material Cg programs): baked vertex colour × texture for pre-lit geometry, and a
        /// single-directional per-vertex light for geometry that ships normals — instead of the editor's
        /// generic hemispheric-lit shader. Off by default. Read by <c>MeshViewportRenderer</c> at draw time
        /// (a pure pipeline switch — no geometry rebuild needed; the vertex colour is always uploaded).
        /// </summary>
        public static bool EngineShading { get; set; } = false;

        /// <summary>When true, continents/caverns draw their procedural sky (horizon→zenith gradient + sun,
        /// from the zone's engine lighting cycle) behind the scene instead of the flat clear colour. Read by
        /// the viewport each frame; toggling raises <see cref="Changed"/> for a lightweight re-render.</summary>
        public static bool Sky { get; set; } = false;

        /// <summary>Maximum viewport framerate in frames per second while the view is animating/being moved.
        /// <c>0</c> = uncapped (render as fast as the pipeline allows). The viewport paces frames to this
        /// rate; capping at (or below) the monitor's refresh avoids wasting GPU on frames the display never
        /// shows. Read live by the viewport each frame.</summary>
        public static int FrameCap { get; set; } = 0;

        /// <summary>Raised when a per-frame render setting changes (e.g. the sky toggle) so open viewports can
        /// redraw without rebuilding geometry.</summary>
        public static event Action? Changed;

        /// <summary>Notify viewports that a per-frame render setting changed.</summary>
        public static void RaiseChanged() => Changed?.Invoke();
    }
}
