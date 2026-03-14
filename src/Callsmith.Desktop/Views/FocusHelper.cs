using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Callsmith.Desktop.Views;

/// <summary>
/// Provides an attached property that automatically focuses a <see cref="TextBox"/>
/// and selects all its text the moment it transitions from hidden to visible.
/// Used for the inline rename TextBox in the collections sidebar tree.
/// </summary>
/// <example>
/// <code>
/// &lt;TextBox views:FocusHelper.FocusOnVisible="True" .../&gt;
/// </code>
/// </example>
public static class FocusHelper
{
    public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>(
            "FocusOnVisible", typeof(FocusHelper), defaultValue: false);

    public static bool GetFocusOnVisible(TextBox tb) => tb.GetValue(FocusOnVisibleProperty);
    public static void SetFocusOnVisible(TextBox tb, bool value) => tb.SetValue(FocusOnVisibleProperty, value);

    static FocusHelper()
    {
        FocusOnVisibleProperty.Changed.AddClassHandler<TextBox>((tb, args) =>
        {
            if ((bool)args.NewValue! == true)
            {
                // Subscribe to IsVisible changes on this TextBox.
                tb.GetObservable(Visual.IsVisibleProperty).Subscribe(new RelayObserver<bool>(isVisible =>
                {
                    if (isVisible)
                        Dispatcher.UIThread.Post(() =>
                        {
                            tb.Focus();
                            tb.SelectAll();
                        }, DispatcherPriority.Input);
                }));
            }
        });
    }

    // Minimal IObserver adapter so we can use a lambda without a method group.
    private sealed class RelayObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
