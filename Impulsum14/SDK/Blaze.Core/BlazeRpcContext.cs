namespace Blaze.Core;

public class BlazeRpcContext
{
    public ushort ComponentId { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public required IBlazeConnection Connection { get; init; }
    public object? State => Connection.State;
    public required ushort ErrorCode { get; init; }
    public required uint MessageNum { get; init; }
    public required byte UserIndex { get; init; }
    public required ulong Context { get; init; }
}
