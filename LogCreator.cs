using System;
using System.Collections.Generic;
using System.Diagnostics;
using Diz.Core.export;
using Diz.Core.Interfaces;
using Diz.Core.util;
using Diz.LogWriter.util;

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
    private Dictionary<string, string> visitedDefines = new();

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
        LogCreatorOutput.OutputResult result = null;
        try
        {
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
            result = GetResult();
            CloseOutput(result);

            OnProgressChanged(ProgressEvent.Status.Done);

        }
        catch (Exception e)
        {
            // unhandled exception (IO errors/etc)
            // not much to be done here.
            result = new LogCreatorOutput.OutputResult
            {
                LogCreator = this,
                ErrorCount = 1,
                Success = false,
                ErrorMsg = $"Exception during export (save, restart, and try again? or legit bug in Diz):\r\n{e.Message}",
            };
        }

        return result;
    }

    private void LockLabelsCache()
    {
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
    }

    public List<IAsmCreationStep> Steps { get; private set; }

    public void RegisterSteps()
    {
        // the following steps will be executed in order to generate the output disassembly files
        // each generates text that ends up in the generated/ directory.
            
        Steps =
        [
            new AsmCreationRomMap { LogCreator = this },

            // outputs all the include stuff in main.asm like "incsrc bank_C0.asm", or "incsrc labels.asm" etc.
            new AsmCreationMainBankIncludes
            {
                Enabled = Settings.Structure == LogWriterSettings.FormatStructure.OneBankPerFile,
                LogCreator = this
            },

            // THE MEAT! outputs all the actual disassembly instructions in each of the bank files.
            // this step also (implicitly) defines labels as they're output, and marks them as "visited" 
            new AsmCreationInstructions { LogCreator = this },

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

            // TODO: the 3 label steps below will run if Settings.IncludeUnusedLabels is checked.

            // optional: let's generate a file that contains ALL LABELS regardless of whether or not they were referenced.

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

            // optional: same as above EXCEPT this time we'll do it as a .sym file, which BSNES's debugger can read
            new AsmStepExtraOutputBsneSymFile
            {
                Enabled = Settings.IncludeUnusedLabels,
                OutputFilename = "bsnes.sym", // would be cool to output with the same base filename of the ROM.

                LogCreator = this,
                LabelTracker = LabelTracker,
            },
            
            // output any defines if present:
            new AsmDefinesGenerator
            {
                LogCreator = this,
                Defines = visitedDefines,
            },
        ];
    }
        
    private void InitOutput()
    {
        Output = Settings.OutputToString ? new LogCreatorStringOutput() : Output = new LogCreatorStreamOutput();
        Output.Init(this);
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
            result.OutputStr = ((LogCreatorStringOutput) Output)?.OutputString;

        return result;
    }

    protected internal void OnErrorReported(int offset, string msg) => Output.WriteErrorLine(offset, msg);
    public int GetRomSize() => Settings.RomSizeOverride != -1 ? Settings.RomSizeOverride : Data.GetRomSize();
    public void WriteLine(string line) => Output.WriteLine(line);
    protected internal void WriteEmptyLine() => WriteSpecialLine("empty");
    internal void WriteSpecialLine(string special, int offset = -1)
    {
        if (special == "empty" && !Settings.OutputExtraWhitespace)
            return;
            
        var output = LineGenerator.GenerateSpecialLine(special, offset); 
        WriteLine(output);
    }

    protected internal void SetBank(int offset, int bankToSwitchTo)
    {
        Output.SetBank(bankToSwitchTo);
        OnBankSwitched(offset);
    }

    private void OnBankSwitched(int offset)
    {
        WriteEmptyLine();
        WriteSpecialLine("org", offset);
        WriteEmptyLine();
    }

    protected internal void SwitchOutputStream(string streamName)
    {
        Output.SwitchToStream(streamName);
            
        if (Settings.Structure == LogWriterSettings.FormatStructure.SingleFile) 
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