using System;
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
//
// Output splitting (which file a byte's assembly lands in) is driven entirely by the tree of
// file-producing regions (ExportSeparateFile == true) -- see docs/diz/regions-as-partition-plan.md
// §A.4. There is no separate "bank" concept anymore: banks are just auto-synthesized,
// whole-bank-sized file-producing regions (GenerateSyntheticBankRegions), Priority = 0, so they
// reproduce today's one-file-per-bank output but are otherwise ordinary regions a user may
// split/rename/delete freely (§A.2.3).
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

    private int GetBankFromOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            throw new InvalidDataException($"Rom offset required to map to SNES address: {offset}");

        return RomUtil.GetBankFromSnesAddress(snesAddress);
    }

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }

    // includes both real regions from Data, comment-derived (!!ir/!!ie) temporary regions, and
    // (after GenerateSyntheticBankRegions) the auto-synthesized whole-bank regions
    private List<IRegion> allRegions = [];

    // -----------------------------------------------------------------------------------
    // the laminar family of file-producing regions (§A.3/§A.4), computed once up front:
    //   fileProducingRegions           -- flat list (real + synthetic bank regions)
    //   parentOfFileProducingRegion    -- region -> its narrowest enclosing file-producing
    //                                      region, or absent/null for a root (no parent)
    //   rootFileProducingRegionsOrdered-- regions with no parent, sorted by StartSnesAddress
    //                                      ascending -- these get `incsrc`'d into main.asm
    // -----------------------------------------------------------------------------------
    private List<IRegion> fileProducingRegions = [];
    private Dictionary<IRegion, IRegion> parentOfFileProducingRegion = new();
    private List<IRegion> rootFileProducingRegionsOrdered = [];

    // the walk needs a stack, not a scalar: leaving a leaf and its parent at the same offset
    // pops multiple frames at once. Bottom of stack = outermost active region, top = current
    // output destination.
    private readonly Stack<IRegion> regionStack = new();

    // every region name we've ever pushed, across the whole scan -- catches a region (of any
    // kind, bank or user-drawn) reused under the same name a second time.
    private readonly HashSet<string> enteredRegionNames = [];

    // SNES address of the last byte actually written, so we can detect a discontinuity
    // (snes(p) != snes(p-1)+1) regardless of which region we're in. Null before the first byte.
    private int? previousSnesAddress;

    // tracks literal PC-bank-number transitions, purely for CheckForBankCrossError -- this is
    // NOT the same thing as "entered a root region" once a hand-drawn region is allowed to
    // cross a bank boundary (§A.2.3): such a region starts mid-bank on purpose, and that must
    // not be misreported as a misaligned instruction. -1 = "no bank visited yet".
    private int previousBankForCrossCheck = -1;

    protected override void Execute()
    {
        GenerateExtraIncSrcRegionsFromComments();
        GenerateSyntheticBankRegions();
        BuildFileProducingTree();

        LogCreator.ReportRootRegions(rootFileProducingRegionsOrdered);

        var romSize = LogCreator.GetRomSize();

        // perf: this is the meat of the export, takes a while
        for (var offset = 0; offset < romSize;) {
            WriteOutputLinesForRomOffset(ref offset);
        }
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

                    // good to go. NOTE: EndSnesAddress is inclusive (§A.2.2) -- snesAddress here
                    // is the address the `!!ie` comment sits ON, i.e. the last byte the user
                    // wants included, so no adjustment is needed.
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

    /// <summary>
    /// Synthesize one whole-bank, file-producing region per SNES bank spanned by the ROM,
    /// reproducing today's one-file-per-bank split (§A.2.3: "banks are not special;
    /// ExportSeparateFile is the only discriminator"). Purely in-memory/transient here --
    /// nothing is written back to Data.Regions here.
    ///
    /// §A.5/step 5 persists these same regions on import and via the save-format-107
    /// migration. The actual bank-enumeration + skip-if-already-covered logic lives in the
    /// shared <see cref="BankRegionSynthesis"/> helper (Diz.Core) so this call site and the
    /// persistence call sites can never disagree about which banks need a region -- a
    /// project that has been migrated/imported already has exact-match persisted bank
    /// regions in allRegions, so this pass sees them via existingRegions and skips them:
    /// nothing is added twice, and re-running export is idempotent. See the "As built -- two
    /// deviations to reconcile" note at the end of §A.4.
    /// </summary>
    private void GenerateSyntheticBankRegions()
    {
        var bankSize = Data.GetBankSize();
        var romSize = LogCreator.GetRomSize();

        var synthesized = BankRegionSynthesis.SynthesizeMissingBankRegions(
            allRegions, romSize, bankSize, Data.ConvertPCtoSnes);

        allRegions.AddRange(synthesized);
    }

    /// <summary>
    /// Build the laminar tree over the file-producing subset of allRegions: for each region,
    /// its parent is the narrowest OTHER file-producing region that fully contains it. Regions
    /// with no parent are roots (get `incsrc`'d into main.asm, ordered by StartSnesAddress).
    /// O(n^2) but n is tiny (~70 regions for CT) -- not worth a smarter structure yet.
    /// </summary>
    private void BuildFileProducingTree()
    {
        fileProducingRegions = allRegions.Where(r => r.IsFileProducingRegion()).ToList();

        var byExtentAscending = fileProducingRegions
            .OrderBy(r => r.EndSnesAddress - r.StartSnesAddress)
            .ToList();

        parentOfFileProducingRegion = new Dictionary<IRegion, IRegion>();
        foreach (var region in byExtentAscending)
        {
            IRegion parent = null;
            foreach (var candidate in byExtentAscending)
            {
                if (ReferenceEquals(candidate, region))
                    continue;

                var strictlyLarger = candidate.EndSnesAddress - candidate.StartSnesAddress >
                                      region.EndSnesAddress - region.StartSnesAddress;
                var contains = candidate.StartSnesAddress <= region.StartSnesAddress &&
                                candidate.EndSnesAddress >= region.EndSnesAddress;

                if (!strictlyLarger || !contains)
                    continue;

                parent = candidate;
                break;
            }
            parentOfFileProducingRegion[region] = parent;
        }

        rootFileProducingRegionsOrdered = fileProducingRegions
            .Where(r => parentOfFileProducingRegion[r] == null)
            .OrderBy(r => r.StartSnesAddress)
            .ToList();
    }

    [CanBeNull]
    private IRegion GetDeepestFileProducingRegionAt(int snesAddress)
    {
        IRegion best = null;
        foreach (var r in fileProducingRegions)
        {
            if (snesAddress < r.StartSnesAddress || snesAddress > r.EndSnesAddress)
                continue;

            if (best == null || r.EndSnesAddress - r.StartSnesAddress < best.EndSnesAddress - best.StartSnesAddress)
                best = r;
        }
        return best;
    }

    // the desired region stack at this address, root-first (index 0 = outermost/no parent,
    // last = the deepest/most specific region covering this byte). Empty if nothing covers it.
    private List<IRegion> GetDesiredRegionStack(int snesAddress)
    {
        var chain = new List<IRegion>();
        var current = GetDeepestFileProducingRegionAt(snesAddress);
        while (current != null)
        {
            chain.Add(current);
            parentOfFileProducingRegion.TryGetValue(current, out current);
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Reconcile regionStack against what should be active at this offset: pop everything past
    /// the point where the current stack and the desired stack diverge (may be more than one
    /// frame at once), then push whatever's new. Sets pushedRoot=true if any of the pushes this
    /// call was a root (parentless / bank-equivalent) region -- the caller uses that to avoid
    /// double-emitting an ORG when a root entry coincides with an address discontinuity.
    /// </summary>
    private void SyncRegionStack(int offset, int snesAddress, out bool pushedRoot)
    {
        pushedRoot = false;

        var desired = GetDesiredRegionStack(snesAddress);

        var current = regionStack.ToArray(); // top-first (Stack<T>.ToArray() pop order)
        Array.Reverse(current);              // now root-first, matching `desired`

        var commonDepth = 0;
        while (commonDepth < current.Length && commonDepth < desired.Count &&
               ReferenceEquals(current[commonDepth], desired[commonDepth]))
        {
            commonDepth++;
        }

        var popCount = current.Length - commonDepth;
        for (var i = 0; i < popCount; i++)
            regionStack.Pop();

        var pushedAny = false;
        for (var i = commonDepth; i < desired.Count; i++)
        {
            pushedAny = true;
            if (EnterRegion(desired[i], offset, snesAddress))
                pushedRoot = true;
        }

        if (popCount > 0 && !pushedAny && EnableRegionIncSrc)
        {
            // we left one or more regions and nothing new opened here -- resume writing into
            // whichever file is now on top of the stack (or main.asm if we're back at the root)
            LogCreator.SwitchOutputStream(regionStack.Count > 0
                ? LogCreator.GetRegionStreamName(regionStack.Peek())
                : LogCreatorStreamOutput.MainStreamFilename);
        }
    }

    /// <summary>
    /// Push one region onto the stack and emit whatever announces it. Returns true if this was
    /// a root (parentless) region.
    ///
    /// Root entries (today: always a synthesized bank region) keep today's minimal format --
    /// blank line + a real ORG, no header -- because that's what today's bank_XX.asm files
    /// start with and the CT byte-identity gate depends on it staying that way.
    ///
    /// Nested entries keep today's "Included region" header, INCLUDING its ORG staying a
    /// comment. Per plan doc §A.4 this was slated to become a real directive for all
    /// file-producing regions; doing so for CT's existing nested regions (player_attack_
    /// animations etc, none of which have any internal discontinuity) would add a real ORG
    /// line those files don't have today, which breaks the "CT .asm text must not change"
    /// gate -- so this deliberately keeps the narrower, byte-identical behavior. The actual
    /// safety-critical half of §A.4 (a real ORG wherever the SNES address is actually
    /// discontinuous, mid-region or not) is unconditional and handled in
    /// WriteOutputLinesForRomOffset regardless of this method.
    /// </summary>
    private bool EnterRegion(IRegion region, int offset, int snesAddress)
    {
        if (!enteredRegionNames.Add(region.RegionName))
            throw new InvalidDataException(
                $"Multiple regions with ExportSeparateFile=true use the same name '{region.RegionName}', invalid");

        regionStack.Push(region);

        var isRoot = !parentOfFileProducingRegion.TryGetValue(region, out var parent) || parent == null;

        if (!EnableRegionIncSrc)
        {
            // single-file mode: no separate output files, but root/bank-equivalent entries
            // still get their un-gated ORG, matching today's behavior (bank switching was never
            // gated by EnableRegionIncSrc -- only the nested "incsrc" mechanism was).
            if (isRoot)
                LogCreator.WriteOrgDirectiveForSnesAddress(snesAddress);

            return isRoot;
        }

        var streamName = LogCreator.GetRegionStreamName(region);

        if (isRoot)
        {
            LogCreator.SwitchOutputStream(streamName);
            LogCreator.WriteOrgDirectiveForSnesAddress(snesAddress);
            return true;
        }

        // we're in the parent's file (currently active stream); write an "incsrc" to the new
        // file we're about to switch into
        LogCreator.WriteIncludeFileDirective(streamName, padWithBlankLine: true);
        LogCreator.SwitchOutputStream(streamName);

        var regionBytesSize = -1;
        var startOffset = Data.ConvertSnesToPc(region.StartSnesAddress);
        var endOffset = Data.ConvertSnesToPc(region.EndSnesAddress);
        if (startOffset != -1 && endOffset != -1 && GetBankFromOffset(startOffset) == GetBankFromOffset(endOffset))
        {
            // we'll only report the size if they're in the same bank, otherwise the math gets weird. maybe. unsure. whatever
            // EndSnesAddress is inclusive (last byte IN the region), so add 1 to get the byte count
            regionBytesSize = endOffset - startOffset + 1;
        }
        LogCreator.WriteHeaderForNewlyIncludedFile(offset, "region", region.RegionName, regionBytesSize);

        return false;
    }

    // write one line of the assembly output
    // address is a "PC address" i.e. offset into the ROM.
    // not a SNES address.
    private void WriteOutputLinesForRomOffset(ref int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            throw new InvalidDataException($"Rom offset required to map to SNES address: {offset}");

        // preserved from the old SwitchBanksIfNeeded/SwitchBank: report when an instruction's
        // bytes straddle a real PC-bank boundary. Deliberately independent of the region stack
        // above -- a file-producing region is now allowed to cross a bank boundary on purpose
        // (§A.2.3), and that must not trip this diagnostic.
        var bank = RomUtil.GetBankFromSnesAddress(snesAddress);
        if (bank != previousBankForCrossCheck)
        {
            CheckForBankCrossError(offset);
            previousBankForCrossCheck = bank;
        }

        SyncRegionStack(offset, snesAddress, out var pushedRoot);

        // ORG on SNES-address discontinuity (§A.4): if this byte doesn't follow directly from
        // the last one we wrote, asar needs to be told where we really are, or (LoRom bank
        // seams especially) it silently mislocates everything after. Root entries above already
        // wrote their own ORG, so skip here to avoid a redundant pair when a root entry and a
        // discontinuity coincide (always true for LoRom bank boundaries).
        var isContinuous = previousSnesAddress.HasValue && snesAddress == previousSnesAddress.Value + 1;
        if (!pushedRoot && !isContinuous)
            LogCreator.WriteOrgDirectiveForSnesAddress(snesAddress);

        // if an asset region starts here, emit one `incbin` and skip its bytes entirely,
        // rather than emitting them inline. must happen before the normal line generation.
        if (TryWriteAssetRegion(ref offset))
        {
            previousSnesAddress = Data.ConvertPCtoSnes(offset - 1);
            return;
        }

        WriteBlankLineIfStartingNewParagraph(offset);
        GenerateAndWriteCodeOutputLinesForRomOffset(offset);    // the important thing
        LogCreator.DataErrorChecking.CheckForErrorsAt(offset);
        WriteBlankLineIfEndPoint(offset);

        // TODO: WARNING: TECHNICALLY, we should be checking for bank and region crosses
        //  looking at our previous offset through our new offset.  we could miss transitions or
        //  put region includes in the wrong spot.  Happens if there's regions that begin in the middle of
        //  boundaries like inside the middle byte of data labelled as 24-bit.
        offset += LogCreator.GetLineByteLength(offset);

        // track the SNES address of the last byte we actually consumed (not just the one this
        // call started at) so the next call's discontinuity check is correct even after this
        // multi-byte advance.
        previousSnesAddress = Data.ConvertPCtoSnes(offset - 1);
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
