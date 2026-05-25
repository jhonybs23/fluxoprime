using System.Globalization;

namespace FluxoPrimeMaui.Converters;

public class ImageUrlConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var url = value?.ToString();
        if (string.IsNullOrWhiteSpace(url))
            return ImageSource.FromFile("appiconfg.svg");
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return ImageSource.FromUri(new Uri(url));
        return ImageSource.FromFile(url);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
