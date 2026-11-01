using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MusicCat.CLVKM.Core.Ipc;

/// <summary>
/// The channel between the watcher and the tray app.
///
/// Both halves are created with <see cref="PipeOptions.CurrentUserOnly"/>, so Windows builds
/// the security descriptor itself: the DACL grants the interactive user (and the local
/// SYSTEM/Administrators that created it) and nothing else, and remote clients are refused.
/// Another account on the same machine therefore cannot connect, let alone inject events that
/// would make the keyboard flash for somebody else.
/// </summary>
public static class WatcherPipe
{
    /// <summary>Name shared by the server and the client.</summary>
    public const string Name = "MusicCatRGB.Watcher.v1";

    /// <summary>How long the client waits for the server before retrying.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Pause between reconnects when the other end is not there yet.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Watcher half of the channel: accepts one client at a time and forwards the events the
/// hooks produce. Nobody connected means the events are dropped on purpose — a flash that
/// arrives a minute late is worse than no flash at all.
/// </summary>
public sealed class WatcherEventServer : IAsyncDisposable
{
    private readonly Channel<WatcherEvent> _outbox = Channel.CreateBounded<WatcherEvent>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Latest moment a client was connected; used to notice that nobody listens.</summary>
    public DateTime? LastClientUtc { get; private set; }

    /// <summary>True while a client is connected and receiving.</summary>
    public bool HasClient { get; private set; }

    /// <summary>Queues an event for the connected client; a no-op while nobody is listening.</summary>
    public void Publish(WatcherEvent message) => _outbox.Writer.TryWrite(message);

    /// <summary>Serves connections until the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                WatcherPipe.Name,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The instance could not be created or the client vanished mid-handshake.
                continue;
            }

            HasClient = true;
            LastClientUtc = DateTime.UtcNow;

            try
            {
                await PumpAsync(server, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                HasClient = false;
            }
        }
    }

    /// <summary>
    /// Forwards events until the pipe breaks. A silent second sends an empty line instead:
    /// it is the only reliable way to notice that the client is gone — <c>IsConnected</c> on a
    /// server stream stays true until an I/O operation actually fails. The app ignores blank
    /// lines, so the keepalive costs nothing.
    /// </summary>
    private async Task PumpAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: false)
            {
                AutoFlush = true
            };

            var heartbeat = TimeSpan.FromSeconds(1);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_outbox.Reader.TryRead(out var message))
                {
                    await writer.WriteLineAsync(message.ToJson().AsMemory(), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
                await Task.Delay(heartbeat, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // The app went away; the next client gets a fresh instance.
        }
        catch (ObjectDisposedException)
        {
            // Same: the pipe was closed underneath us.
        }
    }

    public ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// App half of the channel: connects to the watcher, reconnects when it restarts, and raises
/// <see cref="Received"/> for every message. The connection is verified to be the expected
/// one: the server sets the ACL, so a successful connect already means a trusted peer.
/// </summary>
public sealed class WatcherEventClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on a background thread for every event received.</summary>
    public event EventHandler<WatcherEvent>? Received;

    /// <summary>
    /// Raised when the channel cannot be used at all — the ACL refused this caller, or the
    /// watcher keeps disappearing. The app logs it once instead of spamming the log.
    /// </summary>
    public event Action<string>? Failed;

    /// <summary>True while the pipe is connected.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Starts the read loop; returns immediately and never throws.</summary>
    public Task StartAsync() => Task.Run(async () =>
    {
        try
        {
            await RunAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A background loop must never take the application down with it.
            Failed?.Invoke(ex.Message);
        }
    });

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var deniedReported = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    WatcherPipe.Name,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync((int)WatcherPipe.ConnectTimeout.TotalMilliseconds, cancellationToken)
                    .ConfigureAwait(false);

                IsConnected = true;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (WatcherEvent.FromJson(line) is { } message)
                    {
                        try
                        {
                            Received?.Invoke(this, message);
                        }
                        catch
                        {
                            // A faulty handler must not kill the channel.
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                // The pipe DACL refused this caller — a different account or a different
                // elevation level. Reported once, retried slowly: the watcher may be restarting
                // under the right identity.
                if (!deniedReported)
                {
                    deniedReported = true;
                    Failed?.Invoke($"Access to the watcher pipe was denied: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }
            catch (TimeoutException)
            {
                // The watcher is not up yet; it will be next round.
            }
            catch (IOException)
            {
                // Broken pipe: the watcher restarted or exited.
            }
            finally
            {
                IsConnected = false;
            }

            try
            {
                await Task.Delay(WatcherPipe.ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // Cancelling is enough: the read loop unwinds on its own, and disposing the source
        // straight away would make the pending read throw instead of closing politely.
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }
}
