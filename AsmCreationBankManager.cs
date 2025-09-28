using Diz.Core.Interfaces;
using Diz.Core.util;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

public class AsmCreationBankManager
{
    public LogCreator LogCreator { get; init; }
    public ILogCreatorDataSource<IData> Data => LogCreator?.Data;
    public int CurrentBank { get; protected set; } = -1;

    public void SwitchBanksIfNeeded(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        var newBank = RomUtil.GetBankFromSnesAddress(snesAddress);

        if (newBank != CurrentBank)
            SwitchBank(offset, newBank);
    }

    private void SwitchBank(int offset, int newBank)
    {
        LogCreator.SetBank(offset, newBank);
        CurrentBank = newBank;
            
        CheckForBankCrossError(offset);

        LogCreator.OnBankVisited(newBank);
    }

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }
}