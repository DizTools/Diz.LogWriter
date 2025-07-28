using System.IO;
using Diz.Core.export;
using Diz.Core.Interfaces;
using Diz.Cpu._65816;
using Diz.LogWriter.util;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Global

namespace Diz.LogWriter;

public interface ILogCreatorForGenerator
{ 
    public LogWriterSettings Settings { get; }
    ILogCreatorDataSource<IData> Data { get; }

    int GetLineByteLength(int offset);
    
    // report some events to the log creator so it can stash some meta-information about the assembly output
    void OnLabelVisited(int snesAddress);
    void OnInstructionVisited(int offset, CpuInstructionDataFormatted cpuInstructionDataFormatted);
}

public abstract class TokenBase
{
    
}

public class TokenString : TokenBase
{
    public string Value { get; init; } = "";
}

public class TokenComment : TokenString
{
    
}

public class TokenLabelAssign : TokenString
{
    // in this, value contains the entire assignment for ONE label
}
    
public abstract class AssemblyPartialLineGenerator
{
    public ILogCreatorForGenerator LogCreator { get; set; }
        
    protected ILogCreatorDataSource<IData> Data => LogCreator.Data;
    protected ISnesApi<IData> SnesApi => Data.Data.GetSnesApi();

    public string Token { get; init; } = "";
    public int DefaultLength { get; init; }
    public bool RequiresToken { get; init; } = true;
    public bool UsesOffset { get; init; } = true;

    // helper
    public static TokenBase[] GenerateFromStr(string str)
    {
        return [
            new TokenString {
                Value = str
            }
        ];
    }

    public TokenBase[] GenerateTokens(int? offset, int? lengthOverride)
    {
        var finalLength = lengthOverride ?? DefaultLength;

        Validate(offset, finalLength);

        if (offset == null && UsesOffset)
            throw new InvalidDataException("Invalid Emit() call: Can't call without an offset.");

        var generatedTokens = !UsesOffset
            ? Generate(finalLength) 
            : Generate(offset ?? -1, finalLength);

        return generatedTokens;
    }

    protected virtual TokenBase[] Generate(int length)
    {
        throw new InvalidDataException("Invalid Generate() call: Can't call without an offset.");
    }
        
    protected virtual TokenBase[] Generate(int offset, int length)
    {
        // NOTE: if you get here (without an override in a derived class)
        // it means the client code should have instead been calling the other Generate(length) overload
        // directly. for now, we'll gracefully handle it, but client code should try and be better about it
        // eventually.
            
        return Generate(length);
        // throw new InvalidDataException("Invalid Generate() call: Can't call with offset.");
    }
        
    // call Validate() before doing anything in each Emit()
    // if length is non-zero, use that as our length, if not we use the default length
    protected virtual void Validate(int? offset, int finalLength)
    {
        if (finalLength == 0)
            throw new InvalidDataException("Assembly output component needed a length but received none.");

        if (RequiresToken && string.IsNullOrEmpty(Token))
            throw new InvalidDataException("Assembly output component needed a token but received none.");

        // we should throw exceptions both ways, for now though we'll let it slide if we were passed in
        // an offset and we don't need it.
        var hasOffset = offset != null;

        if (UsesOffset != hasOffset)
            throw new InvalidDataException(UsesOffset
                ? "Assembly output component needed an offset but received none."
                : "Assembly output component doesn't use an offset but we were provided one anyway.");
    }
}