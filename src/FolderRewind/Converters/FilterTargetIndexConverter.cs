using FolderRewind.Plugin.Abstractions;
using Microsoft.UI.Xaml.Data;
using System;

namespace FolderRewind.Converters;

public sealed class FilterTargetIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is PathFilterTarget target ? (int)target : -1;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
