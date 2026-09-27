using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TableForge.ViewModels;

namespace TableForge.Views;

public partial class RollView : UserControl
{
    public RollView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private int _stepCount = 1;

    /// <summary>Export… opens its menu under the button, by mouse or keyboard alike.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        ExportMenu.PlacementTarget = ExportButton;
        ExportMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ExportMenu.IsOpen = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
        if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;

        // This view is reused when another table is opened, so start that table at its top, ready to roll.
        TrailScroller.ScrollToTop();
        _stepCount = (e.NewValue as RollViewModel)?.Steps.Count ?? 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RollButton.Focus());
    }

    /// <summary>A new roll or a followed link changes <see cref="RollViewModel.Results"/>; bring the new content into view.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RollViewModel.Results))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToLatest); // after the new visuals have been laid out
    }

    /// <summary>
    /// Scrolls so the newest roll (or, right after following a link, the new step) starts at the top of the view,
    /// unless that would push the Roll controls out of sight, in which case the controls stay visible.
    /// Older steps are left exactly as they are.
    /// </summary>
    private void ScrollToLatest()
    {
        if (DataContext is not RollViewModel vm || vm.Steps.Count == 0) return;
        UpdateLayout();

        // After following a link, focus goes to Roll for the new table. After an ordinary roll it stays where it was,
        // so someone typing several manual rolls in a row keeps their cursor in the box.
        if (vm.Steps.Count != _stepCount)
        {
            _stepCount = vm.Steps.Count;
            RollButton.Focus();
        }

        var step = vm.Current;
        FrameworkElement? stepElement = FindContainer(StepsList, step);
        var anchor = step.Outcomes.Count > 0 && stepElement is not null
            ? FindContainer(stepElement, step.Outcomes[^1]) ?? stepElement
            : stepElement;
        if (anchor is null) return;

        var anchorTop = TopWithin(anchor, TrailContent);
        var controlsBottom = TopWithin(RollControls, TrailContent) + RollControls.ActualHeight + TrailContent.Margin.Bottom;
        var offset = Math.Max(anchorTop, controlsBottom - TrailScroller.ViewportHeight);
        TrailScroller.ScrollToVerticalOffset(Math.Max(0, offset));
    }

    private static double TopWithin(UIElement element, UIElement ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new Point(0, 0)).Y;

    /// <summary>Finds the generated container (the element wrapping one item) below <paramref name="root"/>.</summary>
    private static FrameworkElement? FindContainer(DependencyObject root, object item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContentPresenter presenter && ReferenceEquals(presenter.Content, item)) return presenter;
            if (FindContainer(child, item) is { } found) return found;
        }
        return null;
    }
}
