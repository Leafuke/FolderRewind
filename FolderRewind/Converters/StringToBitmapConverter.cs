using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace FolderRewind.Converters
{
    public class StringToBitmapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string path && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    // 使用流读取，防止文件被锁死
                    using (var stream = File.OpenRead(path))
                    {
                        var bitmap = new BitmapImage();
                        // 必须拷贝到内存流，因为 BitmapImage 需要随机访问且我们要释放原文件句柄
                        var memStream = new MemoryStream();
                        stream.CopyTo(memStream);
                        memStream.Position = 0;

                        bitmap.SetSource(memStream.AsRandomAccessStream());
                        return bitmap;
                    }
                }
                catch
                {
                    return null; // 加载失败显示默认图标
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}