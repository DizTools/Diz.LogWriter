using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Diz.Core.export;
using Diz.Core.util;

namespace Diz.LogWriter;

public abstract class LogCreatorOutput(LogCreator logCreator)
{
    public class OutputResult
    {
        public bool Success;
        public int ErrorCount = -1;
        public LogCreator LogCreator;
        
        // these are only populated if outputString=true, otherwise this data will be written to the output files
        // (mostly these are used for debugging/testing purposes)
        public string AssemblyOutputStr = "";
        public string ErrorsStr = "";
        
        // only used for bad errors like exceptions during the export process.
        // errors or irregularities in the assemblyoutput are actually considered "success" but marked as warnings
        public string FatalErrorMsg = "";
    }
        
    protected readonly LogCreator LogCreator = logCreator;
    public int ErrorCount { get; protected set; }

    public virtual void Finish(OutputResult result) { }
    public virtual void SetBank(int bankNum) { }
    public virtual void SwitchToStream(string streamName) { }
    public abstract void WriteLine(string line);
    public abstract void WriteErrorLine(string line);

    public void WriteErrorLine(int offset, string msg)
    {
        var offsetMsg = offset >= 0 ? $" Offset 0x{offset:X}" : "";
        WriteErrorLine($"({ErrorCount}){offsetMsg}: {msg}");
    }
        
    protected bool ShouldOutput(string line) => !string.IsNullOrEmpty(line);
}

public class LogCreatorStringOutput : LogCreatorOutput
{
    private readonly StringBuilder outputBuilder = new();
    private readonly StringBuilder errorBuilder = new();

    public string OutputString => outputBuilder.ToString();
    public string ErrorString => errorBuilder.ToString();

    public LogCreatorStringOutput(LogCreator logCreator) : base(logCreator)
    {
        Debug.Assert(LogCreator.Settings.OutputToString && LogCreator.Settings.Structure == LogWriterSettings.FormatStructure.SingleFile);
    }

    public override void WriteLine(string line)
    {
        if (!ShouldOutput(line))
            return;
            
        outputBuilder.AppendLine(line);
    }

    public override void WriteErrorLine(string line)
    {
        ErrorCount++;
        errorBuilder.AppendLine(line);
    }

    public override void Finish(OutputResult result)
    {
        result.AssemblyOutputStr = OutputString;
        result.ErrorsStr = ErrorString;
        result.ErrorCount = ErrorCount;
    }
}

public class LogCreatorStreamOutput : LogCreatorOutput
{
    private const string MainStreamFilename = "main.asm";
    
    private readonly string outputFolder;
    private readonly Dictionary<string, StreamWriter> openOutputStreams = new();
    private StreamWriter activeOutputStream;
    private StreamWriter errorOutputStream;

    public LogCreatorStreamOutput(LogCreator logCreator) : base(logCreator)
    {
        outputFolder = LogCreator.Settings.BuildFullOutputPath();
        
        // main stream: always start on "main.asm" so there's a default
        // single file mode will never allow using a different name
        activeOutputStream = GetOrCreateStream(GetMainStreamFilename());
        errorOutputStream = GetOrCreateStream(LogCreator.Settings.ErrorFilename);
    }

    private string GetMainStreamFilename()
    {
        if (LogCreator.Settings.Structure == LogWriterSettings.FormatStructure.SingleFile)
        {
            // this can be either a FILE or a Directory
            // if it's a directory, we're going to not use the name and just override it.
            // if it's a file, we'll use the filename.
            var singleFileMainFilename = Path.GetFileName(LogCreator.Settings.FileOrFolderOutPath);
            if (!string.IsNullOrEmpty(singleFileMainFilename))
                return singleFileMainFilename;
        }
        
        return MainStreamFilename;
    }

    public override void Finish(OutputResult result)
    {
        activeOutputStream = null;
        errorOutputStream = null;
        
        foreach (var stream in openOutputStreams) {
            stream.Value.Close();
        }
        openOutputStreams.Clear();
        
        if (ErrorCount == 0)
            File.Delete(BuildStreamPath(LogCreator.Settings.ErrorFilename));
    }
    
    public override void SetBank(int bankNum)
    {
        var bankStr = Util.NumberToBaseString(bankNum, Util.NumberBase.Hexadecimal, 2);
        SwitchToStream($"bank_{bankStr}.asm");
    }
    
    public override void SwitchToStream(string streamName)
    {
        // don't switch off the main file IF we're only supposed to be outputting one file
        var streamNameToUse = streamName;
        if (LogCreator.Settings.Structure == LogWriterSettings.FormatStructure.SingleFile)
            streamNameToUse = GetMainStreamFilename();
        
        activeOutputStream = GetOrCreateStream(streamNameToUse);
    }

