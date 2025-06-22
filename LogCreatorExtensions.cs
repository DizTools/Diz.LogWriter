using System.Text;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.util;

namespace Diz.LogWriter
{
    public static class LogCreatorExtensions
    {
        public static string CreateAssemblyFormattedTextLine(this ILogCreatorDataSource<IData> data, int offset, int count)
        {
            var rawStr = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                rawStr.Append((char)(data.GetRomByte(offset + i) ?? 0));
            }

            return CreateAssemblyFormattedTextLine(rawStr.ToString());
        }
        
        public static string CreateAssemblyFormattedTextLine(string rawStr)
        {
            // important: Asar will not accept null characters printed inside quoted text. so we need to break up text lines.
            // also, asar seems to have issues with exclamation points in text
            bool IsPrintableAsciiCharacter(char c) => 
                c >= 32 && c <= 127 && c != '"' && c != '!';

            var outputStr = new StringBuilder("db ");
            var inQuotedSection = false;

            bool StartQuotedSectionIfNeeded(bool printedSomethingBeforeThis)
            {
                if (inQuotedSection)
                    return false;

                if (printedSomethingBeforeThis)
                    outputStr.Append(", ");
                
                outputStr.Append('"');
                inQuotedSection = true;

                return true;
            }

            bool EndQuotedSectionIfNeeded()
            {
                if (!inQuotedSection)
                    return false;
                
                outputStr.Append('"');
                inQuotedSection = false;

                return true;
            }

            var previouslyOutputConstant = false;

            var i = 0;
            foreach (var c in rawStr)
            {
                if (IsPrintableAsciiCharacter(c))
                {
                    StartQuotedSectionIfNeeded(i != 0);

                    // final thing.  there are some characters that, yes, are printable, but, we need an extra escape for them.
                    switch (c)
                    {
                        // NOTE: there might be some way to print a literal double quote in the output stream but,
                        // couldn't figure it out.  something about doubling quotes.
                        // for now, we'll count it as "not printable"
                        
                        case '\\':
                            // literal single backslash, we need to escape it when we write it.
                            outputStr.Append(@"\\"); // literally double backslash.
                            break;
                        default:
                            // otherwise, it's just a normal character
                            outputStr.Append(c);
                            break;
                    }
                } 
                else
                {
                    if (EndQuotedSectionIfNeeded() || previouslyOutputConstant)
                        outputStr.Append(", ");
                        
                    outputStr.Append('$');
                    outputStr.Append(Util.NumberToBaseString(c, Util.NumberBase.Hexadecimal, 2));

                    previouslyOutputConstant = true;
                }

                ++i;
            }
            
            EndQuotedSectionIfNeeded();

            return outputStr.ToString();
        }
        
        public static int GetLineByteLength(this ILogCreatorDataSource<IData> data, int offset, int romSizeMax,
            int countPerLine)
        {
            var snesApi = data.Data.GetSnesApi();
            var flagType = snesApi.GetFlag(offset);

            if (flagType == FlagType.Opcode)
                return data.GetInstructionLength(offset);

            GetLineByteLengthMaxAndStep(flagType, out var max, out var step, countPerLine);

            var bankSize = data.GetBankSize();
            var myBank = offset / bankSize;

            var min = step;
            while (
                min < max &&
                offset + min < romSizeMax &&
                snesApi.GetFlag(offset + min) == flagType &&
                data.Labels.GetLabelName(data.ConvertPCtoSnes(offset + min)) == "" &&
                (offset + min) / bankSize == myBank
            ) min += step;
            return min;
        }
        
