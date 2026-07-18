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
    /// Export a region as an asset and return the assembly directive to emit in its place.
    /// Returns null if the region isn't an asset region.
    /// </summary>
    /// <param name="region">the region to export</param>
    /// <param name="projectRootDir">
    /// The PROJECT root, not the assembly output dir. Assets are hand-edited source and must
    /// live outside the generated tree -- that directory gets rewritten on every export, so
    /// a PNG stored there would be someone's artwork sitting in a folder documented as safe
    /// to delete.
    /// </param>
    /// <param name="asmToProjectRootPrefix">
    /// Relative path from the .asm's directory back to the project root (e.g. ".."), used to
    /// build an incbin path that resolves correctly from where the .asm actually lives.
    /// Empty when the .asm sits at the project root.
    /// </param>
    string ExportRegion(IRegion region, string projectRootDir, string asmToProjectRootPrefix = "");
}

public class RegionAssetExportService : IRegionAssetExportService
{
    // where source assets land, relative to the project root. this is the BASE layer of the
    // override search path: it must always be complete, because mod layers are allowed to
    // be sparse and fall through to it.
    public const string BaseAssetLayer = "assets/src";

    // where the BUILD writes recompiled asset bytes -- what the .asm incbin's, NOT the .bin
    // in the source layer. The source-layer .bin is only a seed (gfxpack turns it into a
    // PNG once; the PNG is then canonical). If the .asm incbin'd the seed, PNG edits would
    // compile to a file nothing reads and the ROM would silently keep the original bytes.
    public const string BuildAssetDir = "build/assets";

    private readonly IReadOnlyByteSource byteSource;
    private readonly ISnesAddressConverter addressConverter;
    private readonly IReadOnlyList<IRegionAssetExporter> exporters;

    public RegionAssetExportService(
        IReadOnlyByteSource byteSource,
        ISnesAddressConverter addressConverter,
        IEnumerable<IRegionAssetExporter> exporters)
    {
        this.byteSource = byteSource;
        this.addressConverter = addressConverter;
        this.exporters = exporters.ToList();
    }

    public bool IsAssetRegion(IRegion region) =>
        region != null && region.ExportType != RegionExportType.Assembly;

    public string ExportRegion(IRegion region, string projectRootDir, string asmToProjectRootPrefix = "")
    {
        if (!IsAssetRegion(region))
            return null;

        var exporter = exporters.FirstOrDefault(e => e.Handles == region.ExportType);
        if (exporter == null)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' wants export type {region.ExportType}, " +
                "but no exporter is registered for it.");

        var (pcOffset, bytes) = ReadRegionBytes(region);

        return exporter.Export(new RegionAssetExportRequest
        {
            Region = region,
            Bytes = bytes,
            PcOffset = pcOffset,
            AssetRootDir = Path.Combine(projectRootDir, BaseAssetLayer.Replace('/', Path.DirectorySeparatorChar)),
            AssetRefPrefix = JoinAsmPath(asmToProjectRootPrefix, BuildAssetDir),
        });
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
    /// NOTE on bounds: EndSnesAddress is treated as EXCLUSIVE here, matching both
    /// Data.GetRegion() and the existing region size math in AsmCreationInstructions.
    /// (GetRegionAtOffset uses an inclusive compare -- that inconsistency predates this
    /// code. Getting it wrong here would shift every byte after the region, so we follow
    /// the size math, which is what actually determines the emitted output.)
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

        var length = endPc - startPc;
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
