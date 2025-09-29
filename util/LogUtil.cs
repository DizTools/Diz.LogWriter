using Diz.Core;
using Diz.Core.export;
using Diz.Core.model.snes;
using JetBrains.Annotations;

namespace Diz.LogWriter.util;

[UsedImplicitly]
public class SampleAssemblyTextGenerator : ISampleAssemblyTextGenerator
{
    private readonly ISampleDataFactory sampleDataFactory;
    private readonly LogWriterSettings settings;

    public SampleAssemblyTextGenerator(ISampleDataFactory sampleDataFactory, LogWriterSettings settings)
    {
        this.sampleDataFactory = sampleDataFactory;
        this.settings = settings;
    }

    /// <summary>
    /// Generate a sample of assembly output with the given settings
    /// </summary>
    /// <returns>Output of assembly generation as text</returns>
    /// <remarks>
    /// This is handy for UI and other areas where you want to quickly demo what the effect
    /// will be of various setting changes. We'll use a built-in sample ROM as our data source.
    /// </remarks>
    public LogCreatorOutput.OutputResult GetSampleAssemblyOutput()
    {
        var data = sampleDataFactory.Create();

        var originalRomSizeBeforePadding = data.Tags.Get<SampleDataGenerationTag>();
        
        var lc = new LogCreator
        {
            Settings = settings with
            {
                Structure = LogWriterSettings.FormatStructure.SingleFile,
                
                // tmp hack to allow SingleFile to work and not generate an error. only use for demo data (like this)
                SuppressSingleFileModeDisabledError = true,
                
                FileOrFolderOutPath = "",
                OutputToString = true,
                RomSizeOverride = originalRomSizeBeforePadding.OriginalRomSizeBeforePadding
            },
            Data = new LogCreatorByteSource(data)
        };
            
        return lc.CreateLog();
    }
}

public interface ISampleAssemblyTextGenerator
{
    LogCreatorOutput.OutputResult GetSampleAssemblyOutput();
}