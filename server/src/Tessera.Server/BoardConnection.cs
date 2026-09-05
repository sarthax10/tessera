using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Tessera.Crdt;
using Tessera.Sync;
using Tessera.Sync.Wire;

namespace Tessera.Server;

/// <summary>Drives one WebSocket for its lifetime. The only class that knows sockets exist.</summary>
public sealed class BoardConnection(RoomRegistry registry, ILogger<BoardConnection> logger)
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    public async Task RunAsync(WebSocket socket, BoardId board, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The first frame carries the replica id everything else is attributed to, so nothing can
        // be accepted before it arrives.
        var opening = await ReadMessageAsync(socket, connectionScope.Token).ConfigureAwait(false);

        if (opening is not ClientMessage.Join join)
        {
            await CloseAsync(socket, "The first message must be a join.").ConfigureAwait(false);
            return;
        }

        if (join.Board != board)
        {
            await CloseAsync(socket, "Join does not match the board in the URL.").ConfigureAwait(false);
            return;
        }

        var room = await registry.OpenAsync(board, cancellationToken).ConfigureAwait(false);
        var subscriber = new WebSocketSubscriber(socket, join.Replica);
        var pump = subscriber.PumpAsync(connectionScope.Token);

        try
        {
            await room.JoinAsync(subscriber, join.Have).ConfigureAwait(false);
            await ReadLoopAsync(socket, room, join, connectionScope.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.ConnectionDropped(join.Replica, board);
        }
        finally
        {
            // Leave first, so peers see the departure even if the connection died mid-frame.
            await room.LeaveAsync(join.Replica).ConfigureAwait(false);
            subscriber.Complete();
            await connectionScope.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(
        WebSocket socket,
        Room room,
        ClientMessage.Join join,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null) return;

            switch (message)
            {
                case ClientMessage.Push push:
                    await room.PushAsync(join.Replica, push.Operations).ConfigureAwait(false);
                    break;

                case ClientMessage.Presence presence:
                    await room.UpdatePresenceAsync(join.Replica, presence.State).ConfigureAwait(false);
                    break;

                case ClientMessage.Join:
                    logger.UnexpectedRejoin(join.Replica);
                    break;

                default:
                    logger.UnknownMessage(message.GetType().Name);
                    break;
            }
        }
    }

    /// <summary>Reads one message, reassembling fragments. Null when the peer closes.</summary>
    private async Task<ClientMessage?> ReadMessageAsync(
        WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

        while (true)
        {
            var segment = buffer.GetMemory(4096);
            var result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close) return null;

            buffer.Advance(result.Count);

            if (buffer.WrittenCount > MaxMessageBytes)
            {
                await CloseAsync(socket, "Message too large.").ConfigureAwait(false);
                return null;
            }

            if (result.EndOfMessage) break;
        }

        try
        {
            return WireFormat.Deserialize<ClientMessage>(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            // Malformed input from a browser is a client bug or an attack, never a reason to fail.
            logger.MalformedMessage(ex.Message);
            return null;
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        if (socket.State != WebSocketState.Open) return;

        await socket.CloseAsync(
            WebSocketCloseStatus.ProtocolError, reason, CancellationToken.None).ConfigureAwait(false);
    }
}

internal static partial class BoardConnectionLogs
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Replica {Replica} dropped from board {Board}.")]
    public static partial void ConnectionDropped(this ILogger logger, ReplicaId replica, BoardId board);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replica {Replica} sent a second join.")]
    public static partial void UnexpectedRejoin(this ILogger logger, ReplicaId replica);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unknown message {MessageType}.")]
    public static partial void UnknownMessage(this ILogger logger, string messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarding malformed message: {Reason}")]
    public static partial void MalformedMessage(this ILogger logger, string reason);
}
