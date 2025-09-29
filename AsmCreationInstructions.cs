using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.util;

namespace Diz.LogWriter;

// this class outputs the meat of the assembly generation process, it prints the actual assembler instructions
public class AsmCreationInstructions : AsmCreationBase
{
    public bool EnableRegionIncSrc { get; init; } = true;
    
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
        var bank = GetBankFromOffset(offset);
        if (bank == currentBank) 
            return;

        if (currentRegionName != "") {
            // we're in a region but that's not allowed when we cross banks,
            // because the "incsrc" for this won't work
            throw new InvalidDataException($"Crossing banks in a region with ExportSeparateFile=true is not allowed. In '{currentRegionName}', crossing into new bank {bank}");
        }
        
        SwitchBank(bank);
        CheckForBankCrossError(offset);
    }

    // switch to a bank, one we may or may not have visited before
    private void SwitchBank(int bank)
    {
        LogCreator.SwitchOutputStreamForBank(bank);

        if (!visitedBanks.Contains(bank)) {
            visitedBanks.Add(bank);
            LogCreator.WriteOrgDirectiveForOffset(bank << 16);
        }

        currentBank = bank;
    }

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }

    protected override void Execute()
    {
        var romSize = LogCreator.GetRomSize();
        
        // perf: this is the meat of the export, takes a while
        for (var offset = 0; offset < romSize;) {
            WriteOutputLinesForRomOffset(ref offset);
        }

        LogCreator.ReportVisitedBanks(visitedBanks);
    }

    private readonly List<string> previousRegions = [];
    private string currentRegionName = "";

    private void CheckIfRegionChanged(int offset)
    {
        if (!EnableRegionIncSrc)
            return;
        
        // can be "" if no region here
        var nextRegionName = GetRegionAtOffset(offset);

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
        LogCreator.WriteHeaderForNewlyIncludedFile(offset, "region", nextRegionName);

        currentRegionName = nextRegionName;
    }

    private string GetRegionAtOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            return "";
        
        // find any applicable regions in the surrounding context of where we are in the ROM offset
        var applicableOrderedRegions = Data.Data.Regions
            .Where(x => 
                snesAddress >= x.StartSnesAddress && 
                snesAddress <= x.EndSnesAddress && 
                x.ExportSeparateFile
            )
            .OrderBy(x => x.Priority)
            .ToList();

        var nextRegion = applicableOrderedRegions.FirstOrDefault(); // can be null
        
        // in the future maybe we can deal with this
        if (applicableOrderedRegions.Count > 1)
            throw new InvalidDataException($"Multiple overlapping regions with ExportSeparateFile=true. This is not supported. '{nextRegion?.RegionName}'");

        // will be empty if no region 
        return nextRegion?.RegionName ?? "";
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