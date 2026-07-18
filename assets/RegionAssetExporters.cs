using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// No interpretation of the contents at all.
/// </summary>
public class BinaryRegionAssetExporter : IRegionAssetExporter
{
    public RegionExportType Handles => RegionExportType.Binary;

    public string Export(RegionAssetExportRequest request)
    {
        var name = RegionAssetUtil.GetAssetName(request.Region);
        var binPath = RegionAssetUtil.PrepareOutputPath(request.AssetRootDir, name, ".bin");
        File.WriteAllBytes(binPath, request.Bytes);
        return $"incbin \"{request.AssetRefPrefix}/{name}.bin\"";
    }
}

/// <summary>
/// Asset export: write the raw .bin AND a manifest describing how to decode it, so an
/// external tool can turn it into an editable PNG and back.
///
/// The .bin is what the assembler consumes, so the build stays correct even before anyone
/// runs the codec tool; the PNG is generated from the .bin as a separate, later step.
/// </summary>
public class GfxRegionAssetExporter : IRegionAssetExporter
{
    public RegionExportType Handles => RegionExportType.Asset;

    // must match gfxpack's default (--layout-width). the manifest records it explicitly
    // anyway, so the two can't silently disagree.
    private const int DefaultLayoutWidthTiles = 16;

    public string Export(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var name = RegionAssetUtil.GetAssetName(region);
        var bytes = request.Bytes;

        var bpp = RegionAssetUtil.ParseSnesGfxBpp(region.AssetType);
        var tileSize = bpp * 8; // 8 rows, bpp/2 bitplane pairs, 2 bytes per row per pair

        if (bytes.Length == 0 || bytes.Length % tileSize != 0)
        {
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' is {bytes.Length} bytes, which is not a whole " +
                $"number of {bpp}bpp tiles ({tileSize} bytes each). Adjust the region bounds " +
                "so it covers complete tiles.");
        }

        var binPath = RegionAssetUtil.PrepareOutputPath(request.AssetRootDir, name, ".bin");
        File.WriteAllBytes(binPath, bytes);

        var manifest = BuildManifest(request, name, bpp, tileSize);
        var manifestPath = RegionAssetUtil.PrepareOutputPath(request.AssetRootDir, name, ".json");
        var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json + Environment.NewLine, new UTF8Encoding(false));

        return $"incbin \"{request.AssetRefPrefix}/{name}.bin\"";
    }

    /// <summary>
    /// Build the manifest. Shape must stay in sync with what gfxpack's load_manifest()
    /// accepts -- gfxpack hard-errors on an unknown version rather than guessing, so a
    /// mismatch here fails loudly at build time instead of producing wrong bytes.
    /// </summary>
    private static JsonObject BuildManifest(RegionAssetExportRequest request, string name, int bpp, int tileSize)
    {
        var region = request.Region;
        var bytes = request.Bytes;
        var tiles = bytes.Length / tileSize;

        var source = new JsonObject
        {
            ["rom_offset"] = $"0x{request.PcOffset:X}",
            ["length"] = bytes.Length,
            ["source_sha256"] = RegionAssetUtil.Sha256Hex(bytes),
            // key name must match what gfxpack's `extract` writes ("snes_addr", not
            // "snes_address") so both authors produce the same schema.
            ["snes_addr"] = $"0x{region.StartSnesAddress:X6}",
        };

        var gfx = new JsonObject
        {
            ["bpp"] = bpp,
            ["tile_w"] = 8,
            ["tile_h"] = 8,
            ["tiles"] = tiles,
            ["plane_order"] = "snes-interleaved-pairs",
            ["layout_width_tiles"] = DefaultLayoutWidthTiles,
        };

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["type"] = $"gfx.snes.{bpp}bpp",
        };

        // `ver` omitted means "latest", which is the default and what we want most of the
        // time. only pin it when the project has explicitly asked for a specific version.
        if (!string.IsNullOrWhiteSpace(region.AssetVersion))
            manifest["ver"] = region.AssetVersion.Trim();

        manifest["source"] = source;
        manifest["gfx"] = gfx;
        manifest["generated_by"] = "DiztinGUIsh";

        return manifest;
    }
}
