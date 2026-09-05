using System.Net.WebSockets;
using System.Threading.Channels;
using Tessera.Crdt;
using Tessera.Sync;
using Tessera.Sync.Wire;

namespace Tessera.Server;

/// <summary>
/// A room participant backed by a WebSocket. Sends are queued rather than written from the room's
/// consumer, which is shared by everyone on the board.
/// </summary>
public sealed class WebSocketSubscriber(WebSocket socket, ReplicaId replica) : IRoomSubscriber
{
    private const int OutboundCapacity = 256;

    private readonly Channel<ServerMessage> _outbound =
        Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ReplicaId Replica { get; } = replica;

    /// <summary>
    /// Stale presence is dropped when the queue fills; document traffic never is, so a client too
    /// slow to keep up is disconnected instead of turning into unbounded server memory.
    /// </summary>
    public ValueTask SendAsync(ServerMessage message, CancellationToken cancellationToken = default)
    {
        if (_outbound.Writer.TryWrite(message)) return ValueTask.CompletedTask;

        if (message is ServerMessage.PeerPresence) return ValueTask.CompletedTask;

        throw new IOException(
            $"Replica {Replica} is more than {OutboundCapacity} messages behind; disconnecting.");
    }

    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _outbound.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await socket.SendAsync(
                    WireFormat.Serialize(message),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The read loop handles teardown.
        }
    }

    public void Complete() => _outbound.Writer.TryComplete();
}
