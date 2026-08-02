using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.model.snes;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.assemblyGenerators;
using Diz.LogWriter.assets;
using JetBrains.Annotations;

namespace Diz.LogWriter;

// this class outputs the meat of the assembly generation process, it prints the actual assembler instructions
//
// Output splitting (which file a byte's assembly lands in) is driven entirely by the tree of
// file-producing regions (ExportSeparateFile == true). There is no separate "bank" concept
// anymore: banks are just auto-synthesized, whole-bank-sized file-producing regions
// (GenerateSyntheticBankRegions), Priority = 0, so they reproduce the one-file-per-bank output
// but are otherwise ordinary regions a user may split/rename/delete freely.
public class AsmCreationInstructions : AsmCreationBase
{
    public bool EnableRegionIncSrc { get; init; } = true;

    // Optional: when both are set, regions marked with an ExportType other than 'Assembly'
    // are described by a manifest instead of inline bytes, and replaced in the .asm with an
    // `incbin` of the build's compiled output. Left null, nothing changes and every region
    // exports inline `db` bytes exactly as before.
    [CanBeNull] public IRegionAssetExportService AssetExportService { get; init; }

    // Where asset manifests are written: the "assets" folder inside the assembly output dir.
    // Manifests are generated output and belong in the tree that export rewrites.
    [CanBeNull] public string AssetManifestRootDir { get; init; }

    // relative path from the .asm's directory back to the project root (e.g. ".."), so the
    // emitted incbin resolves from wherever the .asm actually lives.
    public string AssetAsmToProjectRootPrefix { get; init; } = "";

    private bool AssetExportEnabled =>
        AssetExportService != null && !string.IsNullOrEmpty(AssetManifestRootDir);

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
    // the laminar family of file-producing regions, computed once up front:
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

    // file-producing regions we emitted `check bankcross off` into (because their extent
    // crosses a SNES bank boundary -- legal, but asar's E5032 flags any ORG block that crosses a
    // bank). Tracked so the matching `check bankcross on` restore lands at the END of that same
    // file, keeping the suppression scoped.
    private readonly HashSet<IRegion> bankCrossDisabledRegions = [];

    // SNES address of the last byte actually written, so we can detect a discontinuity
    // (snes(p) != snes(p-1)+1) regardless of which region we're in. Null before the first byte.
    private int? previousSnesAddress;

    // tracks literal PC-bank-number transitions, purely for CheckForBankCrossError -- this is
    // NOT the same thing as "entered a root region" once a hand-drawn region is allowed to
    // cross a bank boundary: such a region starts mid-bank on purpose, and that must
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

                    // good to go. NOTE: EndSnesAddress is inclusive (the last byte IN the
                    // region) -- snesAddress here is the address the `!!ie` comment sits ON, i.e.
                    // the last byte the user wants included, so no adjustment is needed.
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
    /// reproducing the one-file-per-bank split (banks are not special; ExportSeparateFile is
    /// the only discriminator). Purely in-memory/transient here -- nothing is written back to
    /// Data.Regions here.
    ///
    /// Import and the save-format-107 migration persist these same regions. The actual
    /// bank-enumeration + skip-if-already-covered logic lives in the shared
    /// <see cref="BankRegionSynthesis"/> helper (Diz.Core) so this call site and the
    /// persistence call sites can never disagree about which banks need a region -- a
    /// project that has been migrated/imported already has exact-match persisted bank
    /// regions in allRegions, so this pass sees them via existingRegions and skips them:
    /// nothing is added twice, and re-running export is idempotent.
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
    /// O(n^2) but n is tiny (typically well under a hundred regions) -- not worth a smarter
    /// structure yet.
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
        {
            var leaving = regionStack.Pop();

            // If we turned asar's bank-border check off for this file-producing region (it
            // crosses a bank), restore it as the LAST line of that region's own file, before
            // the output stream switches away below. Deepest is popped first and is the active
            // stream; switching explicitly makes this correct even for a crossing region that
            // isn't the innermost frame.
            if (EnableRegionIncSrc && bankCrossDisabledRegions.Remove(leaving))
            {
                LogCreator.SwitchOutputStream(LogCreator.GetRegionStreamName(leaving));
                LogCreator.WriteBankCrossCheckRestore();
            }
        }

