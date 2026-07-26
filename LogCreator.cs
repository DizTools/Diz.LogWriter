using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Diz.Core.export;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.LogWriter.assets;
using Diz.LogWriter.util;
using JetBrains.Annotations;

namespace Diz.LogWriter;

public class LogCreator : ILogCreatorForGenerator
{
    public LogWriterSettings Settings { get; set; }
    public ILogCreatorDataSource<IData> Data { get; init; }
    private LogCreatorOutput Output { get; set; }
    public LineGenerator LineGenerator { get; private set; }
    public LabelTracker LabelTracker { get; private set; }
    private LogCreatorTempLabelGenerator LogCreatorTempLabelGenerator { get; set; }
    public DataErrorChecking DataErrorChecking { get; private set; }
    
    // mapping of defines (like "!max_hp") to values ("$FFFF")
    // this introduces a side effect dependencie of the defines.asm on the main assembly being finished first,
    // ideally, we wouldn't have any side effects
    private Dictionary<string, string> visitedDefines = new();
    
    // root (parentless) file-producing regions, ordered by StartSnesAddress ascending -- these
    // get an `incsrc` in main.asm. Populated by AsmCreationInstructions from the laminar tree;
    // replaces the old bank-number list.
    public List<IRegion> RootRegions { get; } = [];

    public class ProgressEvent
    {
        public enum Status
        {
            StartInit,
            DoneInit,
            StartTemporaryLabelsGenerate,
            DoneTemporaryLabelsGenerate,
            StartMainOutputSteps,
            StartNewMainOutputStep,
            DoneMainOutputSteps,
            StartTemporaryLabelsRemoval,
            EndTemporaryLabelsRemoval,
            FinishingCleanup,
            Done,
        }

        public Status State { get; init; }
    }

    public event EventHandler<ProgressEvent> ProgressChanged;
        
    protected virtual void OnProgressChanged(ProgressEvent.Status status)
    {
        ProgressChanged?.Invoke(this, new ProgressEvent {
            State = status,
        });
    }
        
    public virtual LogCreatorOutput.OutputResult CreateLog()
    {
        try
        {
            // temporary hack. support for SingleFile mode is broken at the moment (especially when using !defines)
            // disable it with a warning. not the most user-friendly thing in the world.
            // we might consider removing support for this mode entirely in the future.
            if (Settings.Structure == LogWriterSettings.FormatStructure.SingleFile && !Settings.SuppressSingleFileModeDisabledError) {
                return new LogCreatorOutput.OutputResult {
                    LogCreator = this, ErrorCount = 1, Success = false,
                    FatalErrorMsg = "\r\nTemporary limitation: Sorry, single file output mode is broken in this version of Diz. If you need it, please open an issue on github so we can fix it.\r\nPlease change exporter settings, set Structure to 'one bank per bank' mode.",
                };
            }
            
            Init();

            try
            {
                CreateTemporaryLabels();

                // optional: performance only: build a cache of the temp labels + real labels, so it's not computed on the fly as often
                // do this after generating any temporary labels/etc
                LockLabelsCache();

                // THIS IS IT: do the real stuff
                WriteAllOutput();
            }
            finally
            {
                // optional: performance only: remove the labels cache
                UnlockLabelsCache();

                // MODIFIES UNDERLYING DATA. WE MUST ALWAYS MAKE SURE TO UNDO THIS
                RemoveTemporaryLabels();
            }

            OnProgressChanged(ProgressEvent.Status.FinishingCleanup);
            var result = GetResult();
            CloseOutput(result); // might want to close the streams as part of the exception handler

            OnProgressChanged(ProgressEvent.Status.Done);
            return result;
        }
        catch (Exception e)
        {
            // unhandled exception (IO errors/etc)
            // not much to be done here.
            return new LogCreatorOutput.OutputResult {
                LogCreator = this, ErrorCount = 1, Success = false,
                FatalErrorMsg = $"Exception during export (save, restart, and try again? or legit bug in Diz):\r\n{e.Message}",
            };
        }
    }

    private void LockLabelsCache()
    {
        // export author-filter (PHASE 2b): hand the blocklist to the shared label service BEFORE it
        // locks/builds its cache. This is the single seam that both the label listing AND operand
        // naming consult (operand naming reaches labels via SnesApi.Labels == Data.Labels, NOT via
        // this LogCreatorByteSource), so filtering has to live on the underlying label service.
        if (Data.TemporaryLabelProvider is LabelsServiceWithTemp labelService)
            labelService.SetExportLabelAuthorFilter(Settings.ExcludedLabelAuthors);

        Data.TemporaryLabelProvider.LockLabelsCache();
    }

