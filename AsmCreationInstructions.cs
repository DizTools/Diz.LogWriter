using Diz.Core.Interfaces;
using Diz.Core.util;

namespace Diz.LogWriter
{
    // this class outputs the meat of the assembly generation process, it prints the actual assembler instructions
    public class AsmCreationInstructions : AsmCreationBase
    {
        public AsmCreationBankManager BankManager { get; protected set; }

        protected override void Execute()
        {
            var romSize = LogCreator.GetRomSize();
            BankManager = new AsmCreationBankManager
            {
                LogCreator = LogCreator,
            };

            // perf: this is the meat of the export, takes a while
            for (var offset = 0; offset < romSize;)
            {
                WriteAddress(ref offset);
            }
        }

        // write one line of the assembly output
        // address is a "PC address" i.e. offset into the ROM.
        // not a SNES address.
        private void WriteAddress(ref int offset)
        {
            BankManager.SwitchBanksIfNeeded(offset);

            WriteBlankLineIfStartingNewParagraph(offset);
            var lineTxt = LogCreator.LineGenerator.GenerateNormalLine(offset);
            LogCreator.WriteLine(lineTxt);
            LogCreator.DataErrorChecking.CheckForErrorsAt(offset);
            WriteBlankLineIfEndPoint(offset);

            offset += LogCreator.GetLineByteLength(offset);
        }

        private void WriteBlankLineIfStartingNewParagraph(int offset)
        {
            // skip if we're in the middle of a pointer table
            if (Data.GetFlag(offset) is FlagType.Pointer16Bit or FlagType.Pointer24Bit or FlagType.Pointer32Bit)
                return;

            if (Data.IsLocationAReadPoint(offset) || AreAnyLabelsPresentAt(offset)) 
                LogCreator.WriteEmptyLine();
        }

        private bool AreAnyLabelsPresentAt(int offset)
        {
            var snesAddress = Data.ConvertPCtoSnes(offset);
            return Data.Labels.GetLabel(snesAddress)?.Name.Length > 0;
        }

        private void WriteBlankLineIfEndPoint(int offset)
        {
            if (!Data.IsLocationAnEndPoint(offset))
                return;

            LogCreator.WriteEmptyLine();
        }
    }
}