using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AvatarDeck.UI.Behaviors;

/// <summary>
/// Scroll a <see cref="ScrollViewer"/> by dragging its content (click-and-drag vertically), like a touch screen.
/// This is how the VR laser scrolls — no thumbstick involved. A drag that starts on a button cancels the
/// button's click (pointer capture moves to the scroll viewer), so tiles never toggle by accident while scrolling.
/// </summary>
public static class DragScroll
{
    /// <summary>Vertical movement (DIP) before a press becomes a scroll.</summary>
    private const double Threshold = 20;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(DragScroll));

    private static readonly AttachedProperty<DragState?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, DragState?>("State", typeof(DragScroll));

    static DragScroll() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            viewer.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        else
        {
            viewer.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            viewer.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            viewer.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        }
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is ScrollViewer viewer && e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed)
        {
            viewer.SetValue(StateProperty, new DragState(e.GetPosition(viewer), viewer.Offset));
        }
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.GetValue(StateProperty) is not { } state)
        {
            return;
        }

        var position = e.GetPosition(viewer);
        var dy = state.Start.Y - position.Y;
        var dx = state.Start.X - position.X;
        if (!state.Dragging)
        {
            // Mostly-vertical moves only, so horizontal slider drags keep working.
            if (Math.Abs(dy) < Threshold || Math.Abs(dy) < Math.Abs(dx) * 1.5)
            {
                return;
            }

            state.Dragging = true;
            e.Pointer.Capture(viewer); // cancels the pressed button: no accidental click
        }

        viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, state.StartOffset.Y + dy));
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.GetValue(StateProperty) is not { } state)
        {
            return;
        }

        viewer.SetValue(StateProperty, null);
        if (state.Dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private sealed class DragState(Point start, Vector startOffset)
    {
        public Point Start { get; } = start;

        public Vector StartOffset { get; } = startOffset;

        public bool Dragging { get; set; }
    }
}
