using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

public static class RegionAssetUtil
{
    /// <summary>Logical asset name for a region: explicit AssetName, else the region name.</summary>
    public static string GetAssetName(IRegion region)
    {
        var name = string.IsNullOrWhiteSpace(region.AssetName) ? region.RegionName : region.AssetName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "Region has neither an AssetName nor a RegionName; can't pick an asset filename.");

        // logical names are '/'-separated regardless of host OS (they end up in manifests,
        // build files, and asm directives, all of which want forward slashes).
        return name.Replace('\\', '/').Trim('/');
    }

    /// <summary>Resolve the on-disk path for an asset file, creating parent dirs.</summary>
    public static string PrepareOutputPath(string rootDir, string assetName, string extension)
    {
        var relative = assetName.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.Combine(rootDir, relative + extension);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return full;
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Parse the bpp out of a "gfx.snes.&lt;n&gt;bpp" asset type. Throws on anything it
    /// doesn't recognize rather than guessing -- a wrong bpp silently produces a valid-looking
    /// but garbage image, which is exactly the failure mode that's hardest to notice.
    /// </summary>
    public static int ParseSnesGfxBpp(string assetType)
    {
        const string prefix = "gfx.snes.";
        const string suffix = "bpp";

        if (assetType?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            assetType.EndsWith(suffix, StringComparison.Ordinal))
        {
            var middle = assetType.Substring(prefix.Length, assetType.Length - prefix.Length - suffix.Length);
            if (int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var bpp) &&
                bpp is 2 or 4 or 8)
            {
                return bpp;
            }
        }

        throw new InvalidOperationException(
            $"Asset type '{assetType}' is not a supported SNES graphics type. " +
            "Expected one of: gfx.snes.2bpp, gfx.snes.4bpp, gfx.snes.8bpp.");
    }
}

/// <summary>
/// Plain binary export: write the bytes to a .bin and emit `incbin`.
/// No interpretation of the contents at all, and no manifest -- so, unlike the
/// manifest-writing asset types, there is no codec and nothing in the build that could
/// reproduce these bytes. That makes this exporter their only producer, so it writes the
/// .bin into the generated tree (beside the manifests) and points the incbin straight at it.
/// </summary>
public class BinaryRegionAssetExporter : IRegionAssetExporter
{
    public bool CanExport(IRegion region) => region.ExportType == RegionExportType.Binary;

    public string Export(RegionAssetExportRequest request)
    {
        var name = RegionAssetUtil.GetAssetName(request.Region);
        var binPath = RegionAssetUtil.PrepareOutputPath(request.ManifestRootDir, name, ".bin");
        File.WriteAllBytes(binPath, request.Bytes);
        return $"incbin \"{RegionAssetExportService.JoinAsmPath(request.ManifestRefPrefix, $"{name}.bin")}\"";
    }
}

/// <summary>
/// Asset export: write a manifest describing where the bytes are and how to decode them, so
/// an external tool can turn them into an editable PNG and back.
///
/// The bytes are not copied out here. The build's `extract` step slices them from the ROM
/// using this manifest and decodes them into an editable PNG; compiling that PNG produces
/// the .bin the assembler incbin's.
/// </summary>
public class GfxRegionAssetExporter : BinaryAssetExporterBase
{
    protected override string AssetTypePrefix => "gfx.";
    protected override string CompiledExtension => ".bin";

    // must match gfxpack's default (--layout-width). the manifest records it explicitly
    // anyway, so the two can't silently disagree.
    private const int DefaultLayoutWidthTiles = 16;

