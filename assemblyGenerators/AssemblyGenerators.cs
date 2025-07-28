using System;
using System.Collections.Generic;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.Cpu._65816;

namespace Diz.LogWriter.assemblyGenerators;

public class AssemblyGeneratePercent : AssemblyPartialLineGenerator
{
    public AssemblyGeneratePercent()
    {
        Token = "";
        DefaultLength = 1;
        RequiresToken = false;
        UsesOffset = false;
    }
    protected override TokenBase[] Generate(int length)
    {
        return GenerateFromStr("%");  // just a literal %
    }
}

public class AssemblyGenerateEmpty : AssemblyPartialLineGenerator
{
    public AssemblyGenerateEmpty()
    {
        Token = "%empty";
        DefaultLength = 1;
        UsesOffset = false;
    }
    protected override TokenBase[] Generate(int length)
    {
        return GenerateFromStr(string.Format($"{{0,{length}}}", ""));
    }
}

public class AssemblyGenerateLabel : AssemblyPartialLineGenerator
{
    public AssemblyGenerateLabel()
    {
        Token = "label";
        DefaultLength = -22;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        // what we're given: a PC offset in ROM.
        // what we need to find: any labels (SNES addresses) that refer to it.
        //
        // i.e. given that we are at PC offset = 0,
        // we find valid SNES offsets mirrored of 0xC08000 and 0x808000 which both refer to the same place
        // 
        // TODO: we may still need to deal with that mirroring here
        // TODO: eventually, support multiple labels tagging the same address, it may not always be just one.
        
        var snesAddress = Data.ConvertPCtoSnes(offset); 
        var label = Data.Labels.GetLabelName(snesAddress);
        if (label == null)
            return GenerateFromStr("");
        
        LogCreator.OnLabelVisited(snesAddress);

        var noColon = label.Length == 0 || label[0] == '-' || label[0] == '+';
        var newLine = (LogCreator.Settings.NewLine && !noColon) ? string.Format($"{Environment.NewLine}{{0,{length}}}", "") : "";

        var str = $"{label}{(noColon ? "" : ":")}";
        return GenerateFromStr($"{Util.LeftAlign(length, str)}{newLine}");
    }
}

public class AssemblyGenerateCode : AssemblyPartialLineGenerator
{
    public AssemblyGenerateCode()
    {
        Token = "code";
        DefaultLength = 37;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var bytes = LogCreator.GetLineByteLength(offset);

        var snesApi = Data.Data.GetSnesApi();
        if (snesApi == null)
            throw new NullReferenceException("SnesApi not present, can't generate line");

        var code = snesApi.GetFlag(offset) switch
        {
            FlagType.Opcode => RenderInstructionStr(offset),
            
            // treat all these as 8bit data
            FlagType.Unreached or 
            FlagType.Operand or 
            FlagType.Data8Bit or FlagType.Graphics or FlagType.Music or FlagType.Empty => 
                snesApi.GetFormattedBytes(offset, 1, bytes),
            
            FlagType.Data16Bit => snesApi.GetFormattedBytes(offset, 2, bytes),
            FlagType.Data24Bit => snesApi.GetFormattedBytes(offset, 3, bytes),
            FlagType.Data32Bit => snesApi.GetFormattedBytes(offset, 4, bytes),
            FlagType.Pointer16Bit => snesApi.GeneratePointerStr(offset, 2),
            FlagType.Pointer24Bit => snesApi.GeneratePointerStr(offset, 3),
            FlagType.Pointer32Bit => snesApi.GeneratePointerStr(offset, 4),
            
            FlagType.Text =>
                // note: this won't always respect the line length because it can generate, on the same line, multiple strings, etc.
                Data.CreateAssemblyFormattedTextLine(offset, bytes),
            
            _ => ""
        };

        return GenerateFromStr(Util.LeftAlign(length, code));
    }

    private string RenderInstructionStr(int offset)
    {
        var cpuInstructionDataFormatted = Data.GetInstructionData(offset);
        
        LogCreator.OnInstructionVisited(offset, cpuInstructionDataFormatted);
        
        // this is the actual thing the assembly generator cares about - the final text
        return cpuInstructionDataFormatted.FullGeneratedText;
    }
}

public class AssemblyGenerateOrg : AssemblyPartialLineGenerator
{
    public AssemblyGenerateOrg()
    {
        Token = "%org";
        DefaultLength = 37;
        UsesOffset = true;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var org =
            $"ORG {Util.NumberToBaseString(Data.ConvertPCtoSnes(offset), Util.NumberBase.Hexadecimal, 6, true)}";
        return GenerateFromStr(Util.LeftAlign(length, org));
    }
}

