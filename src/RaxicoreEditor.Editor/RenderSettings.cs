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
    }
}
