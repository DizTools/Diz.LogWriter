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
        for (var pointer = 0; pointer < LogCreator.GetRomSize(); pointer += LogCreator.GetLineByteLength(pointer))
        {
            GenerateTempLabelIfNeededAt(pointer);
        }
    }

    // this could have some problems but, is probably the right thing to do by default.
    // turn off if diagnosing mirrored label name issues with the resulting assembly.
    private const bool fixup_snes_labels_by_reconverting_addresses = true;

    private void GenerateTempLabelIfNeededAt(int pcOffset)
    {
        var snes = GetAddressOfAnyUsefulLabelsAt(pcOffset);
        if (snes == -1)
            return;
        
        // here's the fun part. (Note: we might need to do this in other parts of the assembly genreation process too)
        // the actual bytes in the ROM will sometimes cause us to generate a mirrored address that is totally fine, but
        // other parts of the assembly label generating process will use a different mirrored address that still maps to
        // the same bytes.
        var normalizedSnesAddress =
            fixup_snes_labels_by_reconverting_addresses
                ? HackyNormalizeSnesMirroredAddress(snes)
                : snes;

        var normalizedLabelText = GenerateGenericTempLabel(normalizedSnesAddress);
        
        if (fixup_snes_labels_by_reconverting_addresses)
        {
            // TODO:
            // if an existing label already exists at this address, use that for both the new and temp label names.
            // kind of a weird artifact of doing aliases this way.
            var label1 = Data.Labels.GetLabel(snes);
            var label2 = Data.Labels.GetLabel(normalizedSnesAddress);

            if (label1 != null || label2 != null)
            {
                normalizedLabelText = label1?.Name ?? label2.Name;
            }
        }
        
        Data.TemporaryLabelProvider.AddTemporaryLabel(normalizedSnesAddress, new Label {Name = normalizedLabelText});

        if (fixup_snes_labels_by_reconverting_addresses && snes != normalizedSnesAddress)
        {
            // now, create an alias to deal with the mirroring.
            // this label will have the normalized TEXT (i.e. DATA8_808017) but will have an aliased offset.
            // this means there will be two labels with that test, one at 0x008017, one at 0x808017.
            // either one will print the SAME text of the normalized label i.e. DATA8_808017 and never DATA8_008017 
            Data.TemporaryLabelProvider.AddTemporaryLabel(snes, new Label {Name = normalizedLabelText});
        }
    }

    private int GetAddressOfAnyUsefulLabelsAt(int pcOffset)
    {
        if (GenerateAllUnlabeled)
            return Data.ConvertPCtoSnes(pcOffset); // this may not be right either...

        var flag = Data.Data.GetSnesApi().GetFlag(pcOffset);
        var usefulToCreateLabelFrom =
            flag == FlagType.Opcode || flag == FlagType.Pointer16Bit ||
            flag == FlagType.Pointer24Bit || flag == FlagType.Pointer32Bit;

        if (!usefulToCreateLabelFrom)
            return -1;

        var snesIa = Data.GetIntermediateAddressOrPointer(pcOffset);
        if (snesIa == -1)
            return -1;
        
        var pc = Data.ConvertSnesToPc(snesIa);
        return pc >= 0 ? snesIa : -1;
    }

    protected int HackyNormalizeSnesMirroredAddress(int snes)
    {
        // in order to produce correct assembly, we're going to try to normalize the mirrored addresses here
        // example: (LoRom mapping)
        // Snes address 0x808017 and 0x008017 both ACTUALLY refer to the SAME ROM offset of 0x17.
        // we'll normalize 0x008017 to turn it into it's mirrored version of 0x808017.
        // super-simple dumb hacky way to do this: convert SNES to PC and back again.
        
        var pcOffset = Data.ConvertSnesToPc(snes);
        if (pcOffset == -1)
            return snes;    // eh, we tried, no luck.

        return Data.ConvertPCtoSnes(pcOffset);
    }

    private string GenerateGenericTempLabel(int snes)
    {
        var pcOffset = Data.ConvertSnesToPc(snes);
        var prefix = RomUtil.TypeToLabel(Data.Data.GetSnesApi().GetFlag(pcOffset));
        var labelAddress = Util.ToHexString6(snes);
        return $"{prefix}_{labelAddress}";
    }
}