    protected override void Validate(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var (bpp, cellHeight, cellSize, _) = ComputeLayout(region);
        var length = request.Bytes.Length;

        if (length == 0 || length % cellSize != 0)
        {
            var what = cellHeight == 8
                ? $"{bpp}bpp tiles ({cellSize} bytes each)"
                : $"{bpp}bpp 8x{cellHeight} cells ({cellSize} bytes each)";
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' is {length} bytes, which is not a whole " +
                $"number of {what}. Adjust the region bounds so it covers complete cells.");
        }
    }

    /// <summary>
    /// Build the gfx-specific manifest pieces. Shape must stay in sync with what gfxpack's
    /// load_manifest() accepts -- gfxpack hard-errors on an unknown version rather than
    /// guessing, so a mismatch here fails loudly at build time instead of producing wrong bytes.
    /// The shared envelope (name, ver, source, generated_by) is added by the base.
    /// </summary>
    protected override AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var (bpp, cellHeight, cellSize, options) = ComputeLayout(region);
        var tiles = request.Bytes.Length / cellSize;

        var gfx = new JsonObject
        {
            ["bpp"] = bpp,
            ["tile_w"] = 8,
            ["tiles"] = tiles,
            ["plane_order"] = "snes-interleaved-pairs",
            ["layout_width_tiles"] = DefaultLayoutWidthTiles,
        };

        // only emit the legacy tile_h for classic 8-row tiles; leaving it at 8 next to a
        // cell_h of 12 would be self-contradictory even though the merge order resolves it.
        if (cellHeight == 8)
            gfx["tile_h"] = 8;

        return new AssetManifestBlock
        {
            TypeString = $"gfx.snes.{bpp}bpp",
            BlockKey = "gfx",
            Block = gfx,
            Options = options,
        };
    }

    /// <summary>
    /// Parse the region's gfx layout once, so Validate() and BuildTypeBlock() can't disagree
    /// about bpp/cell size. bpp/2 bitplane pairs, 2 bytes per row per pair, cellHeight rows;
    /// cellHeight defaults to 8, where a "cell" is exactly a classic 8x8 tile.
    /// </summary>
    private static (int bpp, int cellHeight, int cellSize, JsonObject options) ComputeLayout(IRegion region)
    {
        var bpp = RegionAssetUtil.ParseSnesGfxBpp(region.AssetType);
        var options = ParseAssetOptions(region);
        var cellHeight = GetCellHeight(options);
        var cellSize = bpp * cellHeight;
        return (bpp, cellHeight, cellSize, options);
    }

    /// <summary>
    /// Parse Region.AssetOptions. Returns null when empty (the normal case).
    /// Throws on malformed JSON rather than dropping it: silently ignoring a typo'd option
    /// would export bytes described by a manifest the author didn't actually write.
    /// </summary>
    private static JsonObject ParseAssetOptions(IRegion region)
    {
        var raw = region.AssetOptions;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must be a JSON object " +
                $"(e.g. {{\"cell_h\": 12}}), not a {parsed?.GetValueKind().ToString() ?? "null"}.");
        }

        return obj;
    }

    /// <summary>
    /// The one option Diz must understand: cell_h changes how many bytes each cell occupies,
    /// so the manifest's own "tiles" count is wrong without it.
    /// </summary>
    private static int GetCellHeight(JsonObject options)
    {
        if (options == null || !options.TryGetPropertyValue("cell_h", out var node) || node == null)
            return 8;

        if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<int>(out var cellHeight))
            throw new InvalidOperationException($"Asset Options: cell_h must be an integer, got '{node.ToJsonString()}'.");

        if (cellHeight < 1)
            throw new InvalidOperationException($"Asset Options: cell_h must be >= 1, got {cellHeight}.");

        return cellHeight;
    }
}

/// <summary>
/// Asset export for raw SNES BRR (ADPCM) audio streams -- the second consumer of the generic
/// asset seam, and the case bank-crossing asset regions were designed for (some games store BRR
/// samples that cross bank boundaries). Dispatched by the "audio." AssetType prefix, exactly
/// like gfx is by "gfx." (BinaryAssetExporterBase.CanExport).
///
/// BRR needs NO codec: a .brr file IS the raw ADPCM stream, so the round-trip is a verbatim
/// byte copy handled by the vendored `binpack.py`, not a planar encoder. What Diz writes here
/// is the same shape as gfx: a manifest, and nothing else. `binpack extract` slices the ROM
/// per that manifest into the editable `.brr` payload (the manifest's `audio.ext` tells it
/// which extension); `binpack compile` turns that `.brr` back into
/// `build/assets/audio/&lt;name&gt;.bin`, which is what the assembler `incbin`s. So
/// CompiledExtension is ".bin" here, NOT ".brr" -- ".brr" is the EDITABLE-source extension,
/// carried by the manifest's `audio.ext` block and the audio BuildToolBinding.SourceExtension.
/// This mirrors gfx exactly, where CompiledExtension is ".bin" and the editable extension
/// (".png") lives in the binding.
///
/// The asset region covers ONLY the BRR stream itself. Some games store each sample with a
/// small length/header prefix immediately before the BRR stream; that prefix belongs to the
/// surrounding region's assembly, not the asset, so the region must start after it. With the
/// region covering just the stream, `length % 9 == 0` is the correct validation. Any
/// `prefix == stream-length` integrity check belongs at region-authoring time, not here.
/// </summary>
public class BrrRegionAssetExporter : BinaryAssetExporterBase
{
    protected override string AssetTypePrefix => "audio.";

