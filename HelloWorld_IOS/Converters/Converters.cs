using System.Globalization;

namespace HelloWorld_IOS.Converters;

public sealed class ActiveTabBgConverter : IValueConverter
{
    private static readonly Color Active = Color.FromArgb("#3c7adc");
    private static readonly Color Inactive = Color.FromArgb("#383838");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Active : Inactive;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProgressConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int pct ? Math.Clamp(pct, 0, 100) / 100.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
