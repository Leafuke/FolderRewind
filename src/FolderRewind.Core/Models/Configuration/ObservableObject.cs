using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Services;

namespace FolderRewind.Models
{
    // 基础通知类，省去每个类都写一遍 PropertyChanged
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonExtensionData]
        public Dictionary<string, JsonElement> SchemaExtensions { get; set; } = new();

        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!ConfigMutationProtection.Set(this, ref field, value)) return false;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

}