    // Get a stream (open file handle that can be written to) 
    // Returns an existing stream if it exists, or, registers this in the list of active streams, if not already setup
    private StreamWriter GetOrCreateStream(string streamFilename)  {
        return openOutputStreams.GetValueOrDefault(streamFilename) ?? OpenNewStream(streamFilename);
    }

    // if the stream name doesn't have a file extension, ".asm" will be added automatcally
    protected StreamWriter OpenNewStream(string streamFilename)
    {
        var finalPath = BuildStreamPath(streamFilename);
        
        //TODO: catch rare exception here of System.IO.IOEXception. probably because an external editor has the file open when we do the export
        // example:
        // System.IO.IOException: The requested operation cannot be performed on a file with a user-mapped section open. : 'bank_F9.asm'.
        // .
        // at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options)
        // at Microsoft.Win32.SafeHandles.SafeFileHandle.Open(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
        // at System.IO.Strategies.OSFileStreamStrategy..ctor(String path, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
        // at System.IO.Strategies.FileStreamHelpers.ChooseStrategyCore(String path, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, Nullable`1 unixCreateMode)
        // at System.IO.StreamWriter.ValidateArgsAndOpenPath(String path, Boolean append, Encoding encoding, Int32 bufferSize)
        // at System.IO.StreamWriter..ctor(String path)
        // at Diz.LogWriter.LogCreatorStreamOutput.OpenNewStream(String streamName) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogOutput.cs:line 164
        // at Diz.LogWriter.LogCreatorStreamOutput.SwitchToStream(String streamName, Boolean isErrorStream) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogOutput.cs:line 145
        // at Diz.LogWriter.LogCreatorStreamOutput.SetBank(Int32 bankNum) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogOutput.cs:line 135
        // at Diz.LogWriter.LogCreator.SetBank(Int32 offset, Int32 bankToSwitchTo) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogCreator.cs:line 283
        // at Diz.LogWriter.AsmCreationBankManager.SwitchBank(Int32 offset, Int32 newBank) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\AsmCreationBankManager.cs:line 24
        // at Diz.LogWriter.AsmCreationBankManager.SwitchBanksIfNeeded(Int32 offset) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\AsmCreationBankManager.cs:line 19
        // at Diz.LogWriter.AsmCreationInstructions.WriteAddress(Int32& offset) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\AsmCreationInstructions.cs:line 31
        // at Diz.LogWriter.AsmCreationInstructions.Execute() in D:\projects\DiztinGUIsh-main\Diz.LogWriter\AsmCreationInstructions.cs:line 22
        // at Diz.LogWriter.AsmCreationBase.Generate() in D:\projects\DiztinGUIsh-main\Diz.LogWriter\AsmCreation.cs:line 27
        // at Diz.LogWriter.LogCreator.<WriteAllOutput>b__43_0(IAsmCreationStep step) in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogCreator.cs:line 122
        // at System.Collections.Generic.List`1.ForEach(Action`1 action)
        // at Diz.LogWriter.LogCreator.WriteAllOutput() in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogCreator.cs:line 115
        // at Diz.LogWriter.LogCreator.CreateLog() in D:\projects\DiztinGUIsh-main\Diz.LogWriter\LogCreator.cs:line 66
        // at Diz.Controllers.controllers.ProjectController.<>c__DisplayClass31_0.<WriteAssemblyOutput>b__0() in D:\projects\DiztinGUIsh-main\Diz.Controllers\Diz.Controllers\src\controllers\ProjectController.cs:line 240
        // at Diz.Controllers.controllers.ProgressBarJob.<>c__DisplayClass0_0.<RunAndWaitForCompletion>b__0() in D:\projects\DiztinGUIsh-main\Diz.Controllers\Diz.Controllers\src\controllers\ProgressBarWorker.cs:line 119
        // at Diz.Controllers.controllers.ProgressBarJob.Thread_DoWork() in D:\projects\DiztinGUIsh-main\Diz.Controllers\Diz.Controllers\src\controllers\ProgressBarWorker.cs:line 137
        // at Diz.Controllers.controllers.ProgressBarWorker.Thread_Main() in D:\projects\DiztinGUIsh-main\Diz.Controllers\Diz.Controllers\src\controllers\ProgressBarWorker.cs:line 88
        // at System.Threading.Thread.StartCallback()
        
        var streamWriter = new StreamWriter(finalPath);
        openOutputStreams.Add(streamFilename, streamWriter);
        return streamWriter;
    }

    private string BuildStreamPath(string streamFilename) {
        return Path.Combine(outputFolder, streamFilename);
    }

    public override void WriteLine(string line)
    {
        if (!ShouldOutput(line))
            return;
        
        activeOutputStream.WriteLine(line);
    }

    public override void WriteErrorLine(string line)
    {
        ErrorCount++;
        errorOutputStream?.WriteLine(line);
    }
}