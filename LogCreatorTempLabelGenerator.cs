﻿using Diz.Core.Interfaces;
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
        for (var pointer = 0; pointer < LogCreator.GetRomSize(); pointer += LogCreator.GetLineByteLength(pointer))
        {
            GenerateTempLabelIfNeededAt(pointer);
        }
    }

    private void GenerateTempLabelIfNeededAt(int pcOffset)
    {
        var snes = GetAddressOfAnyUsefulLabelsAt(pcOffset);
        if (snes == -1)
            return;

        var labelName = GenerateGenericTempLabel(snes);
        Data.TemporaryLabelProvider.AddTemporaryLabel(snes, new Label {Name = labelName});
    }

    private int GetAddressOfAnyUsefulLabelsAt(int pcOffset)
    {
        if (GenerateAllUnlabeled)
            return Data.ConvertPCtoSnes(pcOffset);

        var flag = Data.Data.GetSnesApi().GetFlag(pcOffset);
        var usefulToCreateLabelFrom = flag is 
                FlagType.Opcode or 
                FlagType.Pointer16Bit or 
                FlagType.Pointer24Bit or 
                FlagType.Pointer32Bit;

        if (!usefulToCreateLabelFrom)
            return -1;

        var snesIa = Data.GetIntermediateAddressOrPointer(pcOffset);
        var pc = Data.ConvertSnesToPc(snesIa);
        return pc >= 0 ? snesIa : -1;
    }

    private string GenerateGenericTempLabel(int snes)
    {
        var pcOffset = Data.ConvertSnesToPc(snes);
        var prefix = RomUtil.TypeToLabel(Data.Data.GetSnesApi().GetFlag(pcOffset));
        var labelAddress = Util.ToHexString6(snes);
        return $"{prefix}_{labelAddress}";
    }
}