    private void UnlockLabelsCache()
    {
        Data.TemporaryLabelProvider.UnlockLabelsCache();
    }

    private void RemoveTemporaryLabels()
    {
        OnProgressChanged(ProgressEvent.Status.StartTemporaryLabelsRemoval);
        LogCreatorTempLabelGenerator?.ClearTemporaryLabels();
        OnProgressChanged(ProgressEvent.Status.EndTemporaryLabelsRemoval);
    }

    private void CreateTemporaryLabels()
    {
        OnProgressChanged(ProgressEvent.Status.StartTemporaryLabelsGenerate);
        LogCreatorTempLabelGenerator?.GenerateTemporaryLabels();
        OnProgressChanged(ProgressEvent.Status.DoneTemporaryLabelsGenerate);
    }

    public IAsmCreationStep CurrentOutputStep { get; private set; }
        
    protected virtual void WriteAllOutput()
    {
        OnProgressChanged(ProgressEvent.Status.StartMainOutputSteps);
            
        Steps.ForEach(step =>
        {
            if (step == null)
                return;
                
            CurrentOutputStep = step;
            OnProgressChanged(ProgressEvent.Status.StartNewMainOutputStep);
            CurrentOutputStep.Generate();
            CurrentOutputStep = null;
        });
            
        OnProgressChanged(ProgressEvent.Status.DoneMainOutputSteps);
    }

    protected virtual void Init()
    {
        OnProgressChanged(ProgressEvent.Status.StartInit);
            
        Debug.Assert(Settings.RomSizeOverride == -1 || Settings.RomSizeOverride <= Data.GetRomSize());

        InitOutput();
            
        DataErrorChecking = new DataErrorChecking { Data = Data };
        DataErrorChecking.ErrorNotifier += (_, errorInfo) => OnErrorReported(errorInfo.Offset, errorInfo.Msg);
            
        LineGenerator = new LineGenerator(this, Settings.Format);
        LabelTracker = new LabelTracker(this);
        visitedDefines = new Dictionary<string, string>();
        RootRegions.Clear();
            
        if (Settings.Unlabeled != LogWriterSettings.FormatUnlabeled.ShowNone)
        {
            LogCreatorTempLabelGenerator = new LogCreatorTempLabelGenerator
            {
                LogCreator = this,
                GenerateAllUnlabeled = Settings.Unlabeled == LogWriterSettings.FormatUnlabeled.ShowAll,
                ShouldGeneratePlusMinusLabels = Settings.GeneratePlusMinusLabels,
            };
        }

        RegisterSteps();
            
        OnProgressChanged(ProgressEvent.Status.DoneInit);
        
        // we're ready to start.
    }

    /// <summary>
    /// Normalize a relative path for use in generated build/assembly files: forward slashes
    /// (checked-in files must not carry host-specific separators), and "" for "same dir".
    /// </summary>
    private static string NormalizeRel(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized == "." ? "" : normalized;
    }

    public List<IAsmCreationStep> Steps { get; private set; }

