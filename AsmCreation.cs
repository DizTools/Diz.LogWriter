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
    public List<int> BanksVisited { get; init; } = [];

    protected override void Execute()
    {
        LogCreator.SwitchOutputStream(LogCreatorStreamOutput.MainStreamFilename);
        
        WriteIncludeFileDirective("defines.asm");
        BanksVisited.ForEach(WriteIncSrcLineForBank);
        WriteIncludeFileDirective("labels.asm");
    }

    private void WriteIncludeFileDirective(string filename) => 
        LogCreator.WriteLine(BuildIncSrcDirective(filename));
    
    private void WriteIncSrcLineForBank(int bank) => 
        WriteIncludeFileDirective(BuildBankIncludeFilename(bank));

    private static string BuildBankIncludeFilename(int bank)
    {
        var bankName = Util.NumberToBaseString(bank, Util.NumberBase.Hexadecimal, 2);
        return $"bank_{bankName}.asm";
    }

    private static string BuildIncSrcDirective(string val) => 
        $"incsrc \"{val}\"";
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