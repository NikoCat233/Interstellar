namespace Interstellar.Messages;

public interface IMessageProcessor
{
    int Process(MessageTag tag, ReadOnlySpan<byte> bytes);
}