using System.Globalization;
using System.Windows.Data;

namespace TableForge.Views;

/// <summary>
/// The width of each result-set column in an aligned table (Roll and Edit screens): the columns share the scroll area's viewport
/// equally once its fixed parts are taken out, but never go below <see cref="MinWidth"/> — then the area scrolls sideways instead.
/// Headers and cells bind through the same instance, so a heading is always exactly as wide as its column.
/// Values: [0] the viewport width of the scroll area the columns are in, [1] the number of result sets.
/// </summary>
public sealed class AlignedColumnWidth : IMultiValueConverter
{
    public const double MinWidth = 230;

    /// <summary>Width in each row that is not a result-set column: row number, range, padding, delete button.</summary>
    public double Fixed { get; set; }

    /// <summary>Space each column takes beside its own width (its margin).</summary>
    public double Gap { get; set; }

    /// <summary>
    /// The column width for this viewport. Rounded down to whole pixels, so the columns never add up to more than the viewport
    /// and a horizontal scrollbar only ever appears when the minimum is reached.
    /// </summary>
    public static double For(double viewportWidth, int sets, double fixedWidth, double gap) =>
        sets <= 0 || double.IsNaN(viewportWidth) || viewportWidth <= 0
            ? MinWidth
            : Math.Max(MinWidth, Math.Floor((viewportWidth - fixedWidth) / sets - gap));

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double viewport, int sets, ..] ? For(viewport, sets, Fixed, Gap) : MinWidth;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
