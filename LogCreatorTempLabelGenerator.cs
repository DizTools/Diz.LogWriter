#nullable enable
using System.Collections.Generic;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

// Generate labels like "CODE_856469" and "DATA_763525" and +/- labels
// These will be combined with the original labels to produce our final assembly
// These labels exist only for the duration of this export, and then are discarded.
internal class LogCreatorTempLabelGenerator
{
    public LogCreator LogCreator { get; init; }
    public ILogCreatorDataSource<IData> Data => LogCreator.Data;
    public bool GenerateAllUnlabeled { get; init; }
    
    public bool ShouldGeneratePlusMinusLabels { get; init; }
        
    public void ClearTemporaryLabels()
    {
        // restore original labels. SUPER IMPORTANT THIS HAPPENS WHen WE'RE DONE
        Data.TemporaryLabelProvider.ClearTemporaryLabels();
    }

    public void GenerateTemporaryLabels()
    {
        // 1. all labels [like "CODE_xxxx" and "DATA_xxxx"] but not +/- labels
        GenerateSectionTempLabels();
        
        // 2. generate ONLY +/- labels
        //    do this second because we'll selectively overwrite some types of labels (like "CODE_")
        if (ShouldGeneratePlusMinusLabels)
            GeneratePlusMinusLabels();
    }

    private void GenerateSectionTempLabels()
    {
        var romSize = LogCreator.GetRomSize();
        for (var offset = 0; offset < romSize; offset += LogCreator.GetLineByteLength(offset))
        {
            GenerateTempLabelIfNeededAt(offset);
        }
    }

    private class Branch
    {
        public int SrcOffset = -1;
        public int DestOffset = -1;
    }

    private class BranchState
    {
        public int DestOffset;
        
        public int Depth = 1;    // depth of 1 is "+", 2 is "++", etc
        public string Label => new string(IsForwardBranch ? '+' : '-', Depth);
        public bool IsForwardBranch = true;
    }

    private void EmitInternalPlusMinusBranches(List<Branch> validBranches)
    {
        var snesData = Data.Data.GetSnesApi();
        if (snesData == null)
            return;
        
        var forwardBranches = validBranches
            .Where(b => b.DestOffset > b.SrcOffset)
            .OrderBy(b => b.SrcOffset)
            .ToList();
        
        var backwardBranches = validBranches
            .Where(b => b.DestOffset < b.SrcOffset)
            .OrderByDescending(b => b.SrcOffset)
            .ToList();
        
        GenerateLocalPlusMinusBranchLabelsOneDirection(forwardBranches, directionIsForward: true);
        GenerateLocalPlusMinusBranchLabelsOneDirection(backwardBranches, directionIsForward: false);
    }

    private void GenerateLocalPlusMinusBranchLabelsOneDirection(List<Branch> forwardBranches, bool directionIsForward)
    {
        var states = new List<BranchState>();
        foreach (var branch in forwardBranches)
        {
            // 1. remove any branches we moved past before processing this next branch:
            var startingOffset = branch.SrcOffset;
            states = states
                .Where(x => directionIsForward 
                    ? x.DestOffset > startingOffset 
                    : x.DestOffset < startingOffset)
                .ToList();
            
            // 2. what label should we use for this branch?
            var statesSortedByDepth = states.OrderBy(x => x.Depth).ToList();
            var targetDepth = 1;
            BranchState? stateToUse = null;

            foreach (var state in statesSortedByDepth.Where(state => state.DestOffset == branch.DestOffset))
            {
                // found an existing one! use that.
                stateToUse = state;
                targetDepth = state.Depth;
                break;
            }

            if (stateToUse == null)
            {
                foreach (var _ in statesSortedByDepth.TakeWhile(state => state.Depth == targetDepth))
                {
                    // keep looking further down
                    targetDepth++;
                }

                // 3. picked a valid depth.
                //    now add a new branch state for this branch at that depth
                var newState = new BranchState
                {
                    IsForwardBranch = directionIsForward,
                    Depth = targetDepth,
                    DestOffset = branch.DestOffset,
                };
                states.Add(newState);
                stateToUse = newState;
            }
            
            // 4. create the label for this branch:
            var snesDestOffset = Data.ConvertPCtoSnes(branch.DestOffset);
            
            // assumes any existing local label (+/-) we're replacing is IDENTICAL to what we're adding
            // otherwise, it's going to create an error
            Data.TemporaryLabelProvider.AddOrReplaceTemporaryLabel(snesDestOffset, new Label { Name = stateToUse.Label });
        }
    }

