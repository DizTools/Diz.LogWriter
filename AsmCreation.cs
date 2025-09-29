using System.Collections.Generic;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.util;
using Diz.LogWriter.assemblyGenerators;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

public interface IAsmCreationStep
{
    bool Enabled { get; set; }
    void Generate();
}

public abstract class AsmCreationBase : IAsmCreationStep
{
    public LogCreator LogCreator { get; init; }
    public bool Enabled { get; set; } = true;
    public ILogCreatorDataSource<IData> Data => LogCreator?.Data;

    public void Generate()
    {
        if (!Enabled)
            return;
            
        Execute();
    }
    protected abstract void Execute();
}

public class AsmDefinesGenerator : AsmCreationBase
{
    public Dictionary<string, string> Defines { get; init; }
    
    protected override void Execute()
    {
        LogCreator.SwitchOutputStream("defines.asm");

        LogCreator.WriteLine("; contains any auto-generated defines from Diz.");
        LogCreator.WriteLine("; auto-generated file DON'T edit");

        var sortedDefines = Defines
            .OrderBy (x => x.Key);
        
        foreach (var (defineName, value) in sortedDefines) {
            LogCreator.WriteLine($"{defineName} = {Util.ChopExtraZeroesFromHexStr(value)}");
        }
    }
}

public class AsmCreationMainBankIncludes : AsmCreationBase
{
    protected override void Execute()
    {
        LogCreator.SwitchOutputStream(LogCreatorStreamOutput.MainStreamFilename);
        
        WriteIncludeFileDirective("defines.asm");
        LogCreator.UniqueVisitedBanks.ForEach(WriteIncSrcLineForBank);
        WriteIncludeFileDirective("labels.asm");
    }

    private void WriteIncludeFileDirective(string filename) =>
        LogCreator.WriteSpecialLine(
            special: "incsrc", 
            context: new LineGenerator.TokenExtraContextFilename(filename)
        );

    private void WriteIncSrcLineForBank(int bank) => 
        WriteIncludeFileDirective(AsmCreationBankManager.GetBankStreamName(bank));
}
    
public class AsmCreationRomMap : AsmCreationBase
{
    protected override void Execute()
    {
        LogCreator.SwitchOutputStream(LogCreatorStreamOutput.MainStreamFilename);
        
        LogCreator.WriteSpecialLine("map");
        LogCreator.WriteEmptyLine();
    }
}