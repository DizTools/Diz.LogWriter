using Diz.Core.export;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.util;

public interface ILogCreatorDataSource<out TData> :
    IInstructionGettable,
    IReadOnlyByteSource,
    IRomMapProvider,
    ICommentTextProvider,
    IReadOnlyLabels,
    ISnesAddressConverter,
    ISnesIntermediateAddress,
    IRomSize,
    IInOutPointGettable
    where TData : IData
{
    ITemporaryLabelProvider TemporaryLabelProvider { get; }
    TData Data { get; }
}