using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.model.snes;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.assets;
using JetBrains.Annotations;

namespace Diz.LogWriter;

// this class outputs the meat of the assembly generation process, it prints the actual assembler instructions
public class AsmCreationInstructions : AsmCreationBase
{
    public bool EnableRegionIncSrc { get; init; } = true;

    // Optional: when both are set, regions marked with an ExportType other than 'Assembly'
    // have their bytes written out as standalone asset files and replaced in the .asm with
    // an `incbin`. Left null, nothing changes and every region exports inline `db` bytes
    // exactly as before.
    [CanBeNull] public IRegionAssetExportService AssetExportService { get; init; }

    // PROJECT root, not the assembly output dir -- assets are hand-edited source and must not
    // live inside the tree that export rewrites.
    [CanBeNull] public string AssetExportRootDir { get; init; }

    // relative path from the .asm's directory back to the project root (e.g. ".."), so the
    // emitted incbin resolves from wherever the .asm actually lives.
    public string AssetAsmToProjectRootPrefix { get; init; } = "";

    private bool AssetExportEnabled =>
        AssetExportService != null && !string.IsNullOrEmpty(AssetExportRootDir);

    // regions we've already emitted an incbin for, so a region can't be written twice
    private readonly HashSet<string> exportedAssetRegions = [];

    private readonly List<int> visitedBanks = [];
    private int currentBank = -1;
    
    private int GetBankFromOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            throw new InvalidDataException($"Rom offset required to map to SNES address: {offset}");
        
