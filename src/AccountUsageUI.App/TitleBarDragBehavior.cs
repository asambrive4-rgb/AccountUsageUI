using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AccountUsageUI.App;

public static class TitleBarDragBehavior
{
    public static readonly DependencyProperty EnableWindowDragProperty =
        DependencyProperty.RegisterAttached(
            "EnableWindowDrag",
            typeof(bool),
            typeof(TitleBarDragBehavior),
            new PropertyMetadata(false, OnEnableWindowDragChanged));

    public static bool GetEnableWindowDrag(DependencyObject element) =>
        (bool)element.GetValue(EnableWindowDragProperty);

    public static void SetEnableWindowDrag(DependencyObject element, bool value) =>
        element.SetValue(EnableWindowDragProperty, value);

    private static void OnEnableWindowDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.MouseLeftButtonDown += OnMouseLeftButtonDown;
        }
        else
        {
            element.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveElement(source))
        {
            return;
        }

        if (sender is DependencyObject element && Window.GetWindow(element) is Window window)
        {
            if (e.ClickCount == 2 && window.ResizeMode == ResizeMode.CanResize)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }

            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore if mouse button was released before DragMove completed.
            }
        }
    }

    private static bool IsInteractiveElement(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ButtonBase or System.Windows.Controls.TextBox)
            {
                return true;
            }

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
