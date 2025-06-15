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

    private void GenerateTempLabelIfNeededAt(int originOffset)
    {
        var snesData = Data.Data.GetSnesApi();
        if (snesData == null)
            return;
        
        var snesAddressToGenerateLabelAt = -1;
        var useHints = false;
        
        // 1. treat our offset as the origin, check if it references an IA that is interesting to make a label from: 
        var flag = snesData.GetFlag(originOffset);
        var originWasOpcode = flag == FlagType.Opcode;
        var destinationIaMightBeInteresting = originWasOpcode || flag is 
            FlagType.Pointer16Bit or 
            FlagType.Pointer24Bit or 
            FlagType.Pointer32Bit;

        if (destinationIaMightBeInteresting)
        {
            var snesDestinationIa = Data.GetIntermediateAddressOrPointer(originOffset);
            var offsetOfIa = Data.ConvertSnesToPc(snesDestinationIa);
            if (offsetOfIa != -1)
            {
                snesAddressToGenerateLabelAt = snesDestinationIa;
                useHints = true;
            }
        }

        // 2. our origin address doesn't have anything interesting going on.
        // should we add a label for the origin address anyway? (in case we want to see ALL labels [not typical but an option])
        if (snesAddressToGenerateLabelAt == -1 && GenerateAllUnlabeled)
        {
            snesAddressToGenerateLabelAt = Data.ConvertPCtoSnes(originOffset);
            useHints = false;
        }
        
        // no reason to create any new labels, bail
        if (snesAddressToGenerateLabelAt == -1)
            return; 

        // OK, we have a 
        var prefix = "";
        var offsetToGenerateLabelAt = Data.ConvertSnesToPc(snesAddressToGenerateLabelAt);

        if (useHints)
        {
            // figure out if there's anything interesting going on that we might want to change the label somewhat:
            
            // A. was this a JSR or JSL?
            if (originWasOpcode)
            {
                var originRomByte = snesData.GetRomByte(originOffset);
                prefix = originRomByte switch
                {
                    0x20 => "CODE_FN",                      // JSR
                    0x22 => "CODE_FL",                      // JML
                    _ => ""
                };
            }
        }

        // this is like "CODE" etc
        if (prefix.Length == 0)
            // 2. nothing special, just generate a generic label for this like CODE_xxxx or DATA_xxx
            prefix = RomUtil.TypeToLabel(snesData.GetFlag(offsetToGenerateLabelAt));
        
        var labelAddress = Util.ToHexString6(snesAddressToGenerateLabelAt);
        var labelName = $"{prefix}_{labelAddress}";
        
        Data.TemporaryLabelProvider.AddOrReplaceTemporaryLabel(snesAddressToGenerateLabelAt, new Label {Name = labelName});
    }
}