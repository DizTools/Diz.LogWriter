using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

// Generate labels like "CODE_856469" and "DATA_763525"
// These will be combined with the original labels to produce our final assembly
// These labels exist only for the duration of this export, and then are discarded.
//
// TODO: generate some nice looking "+"/"-" labels here.
//
// TODO: rather than build a layer on top of the existing layer system, we should probably
// just generate these on the fly. it would save a lot of complexity on the data model. 
internal class LogCreatorTempLabelGenerator
{
    public LogCreator LogCreator { get; init; }
    public ILogCreatorDataSource<IData> Data => LogCreator.Data;
    public bool GenerateAllUnlabeled { get; init; }
        
    public void ClearTemporaryLabels()
    {
        // restore original labels. SUPER IMPORTANT THIS HAPPENS WHen WE'RE DONE
        Data.TemporaryLabelProvider.ClearTemporaryLabels();
    }

    public void GenerateTemporaryLabels()
    {
        var romSize = LogCreator.GetRomSize();
        for (var offset = 0; offset < romSize; offset += LogCreator.GetLineByteLength(offset))
        {
            GenerateTempLabelIfNeededAt(offset);
        }
    }

    private void GenerateTempLabelIfNeededAt(int offset)
    {
        var snesAddress = GetAddressOfAnyUsefulLabelsAt(offset);
        if (snesAddress == -1)
            return;

        var labelName = GenerateGenericTempLabel(snesAddress);
        Data.TemporaryLabelProvider.AddTemporaryLabel(snesAddress, new Label {Name = labelName});
    }

    private int GetAddressOfAnyUsefulLabelsAt(int offset)
    {
        if (GenerateAllUnlabeled)
            return Data.ConvertPCtoSnes(offset);

        var flag = Data.Data.GetSnesApi().GetFlag(offset);
        var usefulToCreateLabelFrom = flag is 
                FlagType.Opcode or 
                FlagType.Pointer16Bit or 
                FlagType.Pointer24Bit or 
                FlagType.Pointer32Bit;

        if (!usefulToCreateLabelFrom)
            return -1;

        var snesIa = Data.GetIntermediateAddressOrPointer(offset);
        var pc = Data.ConvertSnesToPc(snesIa);
        return pc >= 0 ? snesIa : -1;
    }

    private string GenerateGenericTempLabel(int snesAddress)
    {
        var pcOffset = Data.ConvertSnesToPc(snesAddress);
        var prefix = RomUtil.TypeToLabel(Data.Data.GetSnesApi().GetFlag(pcOffset));
        var labelAddress = Util.ToHexString6(snesAddress);
        return $"{prefix}_{labelAddress}";
    }
}