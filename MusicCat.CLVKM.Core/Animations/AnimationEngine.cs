using System;
using System.Threading;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Interfaces;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

public class AnimationEngine
{
    private readonly IKeyboardController _controller;

    /// <summary>Serialises driver writes so a stale loop can never interleave with a new one.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private IAnimation? _currentAnimation;

    /// <summary>
    /// Colour and deadline of the transient flash (typing, a Windows notification). The
    /// fields are read from the animation loop, so they are touched through Interlocked and
    /// a volatile read; a frame torn in the middle would cost one odd colour, nothing else.
    /// </summary>
    private ColorRGB _flashColor;
    private long _flashUntilTicks;

    /// <summary>Incremented on every Start; loops from older generations exit immediately.</summary>
    private int _generation;

    private volatile bool _isRunning;
    private volatile bool _isPaused;

    public bool IsRunning => _isRunning;

    public bool IsPaused => _isPaused;

    public event Action<ColorRGB, Zone>? FrameApplied;

    public float Brightness { get; set; } = 1.0f;

    public double Speed { get; set; } = 1.0;

    public AnimationEngine(IKeyboardController controller)
    {
        _controller = controller;
    }

    public void Start(IAnimation animation)
    {
        // Fully detach the previous loop before the new one starts.
        StopCore();

        _currentAnimation = animation;
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        var generation = ++_generation;

        _isRunning = true;

        _ = Task.Run(() => RunLoopAsync(animation, token, generation));
    }

    public void Stop()
    {
        _isRunning = false;
        _isPaused = false;
        StopCore();
    }

    /// <summary>Freezes the loop without disposing the animation (sleep timer).</summary>
    public void Pause() => _isPaused = true;

    /// <summary>Resumes a paused loop.</summary>
    public void Resume() => _isPaused = false;

    /// <summary>
    /// Paints over the running effect for a moment — the keypress and notification flashes.
    /// The frames keep flowing, so the effect comes back by itself when the window expires
    /// and nothing has to be restarted.
    /// </summary>
    public void Flash(ColorRGB color, TimeSpan duration)
    {
        _flashColor = color;
        Interlocked.Exchange(ref _flashUntilTicks, Environment.TickCount64 + (long)duration.TotalMilliseconds);
    }

    /// <summary>Ends a flash early, for example while the app is shutting down.</summary>
    public void ClearFlash() => Interlocked.Exchange(ref _flashUntilTicks, 0L);

    private bool IsFlashing() => Environment.TickCount64 < Volatile.Read(ref _flashUntilTicks);

    private void StopCore()
    {
        _cts?.Cancel();
        _cts = null;

        var running = Interlocked.Exchange(ref _currentAnimation, null);
        if (running is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // A faulty Dispose must never break switching effects.
            }
        }
    }

    private async Task RunLoopAsync(IAnimation animation, CancellationToken token, int generation)
    {
        try
        {
            while (!token.IsCancellationRequested && IsCurrent(generation))
            {
                foreach (var frame in animation.GenerateFrames())
                {
                    if (token.IsCancellationRequested || !IsCurrent(generation))
                    {
                        return;
                    }

                    while (_isPaused && !token.IsCancellationRequested && IsCurrent(generation))
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested || !IsCurrent(generation))
                    {
                        return;
                    }

                    // Apply global brightness.
                    var finalColor = frame.Color.ApplyBrightness(Brightness);

                    // A flash (a keypress, a toast) paints over the effect for a moment.
                    if (IsFlashing())
                    {
                        finalColor = _flashColor;
                    }

                    await _writeLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (token.IsCancellationRequested || !IsCurrent(generation))
                        {
                            return;
                        }

                        await _controller.SetColorAsync(frame.TargetZone, finalColor).ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }

                    FrameApplied?.Invoke(finalColor, frame.TargetZone);

                    if (frame.DurationMs > 0)
                    {
                        // Higher speed = shorter delay; never below 10 ms.
                        int delay = (int)(frame.DurationMs / Speed);
                        if (delay < 10)
                        {
                            delay = 10;
                        }

                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop / switch.
        }
        catch (Exception)
        {
            // Never crash the app because of one faulty animation.
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _isRunning = false;
            }
        }
    }

    private bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);
}