public class AssemblyGenerateMap : AssemblyPartialLineGenerator
{
    public AssemblyGenerateMap()
    {
        Token = "%map";
        DefaultLength = 37;
        UsesOffset = false;
    }
    protected override TokenBase[] Generate(int length)
    {
        var romMapType = Data.RomMapMode switch
        {
            RomMapMode.LoRom => "lorom",
            RomMapMode.HiRom => "hirom",
            RomMapMode.Sa1Rom => "sa1rom",
            RomMapMode.ExSa1Rom => "exsa1rom",
            RomMapMode.SuperFx => "sfxrom",
            RomMapMode.ExHiRom => "exhirom",
            RomMapMode.ExLoRom => "exlorom",
            _ => ""
        };
        return GenerateFromStr(Util.LeftAlign(length, romMapType));
    }
}
    
// 0+ = bank_xx.asm, -1 = labels.asm
public class AssemblyGenerateIncSrc : AssemblyPartialLineGenerator
{
    public enum SpecialIncSrc
    {
        // hack: pass these in for the 'offset' param.
        // NOTE: never use -1, has special meaning.
        // we should rewrite the code to not rely on this stuffing hack
        Labels = -2,
        Defines = -3,
    }
    
    public AssemblyGenerateIncSrc()
    {
        Token = "%incsrc";
        DefaultLength = 1;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        return GenerateFromStr(Util.LeftAlign(length, BuildIncSrcForOffset(offset)));
    }

    private string BuildIncSrcForOffset(int offset)
    {
        return offset switch
        {
            // this part is fine.
            // if offset is >= 0, build an include for that bank
            >= 0 => BuildOutputForOffset(offset),
            
            // special includes:
            // this is a total hack: negative numbers are IDs. this is a real dumb way to do this:
            (int)SpecialIncSrc.Labels => BuildIncSrc("labels.asm"),
            (int)SpecialIncSrc.Defines => BuildIncSrc("defines.asm"),
            
            _ => $"; internal error: INVALID incsrc={offset}"
        };
    }

    private string BuildOutputForOffset(int offset)
    {
        var bank = Data.ConvertPCtoSnes(offset) >> 16;
        var name = Util.NumberToBaseString(bank, Util.NumberBase.Hexadecimal, 2);
        return BuildBankInclude(name);
    }

    private static string BuildBankInclude(string name)
    {
        var val = $"bank_{name}.asm";
        return BuildIncSrc(val);
    }

    private static string BuildIncSrc(string val)
    {
        return $"incsrc \"{val}\"";
    }
}
    
public class AssemblyGenerateBankCross : AssemblyPartialLineGenerator
{
    public AssemblyGenerateBankCross()
    {
        Token = "%bankcross";
        DefaultLength = 1;
    }
    protected override TokenBase[] Generate(int length)
    {
        return GenerateFromStr(Util.LeftAlign(length, "check bankcross off"));
    }
}
    
public class AssemblyGenerateIndirectAddress : AssemblyPartialLineGenerator
{
    public AssemblyGenerateIndirectAddress()
    {
        Token = "ia";
        DefaultLength = 6;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var ia = Data.GetIntermediateAddressOrPointer(offset);
        return GenerateFromStr(ia >= 0 ? Util.ToHexString6(ia) : "      ");
    }
}
    
public class AssemblyGenerateProgramCounter : AssemblyPartialLineGenerator
{
    public AssemblyGenerateProgramCounter()
    {
        Token = "pc";
        DefaultLength = 6;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        return GenerateFromStr(Util.ToHexString6(Data.ConvertPCtoSnes(offset)));
    }
}
    
public class AssemblyGenerateOffset : AssemblyPartialLineGenerator
{
    public AssemblyGenerateOffset()
    {
        Token = "offset";
        DefaultLength = -6; // trim to length
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var hexStr = Util.NumberToBaseString(offset, Util.NumberBase.Hexadecimal, 0);
        return GenerateFromStr(Util.LeftAlign(length, hexStr));
    }
}
    
public class AssemblyGenerateDataBytes : AssemblyPartialLineGenerator
{
    public AssemblyGenerateDataBytes()
    {
        Token = "bytes";
        DefaultLength = 8;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var bytes = BuildByteString(offset);
            
        // TODO: FIXME: use 'length' here in this format string
        return GenerateFromStr($"{bytes,-8}");
    }

    private string BuildByteString(int offset)
    {
        if (SnesApi.GetFlag(offset) != FlagType.Opcode) 
            return "";
            
        var bytes = "";
        for (var i = 0; i < Data.GetInstructionLength(offset); i++)
        {
            var romByte = Data.GetRomByteUnsafe(offset + i);
            bytes += Util.NumberToBaseString(romByte, Util.NumberBase.Hexadecimal);
        }

        return bytes;
    }
}
    
public class AssemblyGenerateComment : AssemblyPartialLineGenerator
{
    public AssemblyGenerateComment()
    {
        Token = "comment";
        DefaultLength = 1;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var snesOffset = Data.ConvertPCtoSnes(offset);
        var str = Data.GetCommentText(snesOffset);

        return str
            .Replace("\r", "")
            .Split("\n")
            .Select(commentLine => new TokenComment
            {
                Value = Util.LeftAlign(length, commentLine)
            })
            .Cast<TokenBase>()
            .ToArray();
    }
}
    