        private static void GetLineByteLengthMaxAndStep(FlagType flagType, out int max, out int step, int dataPerLineSize)
        {
            max = 1; step = 1;

            switch (flagType)
            {
                case FlagType.Opcode:
                    break;
                case FlagType.Unreached:
                case FlagType.Operand:
                case FlagType.Data8Bit:
                case FlagType.Graphics:
                case FlagType.Music:
                case FlagType.Empty:
                    max = dataPerLineSize;
                    break;
                case FlagType.Text:
                    max = 21;
                    break;
                case FlagType.Data16Bit:
                    step = 2;
                    max = dataPerLineSize;
                    break;
                case FlagType.Data24Bit:
                    step = 3;
                    max = dataPerLineSize;
                    break;
                case FlagType.Data32Bit:
                    step = 4;
                    max = dataPerLineSize;
                    break;
                case FlagType.Pointer16Bit:
                    step = 2;
                    max = 2;
                    break;
                case FlagType.Pointer24Bit:
                    step = 3;
                    max = 3;
                    break;
                case FlagType.Pointer32Bit:
                    step = 4;
                    max = 4;
                    break;
            }
        }

        public static string GeneratePointerStr(this ISnesApi<IData> data, int offset, int bytes)
        {
            var ia = -1;
            string format = "", param = "";
            switch (bytes)
            {
                case 2:
                    // here's a tricky Diz-specific thing.
                    // at this address, we only have the two bytes of the IA to work with (since this is a 16-bit pointer)
                    // always (maybe... almost always? can't think of a case otherwise), what we want for the bank is the
                    // SAME bank as where the pointer is sitting.  what we DON'T want is the overridden bank (the one you can type in the UI).
                    
                    // older Diz behavior, user has to specify the bank we'll use for the label address
                    var bankFromUser = data.GetDataBank(offset) << 16;
                    
                    // newer Diz Behavior:
                    // this is better (as long as it's valid) because it will insert the correct labels into the pointer table assembly output
                    var autoDetectedBank = RomUtil.GetBankFromSnesAddress(data.ConvertPCtoSnes(offset));
                    
                    // If the user overrode the bank in the pointer table, OK, we'll use that.
                    // sometimes useful for things like pointer tables to other banks.
                    var bankToUse = bankFromUser != 0 ? bankFromUser : autoDetectedBank;
                    
                    ia = (bankToUse << 16) | data.GetRomWordUnsafe(offset);
                    
                    format = "dw {0}";
                    param = Util.NumberToBaseString(data.GetRomWordUnsafe(offset), Util.NumberBase.Hexadecimal, 4, true);
                    break;
                case 3:
                    ia = data.GetRomLongUnsafe(offset);
                    format = "dl {0}";
                    param = Util.NumberToBaseString(data.GetRomLongUnsafe(offset), Util.NumberBase.Hexadecimal, 6, true);
                    break;
                case 4:
                    ia = data.GetRomLongUnsafe(offset);
                    format = "dl {0}" +
                             $" : db {Util.NumberToBaseString(data.GetRomByteUnsafe(offset + 3), Util.NumberBase.Hexadecimal, 2, true)}";
                    param = Util.NumberToBaseString(data.GetRomLongUnsafe(offset), Util.NumberBase.Hexadecimal, 6, true);
                    break;
            }

            if (data.ConvertSnesToPc(ia) >= 0)
            {
                var labelName = data.Labels.GetLabelName(ia);
                if (labelName != "")
                    param = labelName;
            }

            return string.Format(format, param);
        }
        
        public static string GetFormattedBytes(this IReadOnlyByteSource data, int offset, int step, int bytes)
        {
            var res = step switch
            {
                1 => "db ",
                2 => "dw ",
                3 => "dl ",
                4 => "dd ",
                _ => ""
            };

            for (var i = 0; i < bytes; i += step)
            {
                if (i > 0) res += ",";

                switch (step)
                {
                    case 1:
                        res += Util.NumberToBaseString(data.GetRomByteUnsafe(offset + i), Util.NumberBase.Hexadecimal, 2,
                            true);
                        break;
                    case 2:
                        res += Util.NumberToBaseString(data.GetRomWordUnsafe(offset + i), Util.NumberBase.Hexadecimal, 4,
                            true);
                        break;
                    case 3:
                        res += Util.NumberToBaseString(data.GetRomLongUnsafe(offset + i), Util.NumberBase.Hexadecimal, 6,
                            true);
                        break;
                    case 4:
                        res += Util.NumberToBaseString(data.GetRomDoubleWordUnsafe(offset + i), Util.NumberBase.Hexadecimal,
                            8, true);
                        break;
                }
            }

            return res;
        }
    }
}