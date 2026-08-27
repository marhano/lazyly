using System.Globalization;
using System.Windows.Data;
using PublishTool.Core;

namespace PublishTool.Gui;

/// <summary>Formats a UTC <see cref="DateTimeOffset"/> (or nullable) as Philippine time for display
/// in a XAML binding -- see <see cref="PhTime"/>. Registered as an Application-level resource (see
/// App.xaml) so any window/dialog can reference it by key without a per-file declaration.</summary>
public sealed class UtcToPhTimeConverter : IValueConverter
{
    // A bound DateTimeOffset? boxes as either null or a plain boxed DateTimeOffset -- never a boxed
    // "DateTimeOffset?" -- so the DateTimeOffset case below already covers both source types.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset dto => dto.ToPhTime().ToString("yyyy-MM-dd hh:mm:ss tt"),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
