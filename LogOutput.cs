using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Diz.Core.export;
using Diz.Core.util;

namespace Diz.LogWriter;

public abstract class LogCreatorOutput
{
    public class OutputResult
    {
        public bool Success;
        public int ErrorCount = -1;
        public LogCreator LogCreator;
        public string OutputStr = ""; // this is only populated if outputString=true
        
        // only used for bad errors like exceptions during the export process.
        // errors or irregularities in the assemblyoutput are actually considered "success" but marked as warnings
        public string FatalErrorMsg = "";
    }
        
    protected LogCreator LogCreator;
    public int ErrorCount { get; protected set; }

    public void Init(LogCreator logCreator)
    {
        LogCreator = logCreator;
        Init();
    }

    protected virtual void Init() { }
    public virtual void Finish(OutputResult result) { }
    public virtual void SetBank(int bankNum) { }
    public virtual void SwitchToStream(string streamName, bool isErrorStream = false) { }
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

    protected override void Init()
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
        result.OutputStr = OutputString;
    }
}

public class LogCreatorStreamOutput : LogCreatorOutput
{
    private readonly Dictionary<string, StreamWriter> outputStreams = new();
    private StreamWriter errorOutputStream;

    // references to stuff in outputStreams
    private string activeStreamName;
    private StreamWriter activeOutputStream;

    private string folder;
    private string filename; // if set to single file output mode.

    protected override void Init()
    {
        SetupOutputFolderFromSettings();
        SetupInitialOutputStreams();
    }

    private void SetupOutputFolderFromSettings()
    {
        folder = LogCreator.Settings.BuildFullOutputPath();
    }

    private void SetupInitialOutputStreams()
    {
        if (LogCreator.Settings.Structure == LogWriterSettings.FormatStructure.SingleFile)
        {
            filename = Path.GetFileName(LogCreator.Settings.FileOrFolderOutPath);
            SwitchToStream(filename);
        }
        else
        {
            SwitchToStream("main");
        }

        SwitchToStream(LogCreator.Settings.ErrorFilename, isErrorStream: true);
    }

    public override void Finish(OutputResult result)
    {
        foreach (var stream in outputStreams)
        {
            stream.Value.Close();
        }
        outputStreams.Clear();

        activeOutputStream = null;
        errorOutputStream = null;
        activeStreamName = "";

        if (result.ErrorCount == 0)
            File.Delete(BuildStreamPath(LogCreator.Settings.ErrorFilename));
    }

    public override void SetBank(int bankNum)
    {
        var bankStr = Util.NumberToBaseString(bankNum, Util.NumberBase.Hexadecimal, 2);
        SwitchToStream($"bank_{bankStr}");
    }

    public override void SwitchToStream(string streamName, bool isErrorStream = false)
    {
        // don't switch off the main file IF we're only supposed to be outputting one file
        if (LogCreator.Settings.Structure == LogWriterSettings.FormatStructure.SingleFile &&
            !string.IsNullOrEmpty(activeStreamName))
            return;

        var whichStream = outputStreams.TryGetValue(streamName, out var outputStream) 
            ? outputStream 
            : OpenNewStream(streamName);

        if (!isErrorStream)
            SetActiveStream(streamName, whichStream);
        else
            errorOutputStream = whichStream;
    }

    private void SetActiveStream(string streamName, StreamWriter streamWriter)
    {
        activeStreamName = streamName;
        activeOutputStream = streamWriter;
    }

    protected StreamWriter OpenNewStream(string streamName)
    {
        var finalPath = BuildStreamPath(streamName);
        
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
        outputStreams.Add(streamName, streamWriter);
        return streamWriter;
    }

    private string BuildStreamPath(string streamName)
    {
        var fullOutputPath = Path.Combine(folder, streamName);

        if (!Path.HasExtension(fullOutputPath))
            fullOutputPath += ".asm";
        return fullOutputPath;
    }

    public override void WriteLine(string line)
    {
        if (!ShouldOutput(line))
            return;
            
        Debug.Assert(activeOutputStream != null && !string.IsNullOrEmpty(activeStreamName));
        activeOutputStream.WriteLine(line);
    }

    public override void WriteErrorLine(string line)
    {
        ErrorCount++;
        errorOutputStream?.WriteLine(line);
    }
}