    public void RegisterSteps()
    {
        // the following steps will be executed to generate the output disassembly files
        // each generates text that ends up in the generated/ directory.
        // each step (IDEALLY) MUST NOT HAVE SIDE EFFECTS, because these steps need to be able to run in any order.
        // (even though... right now they do have some side effects)
        //
        // WARNING: For multi-file output, the step order doesn't matter much.
        // But, for single-file mode, since the steps are run in order, it matters heavily since things like 'defines'
        // need to come before the assembly code that uses them.
        
        var singleFileMode = Settings.Structure == LogWriterSettings.FormatStructure.SingleFile;

        // Asset export (regions that export as .bin/PNG instead of inline db bytes).
        // Self-activating: with no asset-typed regions everything below stays null/disabled
        // and output is byte-for-byte unchanged. The OutputToString guard matters because
        // that mode has no real output directory, and asset export writes actual files.
        var regions = Data?.Data?.Regions?.ToList() ?? [];
        var hasAssetRegions = !Settings.OutputToString &&
                              regions.Any(r => r.ExportType != RegionExportType.Assembly);

        // Three different directories; conflating them is a data-loss bug:
        //   asmOutputDir   - .asm output (e.g. <project>/generated), REWRITTEN on every export.
        //   manifestDir    - asset manifests, inside the .asm tree: also generated, also
        //                    rewritten every export.
        //   projectRootDir - hand-authored assets, build.ninja, tools. Those are human-owned
        //                    and must never sit inside the regenerated tree.
        string projectRootDir = null, asmOutputDir = null, manifestDir = null,
            asmToProjectRoot = "", mainAsmRelPath = null, manifestRelDir = null;
        if (hasAssetRegions)
        {
            asmOutputDir = Path.GetFullPath(Settings.BuildFullOutputPath());
            projectRootDir = !string.IsNullOrEmpty(Settings.BaseOutputPath)
                ? Path.GetFullPath(Settings.BaseOutputPath)
                : asmOutputDir;

            manifestDir = Path.Combine(asmOutputDir, RegionAssetExportService.AssetSubDir);

            asmToProjectRoot = NormalizeRel(Path.GetRelativePath(asmOutputDir, projectRootDir));

            // normalize first, so that when the .asm sits at the project root these come out
            // as "main.asm"/"assets" rather than "./main.asm"/"./assets".
            var asmOutputRelDir = NormalizeRel(Path.GetRelativePath(projectRootDir, asmOutputDir));
            mainAsmRelPath = NormalizeRel(Path.Combine(asmOutputRelDir, "main.asm"));
            manifestRelDir = NormalizeRel(
                Path.Combine(asmOutputRelDir, RegionAssetExportService.AssetSubDir));
        }

        var assetExportService = hasAssetRegions
            ? new RegionAssetExportService(
                Data,                       // ILogCreatorDataSource is an IReadOnlyByteSource
                Data,                       //   ...and an ISnesAddressConverter
                [
                    new BinaryRegionAssetExporter(), new GfxRegionAssetExporter(),
                    new BrrRegionAssetExporter(), new TextRegionAssetExporter(),
                ],
                Settings.BuildDirPath)
            : null;

        Steps =
        [
            new AsmCreationRomMap { LogCreator = this },

            // REQUIRED: THE MEAT! outputs all the actual disassembly instructions in each of the bank files.
            // this step also (implicitly) defines labels as they're output, and marks them as "visited".
            // limitation: for now, this introduces a side effect of outputting "visitedDefines",
            // so it must come pretty early in the process
            new AsmCreationInstructions
            {
                LogCreator = this,
                EnableRegionIncSrc = !singleFileMode,

                // both null unless the project actually has asset regions
                AssetExportService = assetExportService,
                AssetManifestRootDir = manifestDir,
                AssetAsmToProjectRootPrefix = asmToProjectRoot,
            },
            
            // outputs all the include stuff in main.asm like "incsrc bank_C0.asm", or "incsrc labels.asm" etc.
            // must come AFTER main instructions
            new AsmCreationMainBankIncludes
            {
                LogCreator = this,
                Enabled = !singleFileMode
            },

            // outputs the lines in labels.asm, which includes ONLY the leftover labels that aren't defined somewhere else.
            // i.e. labels in RAM or labels that aren't associated with an offset from the step above will appear here.
            new AsmStepWriteUnvisitedLabels
            {
                LogCreator = this,
                LabelTracker = LabelTracker,
            },

            // --------------
            // at this point, we have everything that's needed for an external assembler, like asar.exe, to start with
            // main.asm and generate a byte-identical ROM from the disassembly. we could stop right here and we'd be done.
            //
            // however, there are some other optional types of files we can generate that are useful for further processing or 
            // for metadata/romhacking/etc.
            // ----------------

            // optional: let's generate a file that contains ALL LABELS regardless of whether they were referenced.
            new AsmStepWriteAllLabels
            {
                Enabled = Settings.IncludeUnusedLabels,
                LogCreator = this,
                LabelTracker = LabelTracker,
                OutputFilename = "all-labels.txt",
            },

            // optional: same as above BUT output all labels even if they're not used (same data as above, just as CSV)
            new AsmStepExtraOutputAllLabelsCsv
            {
                // if wanted, make this a separate setting for CSV export. for now if they check "export extra label stuff"
                // we'll just include the CSV stuff by default.
                Enabled = Settings.IncludeUnusedLabels,
                OutputFilename = "all-labels.csv",

                LogCreator = this,
                LabelTracker = LabelTracker,
            },
            
            // optional: same as above BUT output all labels even if they're not used (same data as above, just as CSV)
            new AsmStepExtraOutputAllLabelsXml
            {
                // if wanted, make this a separate setting for XML export. for now if they check "export extra label stuff"
                // we'll just include the XML stuff by default. (lazy/dummmb)
                Enabled = Settings.IncludeUnusedLabels,
                OutputFilename = "all-labels.xml",

                LogCreator = this,
                LabelTracker = LabelTracker,
            },

            // optional: same as above EXCEPT this time we'll do it as a .sym file, which BSNES's debugger can read
            new AsmStepExtraOutputBsneSymFile
            {
                Enabled = Settings.IncludeUnusedLabels,
                OutputFilename = "bsnes.sym", // would be cool to output with the same base filename of the ROM.

                LogCreator = this,
                LabelTracker = LabelTracker,
            },

            // attribution statistics over the UNFILTERED persistent label set (every author,
            // INCLUDING any excluded from the export output). reads unfiltered persistent labels
            // directly, NOT the export-filtered Data.Labels. Skipped in single-file mode, which has
            // no separate sidecar file for it to go to (its output would concatenate into the main
            // assembly string).
            new AsmStepExtraOutputManifestYaml
            {
                Enabled = !singleFileMode,
                LogCreator = this,
                LabelTracker = LabelTracker,
            },
            
            // REQUIRED: output any defines if present:
            // NOTE: for now, this step must come AFTER AsmCreationInstructions because we require
            // population of the visitedDEfines
            new AsmDefinesGenerator
            {
                LogCreator = this,
                Defines = visitedDefines,   // WARNING: generated via side effect of AsmCreationInstructions
            },

            // optional: generate the build system (build.ninja + wrapper) and vendor the
            // asset codecs in, so the exported repo can rebuild the ROM without Diz.
            // must come last: it needs to know which regions were exported as assets.
            new AsmCreationBuildFiles
            {
                Enabled = hasAssetRegions && !Settings.OutputToString,
                LogCreator = this,
                ExportRootDir = projectRootDir,
                Regions = regions,
                GeneratorSettings = mainAsmRelPath == null
                    ? null
                    : new BuildFileGeneratorSettings
                    {
                        MainAsmPath = mainAsmRelPath,
                        ManifestDir = manifestRelDir,
                        AssetsDir = NormalizeRel(Settings.AssetsDirPath),
                        ExtractedDir = NormalizeRel(Settings.ExtractedDirPath),
                        BuildDir = NormalizeRel(Settings.BuildDirPath),
                    },
            },
        ];
    }
        
