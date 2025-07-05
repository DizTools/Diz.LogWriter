using System.Collections.Generic;
using System.Linq;
using Diz.Core.Interfaces;
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
        LogCreator.SwitchOutputStream("defines");

        LogCreator.WriteLine(";contains any auto-generated defines from Diz.");
        LogCreator.WriteLine("; auto-generated file DON'T edit\n");

        var sortedDefines = Defines
            .OrderBy (x => x.Key);
        
        foreach (var (defineName, value) in sortedDefines) {
            LogCreator.WriteLine($"{defineName} = {value}");
        }
    }
}

public class AsmCreationMainBankIncludes : AsmCreationBase
{
    protected override void Execute()
    {
        var size = LogCreator.GetRomSize();
        
        // NOTE: SpecialIncSrc here is a total hack. rewrite the code to not stuff random vales into the offset
        
        LogCreator.WriteSpecialLine("incsrc", (int)AssemblyGenerateIncSrc.SpecialIncSrc.Defines);
            
        for (var i = 0; i < size; i += Data.GetBankSize())
            LogCreator.WriteSpecialLine("incsrc", i);
            
        // output the include for labels.asm file
        // int.Minvalue here is just a magic nnumber that means output a line with "labels.asm" on it
        LogCreator.WriteSpecialLine("incsrc", (int)AssemblyGenerateIncSrc.SpecialIncSrc.Labels);
    }
}
    
public class AsmCreationRomMap : AsmCreationBase
{
    protected override void Execute()
    {
        LogCreator.WriteSpecialLine("map");
        LogCreator.WriteEmptyLine();
    }
}