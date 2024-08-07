﻿using Diz.Core.Interfaces;
using Diz.LogWriter.util;

namespace Diz.LogWriter
{
    public interface IAsmCreationStep
    {
        bool Enabled { get; set; }
        void Generate();
    }

    public abstract class AsmCreationBase : IAsmCreationStep
    {
        public LogCreator LogCreator { get; init; }
        public bool Enabled { get; set; } = true;
        public ILogCreatorDataSource<IData> Data => LogCreator?.Data;

        public void Generate()
        {
            if (!Enabled)
                return;
            
            Execute();
        }
        protected abstract void Execute();
    }

    public class AsmCreationMainBankIncludes : AsmCreationBase
    {
        protected override void Execute()
        {
            var size = LogCreator.GetRomSize();
            
            for (var i = 0; i < size; i += Data.GetBankSize())
                LogCreator.WriteSpecialLine("incsrc", i);
            
            // output the include for labels.asm file
            // int.Minvalue here is just a magic nnumber that means output a line with "labels.asm" on it
            LogCreator.WriteSpecialLine("incsrc", int.MinValue);
        }
    }
    
    public class AsmCreationRomMap : AsmCreationBase
    {
        protected override void Execute()
        {
            LogCreator.WriteSpecialLine("map");
            LogCreator.WriteEmptyLine();
        }
    }
}