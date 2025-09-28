using System.Collections.Generic;
using Diz.Core.Interfaces;
using Diz.Core.util;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

public class AsmCreationBankManager
{
    public LogCreator LogCreator { get; init; }
    public ILogCreatorDataSource<IData> Data => LogCreator?.Data;
    public int CurrentBank { get; protected set; } = -1;
    public List<int> VisitedBanks { get; } = [];

    public void SwitchBanksIfNeeded(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        var newBank = RomUtil.GetBankFromSnesAddress(snesAddress);

        if (newBank == CurrentBank) 
            return;
        
        SwitchBank(newBank);
        CheckForBankCrossError(offset);
    }

    public static string GetBankStreamName(int bank)
    {
        var bankStr = Util.NumberToBaseString(bank, Util.NumberBase.Hexadecimal, 2);
        var bankStreamName = $"bank_{bankStr}.asm";
        return bankStreamName;
    }

    // switch to a bank, one we may or may not have visited before
    private void SwitchBank(int bank)
    {
        SwitchOutputStreamForBank(bank);

        if (!VisitedBanks.Contains(bank)) {
            VisitedBanks.Add(bank);
            LogCreator.WriteOrgDirectiveForOffset(bank << 16);
        }

        CurrentBank = bank;
    }

    private void SwitchOutputStreamForBank(int bank) => 
        LogCreator.SwitchOutputStream(GetBankStreamName(bank));

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }
}