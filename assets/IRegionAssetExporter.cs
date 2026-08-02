using System.Collections.Generic;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Describes a region as a standalone asset -- by writing a manifest saying where its bytes
/// live in the ROM and how to decode them -- and tells the assembly writer what directive to
/// emit in place of the usual inline `db` bytes.
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
    /// Write the manifest describing this asset, and report both what the assembly writer must
    /// emit in place of the region's inline bytes and what the build has to rebuild.
    /// </summary>
    RegionAssetExportResult Export(RegionAssetExportRequest request);
}

/// <summary>
/// What one region's export produced. The assembly directive and the build graph come out of
/// the same call because they describe the same asset: the exporter already parsed the region's
/// options to write the manifest, so re-deriving the build graph from the region afterwards
/// would be a second, independently-drifting reading of the same authoring.
/// </summary>
public sealed class RegionAssetExportResult
{
    /// <summary>
    /// The assembly line emitted in place of the region's bytes. Exactly one line, whatever
    /// the node structure underneath: a region occupies one contiguous span, so it contributes
    /// one directive.
    /// </summary>
    public string AsmDirective { get; init; }

    /// <summary>What the build must extract and recompile for this region. Never empty.</summary>
    public IReadOnlyList<AssetBuildNode> BuildNodes { get; init; } = [];
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

    /// <summary>
    /// The region's raw bytes, already read out of the ROM. Null for a container member: its
    /// bytes only exist after the container's transform pipeline has run, which is a build-time
    /// operation. Use <see cref="ByteLength"/> for anything that only needs the size.
    /// </summary>
    public byte[] Bytes { get; init; }

    /// <summary>
    /// Where this asset's bytes sit inside a larger buffer, when it is one member of a container
    /// rather than a range of the ROM. Null -- the normal case -- means the bytes came straight
    /// from the ROM and <see cref="Bytes"/> holds them.
    /// </summary>
    public AssetMemberContext Member { get; init; }

    /// <summary>
    /// How many bytes this asset occupies. Every size-derived manifest field (tile counts, record
    /// counts, block-alignment checks) reads this rather than the byte array, so an asset whose
    /// bytes are not available until build time is described exactly like one whose are.
    /// </summary>
    public int ByteLength => Bytes?.Length ?? Member?.Length ?? 0;

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
}

/// <summary>
/// An asset's place inside a container's buffer, and the proof of what its bytes are.
///
/// A member has no ROM offset of its own: the bytes it describes only exist once the container
/// has been sliced out of the ROM and its transform pipeline has run. So its provenance is
/// recorded differently -- the container it belongs to, the offset within that container's
/// buffer, the length, and the hash of the decompressed bytes.
///
/// The hash is AUTHORED alongside the offset and length rather than computed here, because the
/// bytes it covers are exactly the ones no part of this program can see. That makes it an
/// independent claim about what the pipeline must produce: the build hashes the real bytes and
/// halts if they disagree, which is a check a self-computed hash could never fail.
/// </summary>
public sealed class AssetMemberContext
{
    /// <summary>Logical name of the container this is a member of, e.g. "blob/font_pack".</summary>
    public string ContainerName { get; init; }

    /// <summary>Offset of this member's first byte within the container's buffer.</summary>
    public int At { get; init; }

    /// <summary>Length of this member in bytes.</summary>
    public int Length { get; init; }

    /// <summary>Lowercase hex sha256 of this member's bytes as they appear in the buffer.</summary>
    public string Sha256 { get; init; }
}