    private void InitOutput()
    {
        Output = Settings.OutputToString 
            ? new LogCreatorStringOutput(this) 
            : new LogCreatorStreamOutput(this);
    }

    private void CloseOutput(LogCreatorOutput.OutputResult result)
    {
        Output?.Finish(result);
        Output = null;
    }

    private LogCreatorOutput.OutputResult GetResult()
    {
        var result = new LogCreatorOutput.OutputResult
        {
            ErrorCount = Output.ErrorCount,
            Success = true,
            LogCreator = this
        };

        if (Settings.OutputToString)
            result.AssemblyOutputStr = ((LogCreatorStringOutput) Output)?.OutputString;

        return result;
    }

    protected internal void OnErrorReported(int offset, string msg) => Output.WriteErrorLine(offset, msg);
    public int GetRomSize() => Settings.RomSizeOverride != -1 ? Settings.RomSizeOverride : Data.GetRomSize();
    public void WriteLine(string line) => Output.WriteLine(line);
    protected internal void WriteEmptyLine() => WriteSpecialLine("empty");
    internal void WriteSpecialLine(string special, int offset = -1,  [CanBeNull] LineGenerator.TokenExtraContext context = null)
    {
        if (special == "empty" && !Settings.OutputExtraWhitespace)
            return;
            
        var outputLines = LineGenerator.GenerateSpecialLines(special, offset, context);
        foreach (var outputLine in outputLines) {
            WriteLine(outputLine);
        }
    }

    public void WriteOrgDirectiveForSnesAddress(int snesAddress)
    {
        WriteEmptyLine();
        WriteSpecialLine("org", context: new LineGenerator.TokenExtraContextSnes(snesAddress));
        WriteEmptyLine();
    }

    // Suppress / restore asar's bank-border check (E5032) around a file-producing region whose
    // extent legitimately crosses a SNES bank boundary. HiROM is linear across banks C0-FF, so
    // the crossing is correct, but asar flags any ORG block that crosses a bank. Emitted scoped
    // (top + end of that one file) by AsmCreationInstructions so the check still guards the rest
    // of the export.
    public void WriteBankCrossCheckDisable() => WriteSpecialLine("bankcross");
    public void WriteBankCrossCheckRestore() => WriteSpecialLine("bankcrosson");

    protected internal void SwitchOutputStream(string streamName)
    {
        Output.SwitchToStream(streamName);
            
        if (Settings.Structure == LogWriterSettings.FormatStructure.SingleFile) 
            WriteEmptyLine();
    }
    
