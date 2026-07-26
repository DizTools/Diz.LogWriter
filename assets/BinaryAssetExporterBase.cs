using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Base for asset exporters that write a manifest describing how to decode a region's bytes.
/// Owns everything shared across asset types: building the `source` envelope (rom_offset /
/// length / source_sha256 / snes_addr), assembling the manifest in a fixed key order, writing
/// it deterministically, and returning the `incbin` directive.
///
/// The ROM bytes themselves are NOT copied out. The manifest's `source` block says where they
/// live and what they hash to, and the build's `extract` step slices them from the ROM on
/// demand -- so no copy of game data is ever written into the repo, and there is exactly one
/// authority for what those bytes are (the ROM).
///
/// Subclasses supply only the type-specific pieces: the compiled-payload extension, a
/// validation pass, and the manifest's `type` string + typed block (e.g. gfx:{} / audio:{}).
///
/// Dispatch is by AssetType PREFIX (e.g. "gfx.", "audio."), not by the RegionExportType enum:
/// asset regions share one ExportType, so the enum alone cannot tell gfx from audio. Routing on
/// the prefix mirrors what the codec tools already do internally with their `type` field, and it
/// does not touch the save format the way a new enum value would. A subclass may widen the claim
/// (see CanExport) for a type an export-type shorthand can also select.
/// </summary>
public abstract class BinaryAssetExporterBase : IRegionAssetExporter
{
    /// <summary>
    /// AssetType prefix this exporter claims, e.g. "gfx." or "audio.". A region is handled by
    /// this exporter when its <see cref="IRegion.ExportType"/> is Asset and its
    /// <see cref="IRegion.AssetType"/> starts with this prefix.
    /// </summary>
    protected abstract string AssetTypePrefix { get; }

    /// <summary>
    /// Extension of the COMPILED payload the assembler incbin's, e.g. ".bin". Not the
    /// editable source extension (.png/.yaml/.brr) -- that lives in the manifest and in the
    /// build's codec binding.
    /// </summary>
    protected abstract string CompiledExtension { get; }

    /// <summary>
    /// Reject bytes this asset type cannot represent (e.g. a partial gfx cell, or a BRR blob
    /// whose length is not a multiple of 9). Throw <see cref="InvalidOperationException"/> with
    /// a user-facing message; returning normally means "valid". Runs before anything is written.
    /// </summary>
    protected abstract void Validate(RegionAssetExportRequest request);

    /// <summary>
    /// The type-specific manifest pieces: the "type" string and the typed block
    /// (e.g. key "gfx" -&gt; { bpp, tiles, ... }), plus any free-form "options" object.
    /// The shared parts (name, ver, source, generated_by) are added by the base.
    /// </summary>
    protected abstract AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request);

    /// <summary>
    /// Claim regions whose AssetType carries this exporter's prefix. Virtual because one asset
    /// kind -- a verbatim byte range -- is also reachable from an ExportType that carries no
    /// AssetType to dispatch on, and must widen the claim to cover it.
    /// </summary>
    public virtual bool CanExport(IRegion region) =>
        region.ExportType == RegionExportType.Asset &&
        region.AssetType?.StartsWith(AssetTypePrefix, StringComparison.Ordinal) == true;

    public string Export(RegionAssetExportRequest request)
    {
        // validate before writing anything: a half-written asset tree is worse than none.
        Validate(request);

        var name = RegionAssetUtil.GetAssetName(request.Region);

        var manifestPath = RegionAssetUtil.PrepareOutputPath(request.ManifestRootDir, name, ".json");
        WriteManifest(manifestPath, BuildManifest(request, name));

        return $"incbin \"{request.AssetRefPrefix}/{name}{CompiledExtension}\"";
    }

    /// <summary>
    /// Write a manifest byte-deterministically. Manifests may be tracked in git, so the same
    /// project must produce the same bytes on every machine and every run:
    ///   - key order is fixed by construction (see BuildManifest),
    ///   - line endings are forced to LF, never the host's,
    ///   - numbers are formatted by System.Text.Json, which is always invariant-culture,
    ///   - UTF-8 with no BOM.
    /// Any nondeterminism here turns every re-export into diff churn.
    /// </summary>
    private static void WriteManifest(string path, JsonObject manifest)
    {
        var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Assemble the manifest. The key order here is load-bearing: it is what the codec tools'
    /// `load_manifest` reads and what the byte-identity gate pins, so keep name / type / ver /
    /// source / &lt;typed block&gt; / options / generated_by in exactly this sequence.
    /// </summary>
    private JsonObject BuildManifest(RegionAssetExportRequest request, string name)
    {
        var region = request.Region;
        var parts = BuildTypeBlock(request);

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["type"] = parts.TypeString,
        };

        // `ver` omitted means "latest", the default. Only pin it when the project asked for
        // a specific version.
        if (!string.IsNullOrWhiteSpace(region.AssetVersion))
            manifest["ver"] = region.AssetVersion.Trim();

        manifest["source"] = BuildSourceEnvelope(request);
        manifest[parts.BlockKey] = parts.Block;

        // free-form passthrough: the codec owns this vocabulary, Diz only checks it's an
        // object (done at parse time). Anything here wins over the typed block by merge order.
        if (parts.Options != null)
            manifest["options"] = parts.Options.DeepClone();

        manifest["generated_by"] = "DiztinGUIsh";

        return manifest;
    }

    /// <summary>
    /// The `source` envelope: where the bytes came from and their hash. Identical across every
    /// asset type, and load-bearing -- it is what the build's `extract` step slices the ROM
    /// with, and the hash is what catches someone building against the wrong ROM. Key
    /// names/format must match what the codec tools read ("snes_addr", not "snes_address").
    /// </summary>
    private static JsonObject BuildSourceEnvelope(RegionAssetExportRequest request) =>
        new()
        {
            ["rom_offset"] = $"0x{request.PcOffset:X}",
            ["length"] = request.Bytes.Length,
            ["source_sha256"] = RegionAssetUtil.Sha256Hex(request.Bytes),
            ["snes_addr"] = $"0x{request.Region.StartSnesAddress:X6}",
        };
}

/// <summary>
/// The type-specific pieces of an asset manifest, produced by a <see cref="BinaryAssetExporterBase"/>
/// subclass and slotted into the shared manifest by the base.
/// </summary>
public sealed class AssetManifestBlock
{
    /// <summary>The manifest's "type" string, e.g. "gfx.snes.2bpp".</summary>
    public string TypeString { get; init; }

    /// <summary>Key for the typed block, e.g. "gfx" or "audio".</summary>
    public string BlockKey { get; init; }

    /// <summary>The typed block object, e.g. { bpp, tile_w, tiles, ... }.</summary>
    public JsonObject Block { get; init; }

    /// <summary>
    /// Free-form options object merged into the manifest under "options". Null (the normal
    /// case) omits the key entirely.
    /// </summary>
    public JsonObject Options { get; init; }
}