        return RomUtil.GetBankFromSnesAddress(snesAddress);
    }
    
    private void SwitchBanksIfNeeded(int offset)
    {
        // remember: in LoRom mapping, an offset like 0 will map to SNES address $808000.
        
        var bank = GetBankFromOffset(offset);
        if (bank == currentBank) 
            return;

        if (currentRegionName != "") {
            // we're in a region but that's not allowed when we cross banks,
            // because the "incsrc" for this won't work
            throw new InvalidDataException($"Crossing banks in a region with ExportSeparateFile=true is not allowed. In '{currentRegionName}', crossing into new bank {bank}");
        }
        
        SwitchBank(bank, offset);
        CheckForBankCrossError(offset);
    }

    // switch to a bank, one we may or may not have visited before.
    // we also need the real offset because in LoRom, bank 80 starts at ROM offset 0x00 == SNES address 0x808000
    private void SwitchBank(int bank, int offset)
    {
        LogCreator.SwitchOutputStreamForBank(bank);
        
        // note: we can't just bank<<16 here, it will produce incorrect output for LoRom
        var snesAddress = Data.ConvertPCtoSnes(offset);

        if (!visitedBanks.Contains(bank)) {
            visitedBanks.Add(bank);
            LogCreator.WriteOrgDirectiveForSnesAddress(snesAddress);
        }

        currentBank = bank;
    }

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }

    // includes both real regions from Data, and
    // temporary extra regions generated dynamically from comments
    private List<IRegion> allRegions = [];

    protected override void Execute()
    {
        GenerateExtraIncSrcRegionsFromComments();

        var romSize = LogCreator.GetRomSize();
        
        // perf: this is the meat of the export, takes a while
        for (var offset = 0; offset < romSize;) {
            WriteOutputLinesForRomOffset(ref offset);
        }

        LogCreator.ReportVisitedBanks(visitedBanks);
    }

    private void GenerateExtraIncSrcRegionsFromComments()
    {
        var regionSnesAddrStart = -1;
        var regionName = "";
        
        // start with all existing real regions, and we'll append from here
        allRegions.Clear();
        allRegions.AddRange(LogCreator.Data.Data.Regions);
        
        foreach (var (snesAddress, comment) in LogCreator.Data.Data.Comments)
        {
            var parsed = CpuUtils.ParseCommentSpecialDirective(comment);
            if (parsed == null || parsed.IncludeSrc == CpuUtils.OperandOverride.IncSrcOverride.None)
                continue;
            
            var offset = Data.ConvertSnesToPc(snesAddress);

            switch (parsed.IncludeSrc)
            {
                case CpuUtils.OperandOverride.IncSrcOverride.IncSrcStart:
                {
                    if (regionSnesAddrStart != -1)
                    {
                        // we're already inside a region, so we can't generate a new one
                        // this is a bug in the source code, but we'll just ignore it
                        LogCreator.OnErrorReported(offset,
                            "Extra region directive found 'ir' inside a dynamic region. This is not supported.");
                        continue;
                    }

                    var labelName = Data.Labels.GetLabel(snesAddress)?.Name;
                    if ((labelName?.Length ?? 0) == 0)
                    {
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ir' without a label defined. This is not supported.");
                        continue;
                    }
                
                    // good to go
                    regionName = labelName;
                    regionSnesAddrStart = snesAddress;
                    break;
                }
                case CpuUtils.OperandOverride.IncSrcOverride.IncSrcEnd:
                {
                    if (regionSnesAddrStart == -1)
                    {
                        // we're not inside a region, so we can't generate a new one
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ie' outside a dynamic region. This is not supported. Ignoring");;
                        continue;
                    }

                    if (regionName == "")
                    {
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ie'  name defined. This is not supported. Ignoring");
                        continue;
                    }
                
                    // good to go
                    allRegions.Add(new Region {
                        ExportSeparateFile = true,
                        RegionName = regionName,
                        Priority = 0,
                        StartSnesAddress = regionSnesAddrStart,
                        EndSnesAddress = snesAddress,
                    });

                    // reset
                    regionName = "";
                    regionSnesAddrStart = -1;
                    break;
                }
            }
        }

        if (regionSnesAddrStart != -1) {
            LogCreator.OnErrorReported(Data.ConvertSnesToPc(regionSnesAddrStart), $"Unclosed region start directive '{regionName}', ignoring");
        }
    }

    private readonly List<string> previousRegions = [];
    private string currentRegionName = "";

    private void CheckIfRegionChanged(int offset)
    {
        if (!EnableRegionIncSrc)
            return;
        
        var nextRegion = GetRegionAtOffset(offset);                     // can be null if no region here
        var nextRegionName = nextRegion?.RegionName ?? "";

        // did anything change?
        if (nextRegionName == currentRegionName)
            return; // no
        
        var leavingActiveRegion = currentRegionName.Length > 0;
        if (leavingActiveRegion)
        {
            // switch out of the region and back into the current bank
            LogCreator.SwitchOutputStreamForBank(currentBank);
            currentRegionName = "";
        }
        
        // we're back in the parent bank now
        
        // if we're next crossing the bank, don't allow jumping into a new region
        // (bank crossing is part of the reason why caller must to call this function twice, before and after bank is crossed)
        var crossingBankNext = currentBank != GetBankFromOffset(offset);
        if (crossingBankNext)
            return;
        
        var goingIntoNewRegion = nextRegionName.Length > 0;
        if (!goingIntoNewRegion) 
            return;
        
        // make sure we haven't already included a region like this (invalid)
        if (previousRegions.Contains(currentRegionName))
            throw new InvalidDataException($"Multiple regions  with ExportSeparateFile=true use the same name '{currentRegionName}, invalid'");
            
        previousRegions.Add(nextRegionName);
            
        var regionNameIncSrcFilenameTarget = nextRegionName + ".asm";
            
        // we're in the bank file, write an "incsrc" to the new file we're about to use
        LogCreator.WriteIncludeFileDirective(regionNameIncSrcFilenameTarget, padWithBlankLine: true);
            
        // switch further output to the region-specific new file:
        LogCreator.SwitchOutputStream(regionNameIncSrcFilenameTarget);

        var regionBytesSize = -1;
        if (nextRegion != null)
        {
            var startOffset = Data.ConvertSnesToPc(nextRegion.StartSnesAddress);
            var endOffset = Data.ConvertSnesToPc(nextRegion.EndSnesAddress);
            if (startOffset != -1 && endOffset != -1 && GetBankFromOffset(startOffset) == GetBankFromOffset(endOffset)) {
                // we'll only report the size if they're in the same bank, otherwise the math gets weird. maybe. unsure. whatever
                // EndSnesAddress is inclusive (last byte IN the region), so add 1 to get the byte count
                regionBytesSize = endOffset - startOffset + 1;
            }
        }
        LogCreator.WriteHeaderForNewlyIncludedFile(offset, "region", nextRegionName, regionBytesSize);

        currentRegionName = nextRegionName;
    }

    [CanBeNull]
    private IRegion GetRegionAtOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            return null;
        
        // find any applicable regions in the surrounding context of where we are in the ROM offset
        var applicableOrderedRegions = allRegions
            .Where(x => 
                snesAddress >= x.StartSnesAddress && 
                snesAddress <= x.EndSnesAddress && 
                x.ExportSeparateFile
            )
            .OrderByDescending(x => x.Priority)
            .ToList();

        var region = applicableOrderedRegions.FirstOrDefault(); // can be null
        
        // in the future maybe we can deal with this
        if (applicableOrderedRegions.Count > 1)
            throw new InvalidDataException($"Multiple overlapping regions with ExportSeparateFile=true. This is not supported. '{region?.RegionName}'");
        
        return region;
    }

    // write one line of the assembly output
    // address is a "PC address" i.e. offset into the ROM.
    // not a SNES address.
    private void WriteOutputLinesForRomOffset(ref int offset)
    {
        // since we can only CHANGE INTO a region when we're in a bank, and if we're just starting, we're not in a bank yet,
        // skip this check. this check will ONLY make sure  
        if (currentBank != -1) {
            CheckIfRegionChanged(offset);
        }

        // switch output to another bank file if needed.
        // note: being inside a region during a bank cross
        SwitchBanksIfNeeded(offset);
        CheckIfRegionChanged(offset);

        // if an asset region starts here, emit one `incbin` and skip its bytes entirely,
        // rather than emitting them inline. must happen before the normal line generation.
        if (TryWriteAssetRegion(ref offset))
            return;

        WriteBlankLineIfStartingNewParagraph(offset);
        GenerateAndWriteCodeOutputLinesForRomOffset(offset);    // the important thing
        LogCreator.DataErrorChecking.CheckForErrorsAt(offset);
        WriteBlankLineIfEndPoint(offset);

        // TODO: WARNING: TECHNICALLY, we should be checking for bank and region crosses
        //  looking at our previous offset through our new offset.  we could miss transitions or
        //  put region includes in the wrong spot.  Happens if there's regions that begin in the middle of 
        //  boundaries like inside the middle byte of data labelled as 24-bit.
        offset += LogCreator.GetLineByteLength(offset);
    }

    /// <summary>
    /// If an asset region begins exactly at this offset, write its bytes out to a standalone
    /// asset file, emit an `incbin` in place of them, and advance past the whole region.
    /// Returns true if it handled the offset (caller must not also emit normal lines).
    /// </summary>
    private bool TryWriteAssetRegion(ref int offset)
    {
        if (!AssetExportEnabled)
            return false;

        var region = GetAssetRegionStartingAt(offset);
        if (region == null)
            return false;

        var startPc = Data.ConvertSnesToPc(region.StartSnesAddress);
        var endPc = Data.ConvertSnesToPc(region.EndSnesAddress);

        // EndSnesAddress is treated as INCLUSIVE (the last byte IN the region), matching
        // Data.GetRegion() and the region size math above. If that's ever changed, this must
        // change with it -- getting it wrong here shifts every byte after the region.
        var length = endPc - startPc + 1;
        if (length <= 0)
            throw new InvalidDataException(
                $"Asset region '{region.RegionName}' has a non-positive length ({length}).");

        // an asset region must sit inside one bank: the incbin lands in that bank's file,
        // and a region spanning banks would silently put bytes in the wrong place.
        if (GetBankFromOffset(startPc) != GetBankFromOffset(endPc))
            throw new InvalidDataException(
                $"Asset region '{region.RegionName}' crosses a bank boundary, which isn't supported.");

        var directive = AssetExportService.ExportRegion(
            region, AssetExportRootDir, AssetAsmToProjectRootPrefix);
        if (directive == null)
            return false;

        LogCreator.WriteEmptyLine();
        LogCreator.WriteHeaderForNewlyIncludedFile(offset, "asset", region.RegionName, length);
        LogCreator.WriteLine(directive);
        LogCreator.WriteEmptyLine();

        exportedAssetRegions.Add(region.RegionName);
        offset += length;
        return true;
    }

    [CanBeNull]
    private IRegion GetAssetRegionStartingAt(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            return null;

        var matching = allRegions
            .Where(x => x.StartSnesAddress == snesAddress && AssetExportService.IsAssetRegion(x))
            .ToList();

        if (matching.Count == 0)
            return null;

        if (matching.Count > 1)
            throw new InvalidDataException(
                "Multiple asset regions start at the same address " +
                $"(${snesAddress:X6}): {string.Join(", ", matching.Select(x => x.RegionName))}. " +
                "This is ambiguous -- only one region can own a range of bytes.");

        var region = matching[0];
        if (exportedAssetRegions.Contains(region.RegionName))
            throw new InvalidDataException(
                $"Asset region '{region.RegionName}' was already exported. Region names must be unique.");

        return region;
    }

    private void GenerateAndWriteCodeOutputLinesForRomOffset(int offset)
    {
        // one ROM offset might generate multiple lines of output (example: if there's a multi-line comment)
        // generate them all and write them out:
        var outputLines = LogCreator.LineGenerator.GenerateNormalLines(offset);
        foreach (var outputLine in outputLines) {
            LogCreator.WriteLine(outputLine);
        }
    }

    private void WriteBlankLineIfStartingNewParagraph(int offset)
    {
        // skip if we're in the middle of a pointer table
        if (Data.GetFlag(offset) is FlagType.Pointer16Bit or FlagType.Pointer24Bit or FlagType.Pointer32Bit)
            return;

        if (Data.IsLocationAReadPoint(offset) || AreAnyLabelsPresentAt(offset)) 
            LogCreator.WriteEmptyLine();
    }

    private bool AreAnyLabelsPresentAt(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        return Data.Labels.GetLabel(snesAddress)?.Name.Length > 0;
    }

    private void WriteBlankLineIfEndPoint(int offset)
    {
        if (!Data.IsLocationAnEndPoint(offset))
            return;

        LogCreator.WriteEmptyLine();
    }
}