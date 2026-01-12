using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml.Data;
using System;

namespace FolderRewind.Views
{
    public sealed class LogLevelToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not LogLevel level) return string.Empty;

            return level switch
            {
                LogLevel.Info => I18n.GetString("LogLevel_Info"),
                LogLevel.Warning => I18n.GetString("LogLevel_Warning"),
                LogLevel.Error => I18n.GetString("LogLevel_Error"),
                LogLevel.Debug => I18n.GetString("LogLevel_Debug"),
                _ => level.ToString()
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
