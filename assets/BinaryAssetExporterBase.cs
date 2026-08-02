using System;
using System.Collections.Generic;
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
/// A subclass describing a structured blob instead supplies a member list and, optionally, a
/// transform pipeline, and reports the build nodes for the members it declared. Everything is
/// widened through those extension points rather than by overriding Export, so the manifest
/// writer and its key order stay in exactly one place.
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
    ///
    /// Return null only for a pure container, which describes its bytes by the members tiling
    /// them rather than by a codec's geometry. A node carries a typed block or members, never
    /// both -- see BuildMembers.
    /// </summary>
    protected virtual AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request) => null;

    /// <summary>
    /// Transform stages between the ROM bytes and the buffer the manifest describes, outermost
    /// first, as the manifest's "pipeline" array (e.g. a decompression stage carrying the mode
    /// its encoder cannot derive). Null -- the normal case -- omits the key entirely and means
    /// the ROM bytes ARE the buffer.
    /// </summary>
    protected virtual JsonArray BuildPipeline(RegionAssetExportRequest request) => null;

    /// <summary>
    /// Child node references tiling this asset's buffer, in declaration order, as the manifest's
    /// "members" array. Null -- the normal case -- makes this a leaf, described by its typed
    /// block instead. Exactly one of the two is present: a node with both would describe its
    /// bytes twice, and the two descriptions could disagree.
    /// </summary>
    protected virtual JsonArray BuildMembers(RegionAssetExportRequest request) => null;

    /// <summary>
    /// Layer-relative paths of files this asset's codec reads but the manifest only NAMES
    /// (e.g. a text asset's character table). Reported so the build can make them implicit deps
    /// of the extract edge; the exporter is the right place to name them because it is what
    /// validated the authoring they come from.
    /// </summary>
    protected virtual IReadOnlyList<string> BuildSharedFiles(RegionAssetExportRequest request) => [];

    /// <summary>
    /// What the build has to rebuild for this region. The default is one leaf node -- one
    /// region, one codec, one output. A container overrides this to report its member nodes too,
    /// mirroring the "members" it wrote into the manifest.
    /// </summary>
    protected virtual IReadOnlyList<AssetBuildNode> BuildNodes(RegionAssetExportRequest request) =>
    [
        new AssetBuildNode
        {
            Name = RegionAssetUtil.GetAssetName(request.Region),
            AssetType = RegionAssetUtil.GetAssetType(request.Region),
            SharedFiles = BuildSharedFiles(request),
        },
    ];

    /// <summary>
    /// Claim regions whose AssetType carries this exporter's prefix. Virtual because one asset
    /// kind -- a verbatim byte range -- is also reachable from an ExportType that carries no
    /// AssetType to dispatch on, and must widen the claim to cover it.
    /// </summary>
    public virtual bool CanExport(IRegion region) =>
        region.ExportType == RegionExportType.Asset &&
        region.AssetType?.StartsWith(AssetTypePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// NOT virtual, and must stay that way: the manifest writer below is the determinism
    /// contract and the key order is pinned by the byte-identity gate, so a subclass must not be
    /// able to reimplement either. Widen behaviour through the Build* extension points instead.
    /// </summary>
    public RegionAssetExportResult Export(RegionAssetExportRequest request)
    {
        // validate before writing anything: a half-written asset tree is worse than none.
        Validate(request);

        var name = RegionAssetUtil.GetAssetName(request.Region);

        var manifestPath = RegionAssetUtil.PrepareOutputPath(request.ManifestRootDir, name, ".json");
        WriteManifest(manifestPath, BuildManifest(request, name));

        return new RegionAssetExportResult
        {
            AsmDirective = $"incbin \"{request.AssetRefPrefix}/{name}{CompiledExtension}\"",
            BuildNodes = BuildNodes(request),
        };
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
    /// source / pipeline / &lt;typed block | members&gt; / options / generated_by in exactly this
    /// sequence. Optional keys are omitted rather than emitted null, so a manifest never grows
    /// a key that says nothing.
    /// </summary>
    private JsonObject BuildManifest(RegionAssetExportRequest request, string name)
    {
        var region = request.Region;
        var parts = BuildTypeBlock(request);
        var members = BuildMembers(request);

        // The grammar: a leaf is described by its codec's typed block, a container by the
        // members tiling its buffer. Both would describe the same bytes twice and be free to
        // disagree; neither describes them at all.
        if ((parts == null) == (members == null))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': an asset manifest must carry either a typed block " +
                $"or a members list, not {(parts == null ? "neither" : "both")}.");

        var typeString = parts?.TypeString ?? RegionAssetUtil.GetAssetType(region);
        if (string.IsNullOrWhiteSpace(typeString))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' has no asset type. The type is the codec contract " +
                "the build dispatches on, so it cannot be inferred or left blank.");

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["type"] = typeString,
        };

        // `ver` omitted means "latest", the default. Only pin it when the project asked for
        // a specific version.
        if (!string.IsNullOrWhiteSpace(region.AssetVersion))
            manifest["ver"] = region.AssetVersion.Trim();

        manifest["source"] = BuildSourceEnvelope(request);

        if (BuildPipeline(request) is { } pipeline)
            manifest["pipeline"] = pipeline;

        if (parts != null)
            manifest[parts.BlockKey] = parts.Block;
        else
            manifest["members"] = members;

        // free-form passthrough: the codec owns this vocabulary, Diz only checks it's an
        // object (done at parse time). Anything here wins over the typed block by merge order.
        if (parts?.Options != null)
            manifest["options"] = parts.Options.DeepClone();

        manifest["generated_by"] = "DiztinGUIsh";

        return manifest;
    }

    /// <summary>
    /// The `source` envelope: where the bytes came from and their hash. Load-bearing -- it is what
    /// the build's `extract` step slices the ROM with, and the hash is what catches someone
    /// building against the wrong ROM. Key names/format must match what the codec tools read
    /// ("snes_addr", not "snes_address").
    ///
    /// Two shapes, because there are two ways an asset's bytes can be located. A range of the ROM
    /// records where in the ROM it is. An asset that is one member of a container has no ROM
    /// offset -- its bytes only exist after the container is unpacked -- so it records which
    /// container and where inside it instead, and the hash covers the unpacked bytes. Omitting
    /// rom_offset is what tells the codecs not to try to compare it against the cartridge.
    ///
    /// The hash is never optional in either shape. It is the only thing standing between "the
    /// build decoded some bytes" and "the build decoded THESE bytes", and the codecs refuse to
    /// decode a manifest that claims provenance without it.
    /// </summary>
    private static JsonObject BuildSourceEnvelope(RegionAssetExportRequest request) =>
        request.Member is { } member
            ? new JsonObject
            {
                ["length"] = member.Length,
                ["source_sha256"] = member.Sha256,
                ["member_of"] = member.ContainerName,
                ["at"] = member.At,
            }
            : new JsonObject
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
