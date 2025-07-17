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
        //    do this LAST because we'll selectively overwrite some types of labels (like "CODE_")
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
        
        public int Depth = 1;    // depth of 1 is "+" or "-", 2 is "++" or "--", etc
        public string Label => new(IsForwardBranch ? '+' : '-', Depth);
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
        
        // generate "+" labels:
        GenerateLocalPlusMinusBranchLabelsOneDirection(forwardBranches, directionIsForward: true);
        
        // generate "-" labels:
        GenerateLocalPlusMinusBranchLabelsOneDirection(backwardBranches, directionIsForward: false);
    }

    private void GenerateLocalPlusMinusBranchLabelsOneDirection(List<Branch> validBranches, bool directionIsForward)
    {
        var states = new List<BranchState>();
        foreach (var branch in validBranches)
        {
            // 1. remove any branches we moved past before processing this next branch
            //    i.e. branches in the wrong direction
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
        
        // remember not to ever let branch labels cross banks
        var lastBank = -1;
        var validBankBranches = new List<Branch>();
        
        // go through each bank, generate +/- labels for valid branches found. do not let these
        for (var sourceOffset = 0; ;sourceOffset++)
        {
            var inBounds = sourceOffset < romSize;

            var crossedBankBoundary = false;
            if (inBounds)
            {
                // track if we crossed banks
                var sourceSnesAddr = snesData.ConvertPCtoSnes(sourceOffset);
                var bank = RomUtil.GetBankFromSnesAddress(sourceSnesAddr);
                crossedBankBoundary = bank != lastBank && lastBank != -1;
                lastBank = bank;
            }

            var emitPlusMinusLabelsForThisBank = crossedBankBoundary || !inBounds;
            if (emitPlusMinusLabelsForThisBank)
            {
                EmitPlusMinusLabels(validBankBranches);
                validBankBranches.Clear();
            }

            if (!inBounds)
                break;

            var newValidBranch = TryGeneratePlusMinusLabelAtOffset(snesData, sourceOffset);
            if (newValidBranch != null)
                validBankBranches.Add(newValidBranch);
        }
    }

    private Branch? TryGeneratePlusMinusLabelAtOffset(ISnesData snesData, int sourceOffset)
    {
        // found our opcode. does it qualify as a conditional branch/subroutine call?
        // this isn't going to be foolproof but it should catch 95% of the stuff we most care about
        // we're ignoring: JMP JML BRA BRL (because they don't return to this point)
        if (snesData.GetFlag(sourceOffset) != FlagType.Opcode)
            return null;

        var opcode = snesData.GetRomByte(sourceOffset);
        var opcodeIsBranch = opcode == 0x80 ||  // BRA
                             opcode == 0x10 || opcode == 0x30 || opcode == 0x50 || opcode == 0x70 ||     // BPL BMI BVC BVS
                             opcode == 0x90 || opcode == 0xB0 || opcode == 0xD0 || opcode == 0xF0;       // BCC BCS BNE BEQ
        // NOT going to do this for any JUMPs like JMP, JML. and also not BRL [for now?]

        if (!opcodeIsBranch)
            return null;
            
        // let's make sure the destination is an acceptable candidate
        var destSnesAddr = Data.GetIntermediateAddressOrPointer(sourceOffset);
        var destOffset = destSnesAddr == -1 ? -1 : Data.ConvertSnesToPc(destSnesAddr);
        if (destOffset == -1) 
            return null;
            
        // source and destination both good?
        var branchDirection = destOffset - sourceOffset;
            
        // TECHNNNNNNNNNNICALLY, direction of 0 WOULD BE ok as
        // it's an infinite loop. but, can cause issues if we try and use it so, we'll just skip it
        if (branchDirection == 0)
            return null;

        // finally, we only want to consider this branch as valid for +/- label IFF
        // there's not already a higher priority label set for the destination
        var existingLabel = Data.TemporaryLabelProvider.GetLabel(destSnesAddr);
        if (existingLabel != null)
        {
            // 1. does it have an autogenerated label we're allowed to overwrite?
            var existingCodeLabelOkToOverride =
                existingLabel.Name.StartsWith("CODE_") &&    // allowed to overwrite only these
                // but not the rest of these:
                !existingLabel.Name.StartsWith("CODE_FN") &&
                !existingLabel.Name.StartsWith("CODE_FL") &&
                !existingLabel.Name.StartsWith("CODE_J");

            if (!existingCodeLabelOkToOverride)
                return null;

            // this is almost always going to mess things up if they have handwritten +/- labels but... respect it anyway:
            var isPlusMinusHandwrittenLabel = RomUtil.IsValidPlusMinusLabel(existingLabel.Name);
            if (isPlusMinusHandwrittenLabel)
                return null;

            if (existingLabel is TempLabel tempLabel &&
                (tempLabel.Flags & TempLabel.TempLabelFlags.DisallowPlusMinusGeneration) != 0)
                return null;
        }

        // ok, we have a branch in the right direction, mark it:
        return new Branch  {
            DestOffset = destOffset,
            SrcOffset = sourceOffset,
        };
    }

    private void GenerateTempLabelIfNeededAt(int originOffset)
    {
        var snesData = Data.Data.GetSnesApi();
        if (snesData == null)
            return;
        
        var snesAddressToGenerateLabelAt = -1;
        var useHints = false;
        
        var snesSrcAddress = Data.ConvertPCtoSnes(originOffset);    // address of the opcode we're starting from
        
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
            snesAddressToGenerateLabelAt = snesSrcAddress;
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
            
            // A. was this a JSR/JSL/JML/JMP, and is the destination location reached?
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
            var existingLabelIsFn = existingLabel.Name.StartsWith("CODE_FN") || existingLabel.Name.StartsWith("CODE_FL");
            var existingLabelIsJmp = existingLabel.Name.StartsWith("CODE_J");

            // if things JMP and JSR to the same location, let the CODE_Fx_xxxxxx label take priority
            var prefixLabelIsFn = prefix.StartsWith("CODE_FN") || prefix.StartsWith("CODE_FL");
            var skipOtherChecks = prefixLabelIsFn && existingLabelIsJmp;
            if (!skipOtherChecks)
            {
                var existingLabelIsHighPriority = 
                    existingLabelIsFn ||    // like "CODE_FN" or "CODE_FL"
                    existingLabelIsJmp;     // like "CODE_JM" or "CODE_JL"
            
                if (existingLabelIsHighPriority)
                    return;   
            }
        }
        
        // tweak: sometimes we want to generate a label like "CODE_" but then not have it be eligible for
        // a swap (in the next pass) to be replaced by a +/- label
        // (this can happen in certain odd situations, like a 16bit pointer's IA also being the target of a +/- label)
        var dontAllowOvewritingWithPlusMinus = false;

        // no prefix generated above?
        // well, then we're just going to generate an auto-generated label like "CODE_", "DATA_", "EMPTY_" etc
        if (prefix.Length == 0)
        {
            // final check: is there a reason we SHOULDN'T generate a generic label from our source address?
            // one case: we're referencing an instruction that explicitly wants there to be no label generated
            var srcComment = snesData.GetCommentText(snesSrcAddress);
            var srcSpecialDirective = CpuUtils.ParseCommentSpecialDirective(srcComment);
            if (srcSpecialDirective is { ForceOnlyShowRawHex: true } or { DontGenerateTemporaryLabelAtDestination: true })
                return;
            
            prefix = RomUtil.TypeToLabel(snesData.GetFlag(offsetToGenerateLabelAt));
            
            // final check for priority (feel free to add more conditions here as necessary)
            dontAllowOvewritingWithPlusMinus =
                // if we're in a pointer table, prevent the destination address from ever being assigned a +/- label
                flag is FlagType.Pointer16Bit or FlagType.Pointer24Bit or FlagType.Pointer32Bit;
        }

        var labelAddress = Util.ToHexString6(snesAddressToGenerateLabelAt);
        var labelName = $"{prefix}_{labelAddress}";

        var newTempLabel = new TempLabel
        {
            Name = labelName,
            Flags = existingLabel is TempLabel existingTempLabel ? existingTempLabel.Flags : TempLabel.TempLabelFlags.None,
        };
        
        if (dontAllowOvewritingWithPlusMinus)
            newTempLabel.Flags |= TempLabel.TempLabelFlags.DisallowPlusMinusGeneration;
        
        Data.TemporaryLabelProvider.AddOrReplaceTemporaryLabel(snesAddressToGenerateLabelAt, newTempLabel);
    }
}