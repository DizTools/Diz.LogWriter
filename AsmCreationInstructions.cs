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
            var size = LogCreator.GetRomSize();
            BankManager = new AsmCreationBankManager
            {
                LogCreator = LogCreator,
            };

            // perf: this is the meat of the export, takes a while
            for (var pointer = 0; pointer < size;)
            {
                WriteAddress(ref pointer);
            }
        }

        // write one line of the assembly output
        // address is a "PC address" i.e. offset into the ROM.
        // not a SNES address.
        private void WriteAddress(ref int pointer)
        {
            BankManager.SwitchBanksIfNeeded(pointer);

            WriteBlankLineIfStartingNewParagraph(pointer);
            var lineTxt = LogCreator.LineGenerator.GenerateNormalLine(pointer);
            LogCreator.WriteLine(lineTxt);
            LogCreator.DataErrorChecking.CheckForErrorsAt(pointer);
            WriteBlankLineIfEndPoint(pointer);

            pointer += LogCreator.GetLineByteLength(pointer);
        }

        private void WriteBlankLineIfStartingNewParagraph(int pointer)
        {
            // skip if we're in the middle of a pointer table
            if (Data.GetFlag(pointer) is FlagType.Pointer16Bit or FlagType.Pointer24Bit or FlagType.Pointer32Bit)
                return;

            if (Data.IsLocationAReadPoint(pointer) || AreAnyLabelsPresentAt(pointer)) 
                LogCreator.WriteEmptyLine();
        }

        private bool AreAnyLabelsPresentAt(int pointer)
        {
            var snesAddress = Data.ConvertPCtoSnes(pointer);
            return Data.Labels.GetLabel(snesAddress)?.Name.Length > 0;
        }

        private void WriteBlankLineIfEndPoint(int pointer)
        {
            if (!Data.IsLocationAnEndPoint(pointer))
                return;

            LogCreator.WriteEmptyLine();
        }
    }
}