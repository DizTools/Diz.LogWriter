using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

public interface IRegionAssetExportService
{
    bool IsAssetRegion(IRegion region);

    /// <summary>
    /// Every build node reported by the regions exported so far, in export order. The build
    /// file generator reads this after the assembly pass, so it describes exactly what was
    /// exported rather than what the project's regions imply.
    /// </summary>
    IReadOnlyList<AssetBuildNode> ExportedBuildNodes { get; }

    /// <summary>
    /// Export a region as an asset and return what it produced -- the assembly directive to
    /// emit in its place, and the build nodes for it. Returns null if the region isn't an
    /// asset region.
    /// </summary>
    /// <param name="region">the region to export</param>
    /// <param name="manifestRootDir">
    /// Where asset manifests are written: the "assets" folder inside the assembly output dir.
    /// Manifests are generated output -- rewritten on every export -- so they belong in the
    /// same tree as the .asm, not next to anything hand-edited.
    /// </param>
    /// <param name="asmToProjectRootPrefix">
    /// Relative path from the .asm's directory back to the project root (e.g. ".."), used to
    /// build an incbin path that resolves correctly from where the .asm actually lives.
    /// Empty when the .asm sits at the project root.
    /// </param>
    RegionAssetExportResult ExportRegion(IRegion region, string manifestRootDir, string asmToProjectRootPrefix = "");
}

public class RegionAssetExportService : IRegionAssetExportService
{
    /// <summary>
    /// Sub-folder that holds assets inside a tier directory: manifests at
    /// &lt;generated&gt;/assets, compiled payloads at &lt;build&gt;/assets.
    /// </summary>
    public const string AssetSubDir = "assets";

    /// <summary>Default build tier name; see LogWriterSettings.BuildDirPath.</summary>
    public const string DefaultBuildDir = "build";

    private readonly IReadOnlyByteSource byteSource;
    private readonly ISnesAddressConverter addressConverter;
    private readonly IReadOnlyList<IRegionAssetExporter> exporters;

    // Where the BUILD writes recompiled asset bytes -- what the .asm incbin's. Diz never
    // writes here: it only names the path. If the .asm pointed at an editable source instead,
    // edits would compile to a file nothing reads and the ROM would silently keep the
    // original bytes.
    private readonly string buildAssetDir;

    // accumulated as regions are exported, because only the exporter knows what a region turned
    // into: reading the graph back off the regions afterwards would parse the same authoring a
    // second time, and the two readings would be free to disagree.
    private readonly List<AssetBuildNode> exportedBuildNodes = [];

    public RegionAssetExportService(
        IReadOnlyByteSource byteSource,
        ISnesAddressConverter addressConverter,
        IEnumerable<IRegionAssetExporter> exporters,
        string buildDir = DefaultBuildDir)
    {
        this.byteSource = byteSource;
        this.addressConverter = addressConverter;
        this.exporters = exporters.ToList();
        buildAssetDir = $"{(buildDir ?? DefaultBuildDir).Replace('\\', '/').Trim('/')}/{AssetSubDir}";
    }

    public bool IsAssetRegion(IRegion region) =>
        region != null && region.ExportType != RegionExportType.Assembly;

    public IReadOnlyList<AssetBuildNode> ExportedBuildNodes => exportedBuildNodes;

    public RegionAssetExportResult ExportRegion(IRegion region, string manifestRootDir, string asmToProjectRootPrefix = "")
    {
        if (!IsAssetRegion(region))
            return null;

        var exporter = exporters.FirstOrDefault(e => e.CanExport(region));
        if (exporter == null)
        {
            var assetTypeNote = string.IsNullOrWhiteSpace(region.AssetType)
                ? ""
                : $" / asset type '{region.AssetType}'";
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' wants export type {region.ExportType}{assetTypeNote}, " +
                "but no exporter is registered for it.");
        }

        var (pcOffset, bytes) = ReadRegionBytes(region);

        var result = exporter.Export(new RegionAssetExportRequest
        {
            Region = region,
            Bytes = bytes,
            PcOffset = pcOffset,
            ManifestRootDir = manifestRootDir,
            AssetRefPrefix = JoinAsmPath(asmToProjectRootPrefix, buildAssetDir),
        });

        if (result.BuildNodes == null || result.BuildNodes.Count == 0)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' exported a manifest but reported nothing for the " +
                "build to rebuild; its bytes would never be recompiled.");

        exportedBuildNodes.AddRange(result.BuildNodes);
        return result;
    }

    /// <summary>
    /// Join a relative prefix onto an asset path for use inside an assembly directive.
    /// Always forward slashes: asar accepts them on every platform, and the .asm is checked
    /// in, so it must not carry host-specific separators.
    /// </summary>
    public static string JoinAsmPath(string prefix, string path)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == ".")
            return path;

        return $"{prefix.Replace('\\', '/').TrimEnd('/')}/{path}";
    }

    /// <summary>
    /// Read a region's bytes out of the ROM.
    ///
    /// NOTE on bounds: EndSnesAddress is treated as INCLUSIVE here (the last byte IN the
    /// region), matching Data.GetRegion() and the region size math in AsmCreationInstructions.
    /// Getting it wrong here would shift every byte after the region, so we follow the size
    /// math, which is what actually determines the emitted output.
    /// </summary>
    private (int pcOffset, byte[] bytes) ReadRegionBytes(IRegion region)
    {
        var startPc = addressConverter.ConvertSnesToPc(region.StartSnesAddress);
        var endPc = addressConverter.ConvertSnesToPc(region.EndSnesAddress);

        if (startPc == -1 || endPc == -1)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' has SNES addresses that don't map into ROM " +
                $"(${region.StartSnesAddress:X6}..${region.EndSnesAddress:X6}). " +
                "Asset export only works for regions backed by real ROM bytes.");

        var length = endPc - startPc + 1;
        if (length <= 0)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' has a non-positive length ({length} bytes). " +
                "Check that the end address is after the start address.");

        var bytes = new byte[length];
        for (var i = 0; i < length; ++i)
        {
            bytes[i] = byteSource.GetRomByte(startPc + i)
                       ?? throw new InvalidOperationException(
                           $"Region '{region.RegionName}': no ROM byte at PC offset " +
                           $"0x{startPc + i:X} (region runs past the end of the ROM).");
        }

        return (startPc, bytes);
    }
}