    public void WriteIncludeFileDirective(string filename, bool padWithBlankLine = false)
    {
        if (padWithBlankLine) WriteEmptyLine();
        WriteSpecialLine(
            special: "incsrc",
            context: new LineGenerator.TokenExtraContextFilename(filename)
        );
        if (padWithBlankLine) WriteEmptyLine();
    }

    public void WriteIncSrcLineForRegion(IRegion region) =>
        WriteIncludeFileDirective(GetRegionStreamName(region));

    // filename a file-producing region's own .asm gets written to/included as. Auto-created
    // bank regions are named "bank_C0" etc (see AsmCreationInstructions.GenerateSyntheticBankRegions),
    // so this reproduces today's "bank_C0.asm" naming exactly.
    public static string GetRegionStreamName(IRegion region) => $"{region.RegionName}.asm";
    
    public void WriteHeaderForNewlyIncludedFile(int offset, string nameType, string name, int sizeInBytes = -1)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        var formattedOffsetStr = RomUtil.ConvertNumToHexStr(offset, 3);
        var formattedSnesAddrStr = snesAddress == -1 ? "[invalid]" : RomUtil.ConvertNumToHexStr(snesAddress, 3);
        WriteLine($"; --> Included {nameType}: {name}");
        WriteLine($"; --> Included from offset:       {formattedOffsetStr}");
        WriteLine($"; --> Included from SNES address: {formattedSnesAddrStr}");
        if (sizeInBytes != -1)
            WriteLine($"; --> {nameType} defined size (bytes) [actual may differ]: {sizeInBytes}");  // maybe mistrust this for weird situations. fine for comment
        
        WriteEmptyLine();
        // it would be perfectly valid to put this ORG directive here, but, let's leave off absolute references like this once
        // in case end-users want to re-locate this region somewhere else (like in a romhack/etc). since they can just
        // incsrc it wherever they want
        WriteLine($"; ORG {formattedSnesAddrStr}");
        
        WriteEmptyLine();
    }
        
    public void OnLabelVisited(int snesAddress) => LabelTracker.OnLabelVisited(snesAddress);
    public void OnInstructionVisited(int offset, CpuInstructionDataFormatted cpuInstructionDataFormatted)
    {
        // as instructions are generated, this will be called each time.
        // we want to look for some things here:
        
        // 1. is this possibly a "!define" we want to output later?
        RememberInstructionIfOverridden(offset, cpuInstructionDataFormatted);
    }

    public void ReportRootRegions(List<IRegion> rootRegions) {
        RootRegions.Clear();
        RootRegions.AddRange(rootRegions);
    }
    
    private void RememberInstructionIfOverridden(int offset, CpuInstructionDataFormatted instruction)
    {
        if (instruction.OverriddenOperand1.Length > 0 && instruction.OriginalNonOverridenOperand1 == instruction.OverriddenOperand1)
            return;
        
        // if we use a define in the override, let's register it here.
        // that way, we can output all defines in a list later.

        // example of a valid mapping looks like this:
        // OriginalNonOverridenOperand1 = "$03"
        // OverriddenOperand1 = "!num_humans"

        // validate: needs to be a hex number
        var originalValue = instruction.OriginalNonOverridenOperand1;
        if (!originalValue.StartsWith('$'))
            return;
        
        // validate: needs to look like a !define and no expressions or other stuff
        var defineName = instruction.OverriddenOperand1;
        if (!defineName.StartsWith('!') ||  // must start with this
            defineName.Contains('-') ||
            defineName.Contains('+') ||
            defineName.Contains('|') ||
            defineName.Contains('[') ||
            defineName.Contains(','))
            return;
        
        // normalize the value (seems like the right place to do this is here, but,
        // it might be OK to do this earlier in the Cpu operations that generate this)
        originalValue = Util.ChopExtraZeroesFromHexStr(originalValue);
        
        // ok we appear to have found a valid define.
        // let's add it if one doesn't exist.   if multiple exist in the project, they MUST all match or else
        // the output assembly won't be byte-identical.  we'll note the error but that's about it.
        var existingValue = visitedDefines.GetValueOrDefault(defineName);
        if (existingValue != null)
        {
            // if already defined, it must be the same value, or it's an error
            if (existingValue != originalValue)
                OnErrorReported(offset, $"Define '{defineName}' redefined with different value, must fix or generated asm will be wrong.");
            
            return;
        }

        visitedDefines.Add(defineName, originalValue);
    }

    public int GetLineByteLength(int offset) => Data.GetLineByteLength(offset, GetRomSize(), Settings.DataPerLine);
}