public class AssemblyGenerateDataBank : AssemblyPartialLineGenerator
{
    public AssemblyGenerateDataBank()
    {
        Token = "b";
        DefaultLength = 2;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        return GenerateFromStr(Util.NumberToBaseString(SnesApi.GetDataBank(offset), Util.NumberBase.Hexadecimal, 2));
    }
}
    
public class AssemblyGenerateDirectPage : AssemblyPartialLineGenerator
{
    public AssemblyGenerateDirectPage()
    {
        Token = "d";
        DefaultLength = 4;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        return GenerateFromStr(Util.NumberToBaseString(SnesApi.GetDirectPage(offset), Util.NumberBase.Hexadecimal, 4));
    }
}
    
public class AssemblyGenerateMFlag : AssemblyPartialLineGenerator
{
    public AssemblyGenerateMFlag()
    {
        Token = "m";
        DefaultLength = 1;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var m = SnesApi.GetMFlag(offset);

        var s = length == 1 
            ? (m ? "M" : "m") 
            : (m ? "08" : "16");
        
        return GenerateFromStr(s);
    }
}
    
public class AssemblyGenerateXFlag : AssemblyPartialLineGenerator
{
    public AssemblyGenerateXFlag()
    {
        Token = "x";
        DefaultLength = 1;
    }
    protected override TokenBase[] Generate(int offset, int length)
    {
        var x = SnesApi.GetXFlag(offset);

        var s = length == 1 
            ? (x ? "X" : "x") 
            : (x ? "08" : "16");

        return GenerateFromStr(s);
    }
}
    
// output label at snes offset, and its value
// example output:  "FnMultiplyByTwo = $808012"
public class AssemblyGenerateLabelAssign : AssemblyPartialLineGenerator
{
    public record PrintableLabelDataAtOffset(int SnesAddress, string Name, string Comment)
    {
        public string GetSnesAddressFormatted() => 
            Util.NumberToBaseString(SnesAddress, Util.NumberBase.Hexadecimal, 6, true);
    }
        
    public static List<PrintableLabelDataAtOffset> GetPrintableLabelsDataAtSnesAddress(int snesAddress, IReadOnlyLabelProvider labelProvider) 
    {
        var label = labelProvider.GetLabel(snesAddress);
        if (label == null)
            return null;
        
        var labelComment = labelProvider.GetLabelComment(snesAddress) ?? "";

        var allLabelNames = new List<string>();
        
        allLabelNames.Add(label.Name);
        allLabelNames.AddRange(label.ContextMappings.Select(x => x.NameOverride));
        
        return allLabelNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new PrintableLabelDataAtOffset(snesAddress, x, labelComment))
            .ToList();
    }
        
    public AssemblyGenerateLabelAssign()
    {
        Token = "%labelassign";
        DefaultLength = 1;
    }

    protected override TokenBase[] Generate(int offset, int length)
    {
        // EXTREMELY IMPORTANT:
        // unlike all the other generators where offset is a ROM offset,
        // for us, offset will be a SNES address.
        // ReSharper disable once InlineTemporaryVariable
        var snesAddress = offset; // yes. this is correct. HACK THIS IN THERE. DO IT.
            
        // this may generate multiple labels for a given SNES address (in the case of multi-context regions)
        // we need to output ALL of them.
        
        var labelsDataAtOffset = GetPrintableLabelsDataAtSnesAddress(snesAddress, Data.Labels);
        if (labelsDataAtOffset == null || labelsDataAtOffset.Count == 0)
            return [];
        
        var tokensOutput = new List<TokenBase>();
        foreach (var labelDataAtOffset in labelsDataAtOffset)
        {
            var finalCommentText = "";

            // TODO: probably not the best way to stuff this in here. -Dom
            // we should consider putting this in the %comment% section in the future.
            // for now, just hacking this in so it's included somewhere. this option defaults to OFF
            if (LogCreator.Settings.PrintLabelSpecificComments && labelDataAtOffset.Comment != "")
            {
                // warning: can contain newlines. would be nicer to preserve these better. for now:
                var commentInsideText = labelDataAtOffset.Comment
                    .Replace("\n", " ")
                    .Replace("\r", " ");
                
                finalCommentText = $"; !^ {commentInsideText} ^!";
            }

            var snesAddrFormatted = labelDataAtOffset.GetSnesAddressFormatted();
            var str = $"{labelDataAtOffset.Name} = {snesAddrFormatted}{finalCommentText}";
            var formattedLabelAssignText = Util.LeftAlign(length, str);

            tokensOutput.Add(new TokenLabelAssign { Value = formattedLabelAssignText });
        }

        return tokensOutput.ToArray();
    }
}