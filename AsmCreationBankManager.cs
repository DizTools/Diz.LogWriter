﻿using Diz.Core.Interfaces;
using Diz.Core.util;
using Diz.LogWriter.util;

namespace Diz.LogWriter
{
    public class AsmCreationBankManager
    {
        public LogCreator LogCreator { get; init; }
        public ILogCreatorDataSource<IData> Data => LogCreator?.Data;
        public int CurrentBank { get; protected set; } = -1;

        public void SwitchBanksIfNeeded(int pointer)
        {
            var snesAddress = Data.ConvertPCtoSnes(pointer);
            var newBank = RomUtil.GetBankFromSnesAddress(snesAddress);

            if (newBank != CurrentBank)
                SwitchBank(pointer, newBank, snesAddress);
        }

        private void SwitchBank(int pointer, int newBank, int snesAddress)
        {
            LogCreator.SetBank(pointer, newBank);
            CurrentBank = newBank;

            if (snesAddress % Data.GetBankSize() == 0)
                return;

            LogCreator.OnErrorReported(pointer, "An instruction crossed a bank boundary.");
        }
    }
}