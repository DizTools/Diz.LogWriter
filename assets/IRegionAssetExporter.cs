using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Writes a region's raw bytes out as a standalone asset (a .bin, plus optionally a
/// manifest describing how to decode it), and tells the assembly writer what directive
/// to emit in place of the usual inline `db` bytes.
///
/// Deliberately NOT responsible for pixel/palette encoding. Diz writes raw bytes and a
/// manifest; an external, vendored tool (gfxpack) owns every codec. Keeping exactly one
/// implementation of each codec is what makes byte-identical round-trips checkable --
/// a second copy inside Diz would be a second thing to drift.
/// </summary>
public interface IRegionAssetExporter
{
    /// <summary>
    /// Whether this exporter claims <paramref name="region"/>. Replaces the old single-enum
    /// match: plain-binary keys on ExportType, but manifest-writing asset exporters (gfx, brr)
    /// all share ExportType == Asset and disambiguate on the AssetType PREFIX instead. Exactly
    /// one registered exporter must return true for any given asset region.
    /// </summary>
    bool CanExport(IRegion region);

    /// <summary>
    /// Write the asset files for <paramref name="region"/> into <paramref name="outputDir"/>.
    /// Returns the assembly directive line to emit in place of the region's inline bytes.
    /// </summary>
    string Export(RegionAssetExportRequest request);
}

/// <summary>
/// Everything an exporter needs, with the bytes already resolved. Passing the bytes in
/// (rather than a data-source handle) keeps exporters trivially unit-testable and keeps
/// the SNES/PC address conversion in exactly one place.
/// </summary>
public class RegionAssetExportRequest
{
    /// <summary>The region being exported.</summary>
    public IRegion Region { get; init; }

    /// <summary>The region's raw bytes, already read out of the ROM.</summary>
    public byte[] Bytes { get; init; }

    /// <summary>PC/file offset the bytes came from (recorded in the manifest for traceability).</summary>
    public int PcOffset { get; init; }

    /// <summary>Root directory assets are written under, e.g. "&lt;export&gt;/assets/src".</summary>
    public string AssetRootDir { get; init; }

    /// <summary>
    /// Path prefix used inside the emitted assembly directive, relative to where the .asm
    /// lives, e.g. "assets/src". Kept separate from <see cref="AssetRootDir"/> because the
    /// on-disk path and the path the assembler sees are not always the same string.
    /// </summary>
    public string AssetRefPrefix { get; init; }
}