    // the incbin-target extension (see class doc). NOT the editable extension.
    protected override string CompiledExtension => ".bin";

    // the editable payload's extension: what `binpack` extracts to and compiles from. Recorded
    // in the manifest so the vendored codec resolves it without the ninja rule passing --ext.
    private const string EditableExtension = ".brr";

    protected override void Validate(RegionAssetExportRequest request)
    {
        var length = request.Bytes.Length;

        // BRR (SNES ADPCM) is a stream of 9-byte blocks (1 header + 8 data). Anything not a
        // whole number of blocks is a mis-drawn region -- fail LOUDLY naming it, rather than
        // shipping a truncated sample that the SPC would decode into garbage.
        if (length == 0 || length % 9 != 0)
            throw new InvalidOperationException(
                $"Region '{request.Region.RegionName}' is {length} bytes, which is not a whole " +
                "number of 9-byte BRR blocks (SNES ADPCM). Adjust the region so it covers " +
                "complete BRR blocks. NOTE: the region must cover ONLY the BRR stream -- if the " +
                "sample has a length/header prefix before the stream, that prefix stays in the " +
                "parent region's assembly and must NOT be included here.");
    }

    protected override AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request) =>
        new()
        {
            // the type string is authored verbatim into the manifest and is what the codec
            // reads. Diz never decodes BRR itself.
            TypeString = request.Region.AssetType,
            BlockKey = "audio",

            // the block records only the editable extension; binpack reads `audio.ext` to know
            // what to extract to and what to compile from.
            Block = new JsonObject { ["ext"] = EditableExtension },

            // BRR has no option vocabulary Diz interprets; leave options off entirely (the base
            // omits the key when null).
            Options = null,
        };
}

/// <summary>
/// Asset export for CT's fixed-width name tables (text.ct.mapped and kin) -- item names, tech
/// names, menu strings: one byte per glyph, no terminator, each record padded to a fixed width.
/// Dispatched by the "text." AssetType prefix (BinaryAssetExporterBase.CanExport), exactly like
/// gfx is by "gfx." and BRR by "audio.".
///
/// Like gfx and BRR, Diz writes only a manifest and never decodes the text itself. The
/// vendored `textpack.py` slices the ROM per that manifest into an editable `.yaml`
/// (`textpack extract`) and turns that `.yaml` back into `build/assets/text/&lt;name&gt;.bin`
/// (`textpack compile`), which the assembler `incbin`s. So CompiledExtension is ".bin", NOT
/// ".yaml" -- the editable extension lives in the text BuildToolBinding, mirroring gfx (.png)
/// and BRR (.brr).
///
/// The type-specific manifest fields Diz cannot derive from the bytes -- the character table
/// (`tbl`), the record width, the pad byte, and the named-token map (equipment icons / control
/// codes) -- are authored per-region in Region.AssetOptions, the same free-form escape hatch gfx
/// uses for `cell_h`. Diz reads them, computes `count` from the region length, and writes the
/// `text` block in the key order textpack's load_manifest expects. textpack is the authority on
/// their meaning and validates them again at build time (an unknown manifest version is a hard
/// error there, never a silent guess), so a mismatch fails loudly rather than emitting wrong bytes.
/// </summary>
public class TextRegionAssetExporter : BinaryAssetExporterBase
{
    protected override string AssetTypePrefix => "text.";

    // the incbin-target extension (see class doc). NOT the editable ".yaml".
    protected override string CompiledExtension => ".bin";

