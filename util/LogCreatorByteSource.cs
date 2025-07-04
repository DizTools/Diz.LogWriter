﻿using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Cpu._65816;

namespace Diz.LogWriter.util;

public class LogCreatorByteSource : ILogCreatorDataSource<IData>
{
    public LogCreatorByteSource(IData data)
    {
        Data = data;
    }
    
    public IData Data { get; }
    public ITemporaryLabelProvider TemporaryLabelProvider => Data.Labels;

    protected ISnesData SnesApi => Data.GetSnesApi();
    public byte? GetRomByte(int offset) => Data.GetRomByte(offset);
    public int? GetRomWord(int offset) => Data.GetRomWord(offset);
    public int? GetRomLong(int offset) => Data.GetRomLong(offset);
    public int? GetRomDoubleWord(int offset) => Data.GetRomDoubleWord(offset);
    public RomMapMode RomMapMode
    {
        get => Data.RomMapMode;
        set => Data.RomMapMode = value; // need to not use this for diz3
    }

    public RomSpeed RomSpeed
    {
        get => Data.RomSpeed;
        set => Data.RomSpeed = value;  // need to not use this for diz3
    }

    public IReadOnlyLabelProvider Labels => Data.Labels;
    public string GetCommentText(int snesAddress) => Data.GetCommentText(snesAddress);
    public string GetComment(int snesAddress) => Data.GetComment(snesAddress);

    public int GetInstructionLength(int offset) => SnesApi.GetInstructionLength(offset);
    public string GetInstructionStr(int offset) => SnesApi.GetInstructionStr(offset);
    public CpuInstructionDataFormatted GetInstructionData(int offset) => SnesApi.GetInstructionData(offset);
    public int ConvertPCtoSnes(int offset) => SnesApi.ConvertPCtoSnes(offset);
    public int ConvertSnesToPc(int offset) => SnesApi.ConvertSnesToPc(offset);
    public int GetIntermediateAddressOrPointer(int offset) => SnesApi.GetIntermediateAddressOrPointer(offset);
    public int GetIntermediateAddress(int offset, bool resolve = false) => SnesApi.GetIntermediateAddress(offset, resolve);
    public bool IsMatchingIntermediateAddress(int intermediateAddress, int addressToMatch) => SnesApi.IsMatchingIntermediateAddress(intermediateAddress, addressToMatch);

    public int GetRomSize() => SnesApi.GetRomSize();
    public int GetBankSize() => SnesApi.GetBankSize();
    public InOutPoint GetInOutPoint(int offset) => SnesApi.GetInOutPoint(offset);
    public int GetMxFlags(int i) => SnesApi.GetMxFlags(i);

    public bool GetMFlag(int i) => SnesApi.GetMFlag(i);

    public bool GetXFlag(int i) => SnesApi.GetXFlag(i);

    public int GetDataBank(int offset) => SnesApi.GetDataBank(offset);

    public int GetDirectPage(int offset) => SnesApi.GetDirectPage(offset);

    public FlagType GetFlag(int offset) => SnesApi.GetFlag(offset);
}