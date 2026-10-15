using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Services;

namespace FolderRewind.Models;

// ArchiveSettings 本身使用生产源码；只替换与本测试无关的 WinUI 配置根。
public enum BackupMode { Full = 0, Smart = 1, Rolling = 2 }
public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonExtensionData] public Dictionary<string, JsonElement> SchemaExtensions { get; set; } = new();
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!ConfigMutationProtection.Set(this, ref field, value)) return false;
        PropertyChanged?.Invoke(this, new(propertyName)); return true;
    }
}