    protected override void Validate(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var recordWidth = GetRecordWidth(ParseAssetOptions(region), region);
        var length = request.Bytes.Length;

        // Fixed-width records with no terminator: the region must be a whole number of them, or
        // every record past the ragged point is mis-framed. Fail LOUDLY naming the region rather
        // than shipping a name table shifted by a few bytes.
        if (length == 0 || length % recordWidth != 0)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' is {length} bytes, which is not a whole number of " +
                $"{recordWidth}-byte records. Adjust the region bounds (or the record_width in " +
                "Asset Options) so it covers complete records.");
    }

    protected override AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var options = ParseAssetOptions(region);
        var recordWidth = GetRecordWidth(options, region);
        var count = request.Bytes.Length / recordWidth;

        // Key order matches what textpack's `load_manifest` reads, so the tracked manifest
        // stays byte-stable: tbl, count, record_width, pad, [tokens].
        var text = new JsonObject
        {
            ["tbl"] = GetRequiredString(options, "tbl", region),
            ["count"] = count,
            ["record_width"] = recordWidth,
            ["pad"] = GetPad(options, region),
        };
        var tokens = GetTokens(options, region);
        if (tokens != null)
            text["tokens"] = tokens;

        return new AssetManifestBlock
        {
            // authored verbatim into the manifest and read by textpack; Diz never decodes it.
            TypeString = region.AssetType,
            BlockKey = "text",
            Block = text,
            // every option is consumed into the text block above; nothing passes through to a
            // separate "options" key.
            Options = null,
        };
    }

    /// <summary>
    /// Parse Region.AssetOptions into the required options object. Unlike gfx -- where options are
    /// optional and only carry cell_h -- a text asset CANNOT be described without them: the table,
    /// width, and pad byte have no defaults Diz could invent. So an empty AssetOptions is a hard
    /// error that names what's missing, not a silent fallback.
    /// </summary>
    private static JsonObject ParseAssetOptions(IRegion region)
    {
        var raw = region.AssetOptions;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' is a text asset but has no Asset Options. Text " +
                "assets require at least " +
                "{\"tbl\": \"text/<table>.tbl\", \"record_width\": N, \"pad\": \"0xNN\"} " +
                "(plus an optional \"tokens\" map).");

        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is not JsonObject obj)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must be a JSON object, not a " +
                $"{parsed?.GetValueKind().ToString() ?? "null"}.");

        return obj;
    }

    private static int GetRecordWidth(JsonObject options, IRegion region)
    {
        if (!options.TryGetPropertyValue("record_width", out var node) || node == null ||
            node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<int>(out var width))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must set an integer \"record_width\".");
        if (width < 1)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': record_width must be >= 1, got {width}.");
        return width;
    }

    private static string GetRequiredString(JsonObject options, string key, IRegion region)
    {
        if (!options.TryGetPropertyValue(key, out var node) || node == null ||
            node.GetValueKind() != JsonValueKind.String)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must set a string \"{key}\".");
        var value = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options \"{key}\" must not be empty.");
        return value;
    }

    /// <summary>
    /// The pad byte, verbatim as authored (e.g. "0xEF"). Validated as a byte literal here so a
    /// typo fails at export, not at build time -- textpack checks it again as the authority.
    /// </summary>
    private static string GetPad(JsonObject options, IRegion region)
    {
        var pad = GetRequiredString(options, "pad", region);
        if (!TryParseByteLiteral(pad, out _))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options \"pad\" must be a byte literal like " +
                $"\"0xEF\" (0..255), got \"{pad}\".");
        return pad;
    }

    private static bool TryParseByteLiteral(string s, out int value)
    {
        s = s.Trim();
        var ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        return ok && value is >= 0 and <= 0xFF;
    }

    /// <summary>
    /// The optional named-token map (equipment icons / control codes), deep-cloned so it detaches
    /// from the parsed options tree before it's grafted into the manifest (a JsonNode can't have
    /// two parents). Null when absent -- the block simply omits "tokens".
    /// </summary>
    private static JsonObject GetTokens(JsonObject options, IRegion region)
    {
        if (!options.TryGetPropertyValue("tokens", out var node) || node == null)
            return null;
        if (node is not JsonObject tokens)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options \"tokens\" must be an object of " +
                $"NAME -> \"0xNN\", not a {node.GetValueKind()}.");
        return tokens.DeepClone().AsObject();
    }
}
