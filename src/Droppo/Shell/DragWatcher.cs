using System;
using System.Windows.Threading;

namespace Droppo.Shell;

/// <summary>
/// Watches the mouse and the trigger key system-wide (polling, no hooks, so it can't slow your PC).
/// "Mouse held + moved a bit + trigger key held" = show the wheel.
/// The wheel itself then confirms it's really a file drag (it only lights up if Windows hands it files).
/// </summary>
internal sealed class DragWatcher
{
    private const int DragThresholdPx = 6;
    private const int KeyReleaseGraceMs = 150;

    private readonly DispatcherTimer _timer;
    private bool _mouseDown;
    private bool _dragging;
    private bool _wheelShown;
    private bool _suppressed;
    private NativeMethods.POINT _pressPos;
    private DateTime? _keyUpSince;

    /// <summary>Trigger key went down during a drag. Argument: cursor position in physical pixels.</summary>
    public event Action<NativeMethods.POINT>? TriggerPressed;

    /// <summary>Trigger key released before dropping: user changed their mind.</summary>
    public event Action? TriggerReleased;

    /// <summary>Mouse button released while the wheel was up.</summary>
    public event Action? DragEnded;

    public Func<int> TriggerKey { get; set; } = () => NativeMethods.VK_SHIFT;

    public DragWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>Ignore the rest of this drag (it wasn't a file drag, or the drop already happened).</summary>
    public void Suppress()
    {
        _suppressed = true;
        _wheelShown = false;
        _keyUpSince = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var pos = NativeMethods.CursorPos();

        if (!NativeMethods.IsPrimaryButtonDown())
        {
            if (_wheelShown) DragEnded?.Invoke();
            _mouseDown = _dragging = _wheelShown = _suppressed = false;
            _keyUpSince = null;
            return;
        }

        if (!_mouseDown)
        {
            _mouseDown = true;
            _pressPos = pos;
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(pos.X - _pressPos.X) <= DragThresholdPx && Math.Abs(pos.Y - _pressPos.Y) <= DragThresholdPx) return;
            _dragging = true;
        }

        if (_suppressed) return;

        if (NativeMethods.IsKeyDown(TriggerKey()))
        {
            _keyUpSince = null;
            if (!_wheelShown)
            {
                _wheelShown = true;
                TriggerPressed?.Invoke(pos);
            }
        }
        else if (_wheelShown)
        {
            _keyUpSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _keyUpSince.Value).TotalMilliseconds > KeyReleaseGraceMs)
            {
                _wheelShown = false;
                _keyUpSince = null;
                TriggerReleased?.Invoke();
            }
        }
    }
}
