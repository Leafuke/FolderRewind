using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace FolderRewind.Converters;

public sealed class AutomationIdConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var prefix = parameter?.ToString()?.Trim() ?? string.Empty;
        var identity = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return string.IsNullOrWhiteSpace(identity)
            ? prefix
            : $"{prefix}_{identity.Trim()}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
