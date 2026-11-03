using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WallTerm.Rendering;

/// <summary>
/// OLED burn-in protection. Every few minutes the text canvas drifts to a new offset 1–3 physical pixels away
/// (within ±WigglePixels of the origin) using a slow eased glide, so prompts and static text never sit on the
/// same sub-pixels for long. Only a transform animates; row contents are not re-rendered.
/// </summary>
internal sealed class BurnInGuard
{
    private readonly FrameworkElement _target;
    private readonly TranslateTransform _transform = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private bool _enabled, _paused;
    private int _minSeconds = 180, _maxSeconds = 300, _maxOffset = 3;
    private int _px, _py; // current offset in physical pixels

    public BurnInGuard(FrameworkElement target)
    {
        _target = target;
        _target.RenderTransform = _transform;
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Step();
            Schedule();
        };
    }

    public void Configure(bool enabled, int minSeconds, int maxSeconds, int maxOffsetPixels)
    {
        _minSeconds = minSeconds;
        _maxSeconds = Math.Max(minSeconds, maxSeconds);
        _maxOffset = Math.Max(1, maxOffsetPixels);
        _enabled = enabled;
        if (!enabled && (_px != 0 || _py != 0))
        {
            _px = _py = 0;
            Animate();
        }
        else if (Math.Abs(_px) > _maxOffset || Math.Abs(_py) > _maxOffset)
        {
            _px = Math.Clamp(_px, -_maxOffset, _maxOffset);
            _py = Math.Clamp(_py, -_maxOffset, _maxOffset);
            Animate();
        }
        Schedule();
    }

    /// <summary>Paused while the display is off or the session is locked.</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        Schedule();
    }

    /// <summary>Re-applies the current offset after a DPI change (offsets are in physical pixels).</summary>
    public void Refresh() => Animate(immediate: true);

    private void Schedule()
    {
        _timer.Stop();
        if (!_enabled || _paused) return;
        _timer.Interval = TimeSpan.FromSeconds(_rng.Next(_minSeconds, _maxSeconds + 1));
        _timer.Start();
    }

    private void Step()
    {
        // Pick a new point inside the ±max box whose distance from the current one is 1–3 px on the larger axis.
        for (int attempt = 0; attempt < 64; attempt++)
        {
            int nx = _rng.Next(-_maxOffset, _maxOffset + 1);
            int ny = _rng.Next(-_maxOffset, _maxOffset + 1);
            int step = Math.Max(Math.Abs(nx - _px), Math.Abs(ny - _py));
            if (step is >= 1 and <= 3)
            {
                _px = nx;
                _py = ny;
                break;
            }
        }
        Animate();
    }

    private void Animate(bool immediate = false)
    {
        double ppd = VisualTreeHelper.GetDpi(_target).PixelsPerDip;
        double x = _px / ppd, y = _py / ppd;
        if (immediate)
        {
            _transform.BeginAnimation(TranslateTransform.XProperty, null);
            _transform.BeginAnimation(TranslateTransform.YProperty, null);
            _transform.X = x;
            _transform.Y = y;
            return;
        }
        var duration = TimeSpan.FromSeconds(4);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var ax = new DoubleAnimation(x, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        var ay = new DoubleAnimation(y, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        // A few-pixel glide needs very few frames; keep the GPU/CPU cost of the animation tiny.
        Timeline.SetDesiredFrameRate(ax, 20);
        Timeline.SetDesiredFrameRate(ay, 20);
        _transform.BeginAnimation(TranslateTransform.XProperty, ax);
        _transform.BeginAnimation(TranslateTransform.YProperty, ay);
    }
}
