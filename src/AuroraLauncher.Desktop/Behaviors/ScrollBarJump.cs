using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AuroraLauncher.Desktop.Behaviors;

/// <summary>Attached behavior: clicking (or dragging on) the scroll bar track
/// jumps the thumb straight to the clicked position instead of paging.</summary>
public static class ScrollBarJump
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollBarJump),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar sb) return;
        if ((bool)e.NewValue)
        {
            sb.PreviewMouseLeftButtonDown += OnPreviewDown;
            sb.PreviewMouseMove += OnPreviewMove;
            sb.PreviewMouseLeftButtonUp += OnPreviewUp;
        }
        else
        {
            sb.PreviewMouseLeftButtonDown -= OnPreviewDown;
            sb.PreviewMouseMove -= OnPreviewMove;
            sb.PreviewMouseLeftButtonUp -= OnPreviewUp;
        }
    }

    private static bool _dragging;

    private static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        // OriginalSource is the inner Border of the thumb (not the Thumb itself),
        // so walk the visual ancestors — grabbing the thumb must keep the native
        // smooth drag instead of jumping to the click position.
        if (IsWithinThumb(e.OriginalSource as DependencyObject)) return;
        var sb = (ScrollBar)sender;
        if (!TryGetValueAt(sb, e, out var value)) return;

        _dragging = true;
        sb.CaptureMouse();
        ApplyValue(sb, value);
        e.Handled = true;
    }

    private static bool IsWithinThumb(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Thumb) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>Drives the owning ScrollViewer DIRECTLY. Setting ScrollBar.Value
    /// alone only moves the thumb — the ScrollViewer does not scroll content in
    /// response to programmatic value changes, which is exactly the
    /// "thumb jumps, content stays" bug.</summary>
    private static void ApplyValue(ScrollBar sb, double value)
    {
        value = Math.Clamp(value, sb.Minimum, sb.Maximum);
        if (sb.TemplatedParent is ScrollViewer sv)
        {
            if (sb.Orientation == Orientation.Vertical) sv.ScrollToVerticalOffset(value);
            else sv.ScrollToHorizontalOffset(value);
        }
        else
        {
            sb.Value = value; // standalone scrollbar: no owner to drive
        }
    }

    private static void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var sb = (ScrollBar)sender;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(sb); return; }
        if (TryGetValueAt(sb, e, out var value)) ApplyValue(sb, value);
        e.Handled = true;
    }

    private static void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) EndDrag((ScrollBar)sender);
    }

    private static void EndDrag(ScrollBar sb)
    {
        _dragging = false;
        if (sb.IsMouseCaptured) sb.ReleaseMouseCapture();
    }

    /// <summary>Maps the mouse position onto the track value range.
    /// Note: the vertical template sets IsDirectionReversed=True, so top = min.</summary>
    private static bool TryGetValueAt(ScrollBar sb, MouseEventArgs e, out double value)
    {
        value = 0;
        var track = FindTrack(sb);
        if (track == null || track.ActualHeight <= 0 && track.ActualWidth <= 0) return false;

        double ratio;
        if (sb.Orientation == Orientation.Vertical)
        {
            var pos = e.GetPosition(track);
            ratio = pos.Y / track.ActualHeight;
        }
        else
        {
            var pos = e.GetPosition(track);
            ratio = pos.X / track.ActualWidth;
        }

        ratio = Math.Clamp(ratio, 0, 1);
        value = sb.Minimum + ratio * (sb.Maximum - sb.Minimum);
        return true;
    }

    private static Track? FindTrack(DependencyObject d)
    {
        if (d is Track t) return t;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var found = FindTrack(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            if (found != null) return found;
        }
        return null;
    }
}