        if (popCount > 0 && EnableRegionIncSrc)
        {
            // we left one or more regions -- resume writing into whichever file is now on top
            // of the stack (or main.asm if we're back at the root) BEFORE any new region opens
            // here. When a sibling region starts on the byte right after the previous one ends
            // (pop+push in the same call), this is what makes the new sibling's incsrc land in
            // the shared parent's file (its parent's file), not in the
            // just-closed sibling's file.
            LogCreator.SwitchOutputStream(regionStack.Count > 0
                ? LogCreator.GetRegionStreamName(regionStack.Peek())
                : LogCreatorStreamOutput.MainStreamFilename);
        }

        for (var i = commonDepth; i < desired.Count; i++)
        {
            if (EnterRegion(desired[i], offset, snesAddress))
                pushedRoot = true;
        }
    }

    /// <summary>
    /// Push one region onto the stack and emit whatever announces it. Returns true if this was
    /// a root (parentless) region.
    ///
    /// Root entries (today: always a synthesized bank region) keep the minimal format --
    /// blank line + a real ORG, no header -- because that's what the per-bank bank_XX.asm files
    /// start with, and byte-identical re-export depends on it staying that way.
    ///
    /// Nested entries keep the "Included region" header, INCLUDING its ORG staying a comment.
    /// Turning that comment into a real ORG directive for an existing nested region that has no
    /// internal discontinuity would add a real ORG line the file doesn't have today, changing
    /// the .asm text -- so this deliberately keeps the narrower, byte-identical behavior. The
    /// safety-critical case (a real ORG wherever the SNES address is actually discontinuous,
    /// mid-region or not) is unconditional and handled in WriteOutputLinesForRomOffset
    /// regardless of this method.
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
            EmitBankCrossDisableIfCrossing(region);
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

        EmitBankCrossDisableIfCrossing(region);

        return false;
    }

    // If this file-producing region's extent crosses a SNES bank boundary, emit
    // `check bankcross off` into its (now-active) file. HiROM banks C0-FF are one linear
    // address space, so the crossing is intentional/correct, but asar raises E5032 for any ORG
    // block that crosses a bank boundary -- so we suppress the check for exactly this file and
    // restore asar's default at its end (SyncRegionStack, on pop). Non-crossing regions get
    // nothing, so the check keeps guarding the other ~64 files. Only meaningful in the
    // separate-file path; single-file mode (EnableRegionIncSrc == false) is the disabled legacy
    // mode and is not handled here.
    private void EmitBankCrossDisableIfCrossing(IRegion region)
    {
        if (!EnableRegionIncSrc)
            return;

        if (RomUtil.GetBankFromSnesAddress(region.StartSnesAddress) ==
            RomUtil.GetBankFromSnesAddress(region.EndSnesAddress))
            return;

        LogCreator.WriteBankCrossCheckDisable();
        bankCrossDisabledRegions.Add(region);
    }

    // True if the narrowest file-producing region covering this byte legitimately spans a SNES
    // bank boundary. Used to suppress the "instruction crossed a bank boundary" diagnostic for
    // those sanctioned crossings. Spatial query, so it is correct regardless of where
    // the region stack currently is.
    private bool CurrentFileProducingRegionCrossesBank(int snesAddress)
    {
        var region = GetDeepestFileProducingRegionAt(snesAddress);
        return region != null &&
               RomUtil.GetBankFromSnesAddress(region.StartSnesAddress) !=
               RomUtil.GetBankFromSnesAddress(region.EndSnesAddress);
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
        // bytes straddle a real PC-bank boundary. A file-producing region is now allowed to
        // cross a bank boundary on purpose, and -- as this check's comment has always
        // said -- that must NOT trip this diagnostic. It used to anyway: a bank-crossing asset
        // incbin advances the offset straight past the seam, so the next byte lands mid-bank and
        // looks like a straddling instruction. Suppress the diagnostic while the deepest
        // file-producing region covering this byte is one whose extent legitimately crosses a
        // bank (the same regions that get `check bankcross off`). Non-crossing regions still trip
        // it, so a genuinely mis-sized instruction is still caught.
        var bank = RomUtil.GetBankFromSnesAddress(snesAddress);
        if (bank != previousBankForCrossCheck)
        {
            if (!CurrentFileProducingRegionCrossesBank(snesAddress))
                CheckForBankCrossError(offset);
            previousBankForCrossCheck = bank;
        }

        SyncRegionStack(offset, snesAddress, out var pushedRoot);

        // ORG on SNES-address discontinuity: if this byte doesn't follow directly from
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

        // An asset region's bytes are read as one contiguous PC run and its incbin lands in
        // whichever file-producing region currently owns this offset (the top of the region
        // stack) -- NOT a per-bank file. Banks stopped being an output concept, so a
        // bank-crossing asset region is fine (some games have BRR samples that do exactly this):
        // under HiROM the SNES->PC map is linear and continuous across bank seams, so the bytes stay
        // one contiguous run. The real correctness condition is that contiguity -- the SNES span
        // must equal the PC span -- which is mapping-mode-agnostic. If they differ (a LoROM
        // 32KiB seam), reading startPc..endPc would grab the wrong bytes, so reject THAT.
        if (endPc - startPc != region.EndSnesAddress - region.StartSnesAddress)
            throw new InvalidDataException(
                $"Asset region '{region.RegionName}' spans a non-contiguous SNES->PC range " +
                $"(SNES ${region.StartSnesAddress:X6}-${region.EndSnesAddress:X6} maps to " +
                $"PC 0x{startPc:X}-0x{endPc:X}); its bytes can't be read as one run. This happens " +
                "at a LoROM bank seam -- split the region so it doesn't cross one.");

        var exported = AssetExportService.ExportRegion(
            region, AssetManifestRootDir, AssetAsmToProjectRootPrefix);
        if (exported == null)
            return false;

        // Names to bracket the incbin with. These are exactly the names labels.asm would
        // otherwise emit as `NAME = $XXXXXX` equates for this address, because the
        // OnLabelVisited() below suppresses ALL of them at once. The emission and the visit are
        // strictly coupled and must stay that way: visit without emitting and the symbol exists
        // nowhere, so the assembler fails at its first reference; emit without visiting and
        // labels.asm defines it a second time, so the assembler fails on the redefinition.
        var labelNames = GetAssetRegionLabelNamesAt(region.StartSnesAddress);

        LogCreator.WriteEmptyLine();
        LogCreator.WriteAssetIncludeHeaderLine(
            offset, region.RegionName, GetAssetTypeNameForHeader(region), length);

        foreach (var labelName in labelNames)
            LogCreator.WriteLine($"{labelName}:");

        LogCreator.WriteLine(exported.AsmDirective);

        if (labelNames.Count > 0)
        {
            // End marker: plain output text, deliberately NEVER registered in the label store.
            // The store holds one label per SNES address, and asset regions very often end
            // exactly where the next one begins -- so an end label could not coexist with the
            // next region's start label. Written straight into the text, the assembler resolves
            // it to the program counter, which right after the incbin is start + len: exactly
            // what `X__END - X` table math needs. The name pairs with whichever name came first
            // above, so it reads the same whether that name was hand-authored or generated.
            LogCreator.WriteLine($"{labelNames[0]}__END:");

            // paired with the inline emission above -- see the comment on labelNames.
            LogCreator.OnLabelVisited(region.StartSnesAddress);
        }

        LogCreator.WriteEmptyLine();

        exportedAssetRegions.Add(region.RegionName);
        offset += length;
        return true;
    }

    /// <summary>
    /// The "type" value for an asset region's header line: its codec contract when it declares
    /// one, otherwise its export type's own name lowercased (e.g. "binary"), so the key is always
    /// present and always says something true about the region.
    /// </summary>
    private static string GetAssetTypeNameForHeader(IRegion region) =>
        !string.IsNullOrWhiteSpace(region.AssetType)
            ? region.AssetType
            : region.ExportType.ToString().ToLowerInvariant();

    /// <summary>
    /// Every label name that exists at an asset region's start address, read through the SAME
    /// accessor the leftover-labels file uses -- so context-mapping name overrides are included
    /// and the two can never disagree about which names live at this address.
    /// Empty when asset labels are switched off, which restores the un-bracketed output.
    /// </summary>
    private List<string> GetAssetRegionLabelNamesAt(int snesAddress)
    {
        if (!LogCreator.Settings.GenerateAssetLabels)
            return [];

        var printableLabels =
            AssemblyGenerateLabelAssign.GetPrintableLabelsDataAtSnesAddress(snesAddress, Data.Labels);

        return printableLabels == null
            ? []
            : printableLabels.Select(x => x.Name).ToList();
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
