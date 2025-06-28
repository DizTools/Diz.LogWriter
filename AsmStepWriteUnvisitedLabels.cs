using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using Diz.Core.Interfaces;
using Diz.LogWriter.assemblyGenerators;
using JetBrains.Annotations;

namespace Diz.LogWriter
{
    public abstract class AsmStepExtraLabelOutputBase : AsmCreationBase
    {
        public LabelTracker LabelTracker { get; init; }
        
        public static IOrderedEnumerable<int> GetLabelSnesAddressesSorted(IReadOnlyLabelProvider readOnlyLabelProvider)
        {
            // keep these sorted so that the output is consistent
            return readOnlyLabelProvider.Labels
                .Select(x=>x.Key)
                .OrderBy(x=>x);
        }
    }

    // TODO: we can probably refactor/recombine a few of these related classes together

    // generate labels.asm. this is a list of labels that are NOT defined implicitly in the main .asm files
    // this will be a list of unvisited labels. it will NOT contain VISITED labels.
    public class AsmStepWriteUnvisitedLabels : AsmStepExtraLabelOutputBase
    {
        protected override void Execute()
        {
            LogCreator.SwitchOutputStream("labels");

            foreach (var (snesAddress, _) in LabelTracker.UnvisitedLabels)
            {
                WriteUnusedLabel(snesAddress);
            }
        }

        private void WriteUnusedLabel(int snesAddress)
        {
            // shove this in here even though what we SHOULD do is express it as a ROM offset.
            // however, we need to be able to pass in non-ROM SNES addresses.
            var stuffSnesAddressInOffset = snesAddress;
                
            LogCreator.WriteLine(LogCreator.LineGenerator.GenerateSpecialLine("labelassign", stuffSnesAddressInOffset));
        }
    }

    // write all labels, regardless of whether they're used.
    // output in a format that could be used directly for disassembly or importing into bsnes/etc
    // this is all optional: if requested, print all labels regardless of use.
    // Useful for debugging, documentation, or reverse engineering workflow.
    // this file shouldn't need to be included in the build, it's just reference documentation
    public class AsmStepWriteAllLabels : AsmStepExtraLabelOutputBase
    {
        public string OutputFilename { get; init; }

        protected override void Execute()
        {
            LogCreator.SwitchOutputStream(OutputFilename);
            OutputHeader();
            
            foreach (var labelSnesAddress in GetLabelSnesAddressesSorted(Data.Labels))
            {
                WriteLabel(labelSnesAddress);
            }
        }

        protected virtual void OutputHeader()
        {
            // NOP
        }

        private void WriteLabel(int snesAddress)
        {
            // maybe not the best place to add formatting
            var category = LabelTracker.UnvisitedLabels.ContainsKey(snesAddress) ? "UNUSED" : "USED";
            
            // shove this in here even though what we SHOULD do is express it as a ROM offset.
            // however, we need to be able to pass in non-ROM SNES addresses.
            // ReSharper disable once InlineTemporaryVariable
            var hackShoveSnesAddressInsteadOfRomOffset = snesAddress;
            
            OutputLabelAtOffset(category, hackShoveSnesAddressInsteadOfRomOffset);
        }

        protected virtual void OutputLabelAtOffset(string category, int offset)
        {
            LogCreator.WriteLine($";!^!-{category}-! " + LogCreator.LineGenerator.GenerateSpecialLine("labelassign", offset));
        }
    }

    // same as above except output as a CSV file
    public class AsmStepExtraOutputAllLabelsCsv : AsmStepWriteAllLabels
    {
        [CanBeNull] private CsvWriter csvWriter;
        
        private record CsvLabelRecord
        {
            public CsvLabelRecord(AssemblyGenerateLabelAssign.PrintableLabelDataAtOffset printableData, string usedStatus)
            {
                Name = printableData.Name;
                Comment = printableData.Comment;
                UsedStatus = usedStatus;
                SnesAddress = printableData.SnesAddress;
                SnesAddressHex = printableData.GetSnesAddressFormatted();
            }

            public string SnesAddressHex { get; init; }

            public int SnesAddress { get; init; }

            public string Name { get; init; }
            public string Comment { get; init; }
            public string UsedStatus { get; init; }
        } 

        protected override void Execute()
        {
            string output;
            using (var writer = new StringWriter())
            using (csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                base.Execute();
                output = writer.ToString();
            }
            
            var lines = output.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            foreach (var line in lines)
            {
                LogCreator.WriteLine(line);
            }
        }

        protected override void OutputHeader()
        {
            Debug.Assert(csvWriter != null);
            csvWriter.WriteHeader<CsvLabelRecord>();
            csvWriter.NextRecord();
        }

        protected override void OutputLabelAtOffset(string category, int offset)
        {
            var printableLabel = AssemblyGenerateLabelAssign.GetPrintableLabelDataAtOffset(offset, Data.Labels);
            if (printableLabel == null)
                return;
            
            var labelRecord = new CsvLabelRecord(printableLabel, category);

            Debug.Assert(csvWriter != null);
            csvWriter.WriteRecord(labelRecord);
            csvWriter.NextRecord();
        }
    }
    
    

    // same as above except output as a BSNES .sym file, for use with the BSNES debugger
    public class AsmStepExtraOutputBsneSymFile : AsmStepWriteAllLabels
    { 
        protected override void Execute()
        {
            // this will print all the labels
            base.Execute();
            
            // with that done, we also need to print the comments:
            LogCreator.WriteLine("\n");
            LogCreator.WriteLine("[comments]");
            
            foreach (var labelSnesAddress in GetLabelSnesAddressesSorted(Data.Labels))
            {
                var commentText = Data.GetCommentText(labelSnesAddress);
                if (commentText.Length == 0)
                    continue;
                
                WriteLineBsnesFormattedAddressAndText(labelSnesAddress, commentText);
            }
        }
        
        // sample bsnes .sym file format:
        
        // [labels]
        // 00:2100 SNES.INIDISP
        // c2:0aae fn_some_function_battle_whatever
        //
        // [comments]
        // c0:0082 Read variable X and multiply by 42, that is the screen brightness
        // c0:0097 This is the main loop
        
        protected override void OutputHeader()
        {
            LogCreator.WriteLine("[labels]");
        }

        protected override void OutputLabelAtOffset(string category, int offset)
        {
            var printableLabel = AssemblyGenerateLabelAssign.GetPrintableLabelDataAtOffset(offset, Data.Labels);
            if (printableLabel == null)
                return;
            
            // we're going to exclude a few auto-generated labels from this just keep BSNES exports de-cluttered.
            if (printableLabel.Name.StartsWith("CODE_") || printableLabel.Name.StartsWith("LOOSE_OP_"))
                return;
            
            WriteLineBsnesFormattedAddressAndText(printableLabel.SnesAddress, printableLabel.Name);
        }

        private void WriteLineBsnesFormattedAddressAndText(int snesAddress, string text)
        {
            // always output snes addresses (not offsets)
            // "c2:0aae Well hey son this is some fancy label or comment text for BSNES"
            var printableSnesAddress = FormatAs24BitBsnesAddress(snesAddress);
            var formattedText = text.ReplaceLineEndings("");
            LogCreator.WriteLine($"{printableSnesAddress} {formattedText}");
        }

        private static string FormatAs24BitBsnesAddress(int address)
        {
            // print a 24bit number in the format BSNES .sym files like:
            // "c2:0aae"
            return address.ToString("X6").Insert(2, ":");
            
            // (this function doesn't care if it's SNES address or offset, it's printing the number)
        }
    }
}