    private void EmitPlusMinusLabels(List<Branch> branches)
    {
        // Step 1: Filter branches to avoid conflicting labels (forward vs backward)
        var forwardDestOffsets = branches
            .Where(b => b.DestOffset > b.SrcOffset)
            .Select(b => b.DestOffset)
            .ToHashSet();
            
        var backwardDestOffsets = branches
            .Where(b => b.DestOffset < b.SrcOffset)
            .Select(b => b.DestOffset)
            .ToHashSet();
            
        var conflictingOffsets = forwardDestOffsets.Intersect(backwardDestOffsets).ToHashSet();
        
        // Filter out branches with conflicting destinations
        var validBranches = branches
            .Where(b => !conflictingOffsets.Contains(b.DestOffset))
            .ToList();
        
        EmitInternalPlusMinusBranches(validBranches);
    }

    private void GeneratePlusMinusLabels()
    {
        var romSize = LogCreator.GetRomSize();
        var snesData = Data.Data.GetSnesApi();
        if (snesData == null)
            return;
        
        // remember not to cross banks with this.
        var lastBank = -1;
        var validBranches = new List<Branch>();
        
        for (var sourceOffset = 0; sourceOffset < romSize; sourceOffset++)
        {
            var sourceSnesAddr = snesData.ConvertPCtoSnes(sourceOffset);
            var bank = RomUtil.GetBankFromSnesAddress(sourceSnesAddr);
            if (bank != lastBank && lastBank != -1)
            {
                EmitPlusMinusLabels(validBranches);
                validBranches.Clear();
            }
            lastBank = bank;
            
            // found our opcode. does it qualify as a conditional branch/subroutine call?
            // this isn't going to be foolproof but it should catch 95% of the stuff we most care about
            // we're ignoring: JMP JML BRA BRL (because they don't return to this point)
            if (snesData.GetFlag(sourceOffset) != FlagType.Opcode)
                continue;

            var opcode = snesData.GetRomByte(sourceOffset);
            var opcodeIsBranch = opcode == 0x80 ||  // BRA
                                 opcode == 0x10 || opcode == 0x30 || opcode == 0x50 || opcode == 0x70 ||     // BPL BMI BVC BVS
                                 opcode == 0x90 || opcode == 0xB0 || opcode == 0xD0 || opcode == 0xF0;       // BCC BCS BNE BEQ
            // NOT going to do this for any JUMPs like JMP, JML, and also not BRL
            
            if (!opcodeIsBranch)
                continue;
            
            // let's make sure the destination is an acceptable candidate
            var destSnesAddr = Data.GetIntermediateAddressOrPointer(sourceOffset);
            var destOffset = destSnesAddr == -1 ? -1 : Data.ConvertSnesToPc(destSnesAddr);
            if (destOffset == -1)
                continue;
            
            // source and destination both good
            var branchDirection = destOffset - sourceOffset;
            if (branchDirection == 0) // TECHNNNNNNNNNNICALLY, this is ok as it's an infinite loop. but, for now, ignore it.
                continue;

            // finally, we only want to consider stuff if there's not already a label set for the destination
            var existingLabel = Data.TemporaryLabelProvider.GetLabel(destSnesAddr);
            
            var isLowPriTempLabel = 
                existingLabel != null && 
                existingLabel.Name.StartsWith("CODE_") &&         // allowed to overwrite these
                !existingLabel.Name.StartsWith("CODE_F") &&       // but not these
                !existingLabel.Name.StartsWith("CODE_J");
            
            var isPlusMinusHandwrittenLabel = RomUtil.IsValidPlusMinusLabel(existingLabel?.Name ?? "");
            
            var overwriteLabelAllowed = isLowPriTempLabel || isPlusMinusHandwrittenLabel;
            if (!overwriteLabelAllowed)
                continue;  // skip putting a label on this particular branch

            // ok, we have a branch in the right direction, mark it:
            validBranches.Add(new Branch  {
                DestOffset = destOffset,
                SrcOffset = sourceOffset,
            });
        }
        
        // do it for anything remaining at the very end
        EmitPlusMinusLabels(validBranches);
    }

