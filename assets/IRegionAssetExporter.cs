using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Describes a region as a standalone asset -- normally by writing a manifest saying where
/// its bytes live in the ROM and how to decode them -- and tells the assembly writer what
/// directive to emit in place of the usual inline `db` bytes.
///
/// Deliberately NOT responsible for pixel/palette encoding, and it does not copy the ROM
/// bytes out either: the build slices them straight from the ROM using the manifest. An
/// external, vendored tool (gfxpack and friends) owns every codec. Keeping exactly one
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
    /// Write whatever files describe this asset (normally just its manifest) and return the
    /// assembly directive line to emit in place of the region's inline bytes.
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

    /// <summary>
    /// Root directory manifests are written under, e.g. "&lt;project&gt;/generated/assets".
    /// Generated output: rewritten on every export, never hand-edited.
    /// </summary>
    public string ManifestRootDir { get; init; }

    /// <summary>
    /// Path prefix used inside the emitted assembly directive for COMPILED asset payloads,
    /// relative to where the .asm lives, e.g. "../build/assets". Kept separate from the
    /// on-disk roots because the path the assembler sees is not the path Diz writes to --
    /// nothing Diz writes is what gets incbin'd.
    /// </summary>
    public string AssetRefPrefix { get; init; }

    /// <summary>
    /// Path prefix, relative to where the .asm lives, of <see cref="ManifestRootDir"/>. Only
    /// needed by asset kinds that have no codec step and are therefore incbin'd straight out
    /// of the generated tree.
    /// </summary>
    public string ManifestRefPrefix { get; init; }
}
