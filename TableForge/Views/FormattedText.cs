using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TableForge.Domain;

namespace TableForge.Views;

/// <summary>
/// Shows result text with its bold/italic on a TextBlock: <c>views:FormattedText.Segments="{Binding Segments}"</c> in place of
/// <c>Text="{Binding Text}"</c>. Text with no formatting is set as plain <see cref="TextBlock.Text"/>, exactly as before, so a
/// plain table costs and looks the same as it did. With formatting, <see cref="TextBlock.Text"/> is still set to the whole
/// plain text (WPF does not update it from Inlines, and automation and tests read it), then the Inlines are replaced with one
/// Run per segment.
/// </summary>
public static class FormattedText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<FormattedSegment>), typeof(FormattedText), new PropertyMetadata(null, OnSegmentsChanged));

    /// <summary>Whether the TextBlock currently holds formatted Runs rather than its plain Text.</summary>
    private static readonly DependencyProperty HasRunsProperty = DependencyProperty.RegisterAttached(
        "HasRuns", typeof(bool), typeof(FormattedText), new PropertyMetadata(false));

    public static IReadOnlyList<FormattedSegment>? GetSegments(DependencyObject element) =>
        (IReadOnlyList<FormattedSegment>?)element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, IReadOnlyList<FormattedSegment>? value) => element.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        var segments = e.NewValue as IReadOnlyList<FormattedSegment> ?? [];
        var text = string.Concat(segments.Select(s => s.Text));

        if (segments.All(s => s.Style == TextStyle.None))
        {
            // Setting Text to the value it already holds changes nothing, so Runs left from formatting are cleared first.
            if ((bool)block.GetValue(HasRunsProperty))
            {
                block.ClearValue(TextBlock.TextProperty);
                block.SetValue(HasRunsProperty, false);
            }
            block.Text = text;
            return;
        }

        block.Text = text;
        block.Inlines.Clear();
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsBold) run.FontWeight = FontWeights.Bold;
            if (segment.IsItalic) run.FontStyle = FontStyles.Italic;
            block.Inlines.Add(run);
        }
        block.SetValue(HasRunsProperty, true);
    }
}