    private void GenerateTempLabelIfNeededAt(int originOffset)
    {
        var snesData = Data.Data.GetSnesApi();
        if (snesData == null)
            return;
        
        var snesAddressToGenerateLabelAt = -1;
        var useHints = false;
        
        // 1. treat our offset as the origin, check if it references an IA that is interesting to make a label from: 
        var flag = snesData.GetFlag(originOffset);
        var originWasOpcode = flag == FlagType.Opcode;
        var destinationIaMightBeInteresting = originWasOpcode || flag is 
            FlagType.Pointer16Bit or 
            FlagType.Pointer24Bit or 
            FlagType.Pointer32Bit;

        if (destinationIaMightBeInteresting)
        {
            var snesDestinationIa = Data.GetIntermediateAddressOrPointer(originOffset);
            var offsetOfIa = Data.ConvertSnesToPc(snesDestinationIa);
            if (offsetOfIa != -1)
            {
                snesAddressToGenerateLabelAt = snesDestinationIa;
                useHints = true;
            }
        }

        // 2. our origin address doesn't have anything interesting going on.
        // should we add a label for the origin address anyway? (in case we want to see ALL labels [not typical but an option])
        if (snesAddressToGenerateLabelAt == -1 && GenerateAllUnlabeled)
        {
            snesAddressToGenerateLabelAt = Data.ConvertPCtoSnes(originOffset);
            useHints = false;
        }
        
        // no reason to create any new labels, bail
        if (snesAddressToGenerateLabelAt == -1)
            return; 

        var prefix = "";
        var offsetToGenerateLabelAt = Data.ConvertSnesToPc(snesAddressToGenerateLabelAt);

        if (useHints)
        {
            // figure out if there's anything interesting going on that we might want to change the label somewhat:
            var destinationIsOpcode = snesData.GetFlag(offsetToGenerateLabelAt) == FlagType.Opcode;
            
            // A. was this a JSR or JSL, and is the destination location reached?
            if (originWasOpcode && destinationIsOpcode)
            {
                var originRomByte = snesData.GetRomByte(originOffset);
                switch (originRomByte)
                {
                    case 0x4C:  // JMP
                        prefix = "CODE_JP";
                        break;
                    case 0x5C:  case 0x82:  // JML BRL
                        prefix = "CODE_JL";
                        break;
                    case 0x20:  // JSR
                        prefix = "CODE_FN";
                        break;
                    case 0x22:  // JML
                        prefix = "CODE_FL";
                        break;
                    default:
                        prefix = "";
                        break;
                }
            }
        }
        
        var existingLabel = Data.TemporaryLabelProvider.GetLabel(snesAddressToGenerateLabelAt);
        if (existingLabel != null)
        {
            var existingLabelIsFn = existingLabel.Name.StartsWith("CODE_F");
            var existingLabelIsJmp = existingLabel.Name.StartsWith("CODE_J");

            // if things JMP and JSR to the same location, let the CODE_Fx_xxxxxx label take priority
            var skipOtherChecks = prefix.StartsWith("CODE_F") && existingLabelIsJmp;
            if (!skipOtherChecks)
            {
                var existingLabelIsHighPriority = 
                    existingLabelIsFn || 
                    existingLabelIsJmp;     // like "CODE_FN" or "CODE_FL", or "CODE_JM" or "CODE_JL"
            
                if (existingLabelIsHighPriority)
                    return;   
            }
        }

        // this is like "CODE_", "DATA_", "EMPTY_" etc
        if (prefix.Length == 0)
            // 2. nothing special, just generate a generic label for this like CODE_xxxx or DATA_xxx
            prefix = RomUtil.TypeToLabel(snesData.GetFlag(offsetToGenerateLabelAt));
        
        var labelAddress = Util.ToHexString6(snesAddressToGenerateLabelAt);
        var labelName = $"{prefix}_{labelAddress}";
        
        Data.TemporaryLabelProvider.AddOrReplaceTemporaryLabel(snesAddressToGenerateLabelAt, new Label {Name = labelName});
    }
}