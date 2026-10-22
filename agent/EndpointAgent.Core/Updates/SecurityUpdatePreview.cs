namespace EndpointAgent.Core.Updates;

/// <summary>
/// The single rule for what counts as a preview/beta security update, which the
/// platform excludes from both scanning and installation.
/// </summary>
/// <remarks>
/// Pure and platform-neutral so it reads the same whether it is applied to a live
/// WUA update object (the Windows service), to the scan results before they are
/// reported (the scan executor's belt-and-braces filter), or in a unit test. An
/// update is a preview when the Windows Update Agent flags it beta, or when its
/// title advertises itself as a preview or beta build -- which is what catches the
/// optional "Preview" cumulative updates Microsoft offers between Patch Tuesdays.
/// The rule is the same one <c>EndpointAgent.Core.Drivers.DriverUpdatePreview</c>
/// applies to driver updates, kept as a sibling so each feature's classifier can
/// evolve on its own.
/// </remarks>
public static class SecurityUpdatePreview
{
    private static readonly string[] Markers = ["preview", "beta"];

    /// <summary>True when the title advertises a preview or beta build (case-insensitive).</summary>
    public static bool HasPreviewMarker(string? title) =>
        !string.IsNullOrEmpty(title)
        && Array.Exists(Markers, m => title.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the update is beta (per WUA) or its title marks it a preview/beta.</summary>
    public static bool IsPreview(string? title, bool isBeta) => isBeta || HasPreviewMarker(title);
}
