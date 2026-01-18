using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services.Hotkeys
{
    public enum HotkeyTrigger
    {
        Shortcut = 0,
        GlobalHotkey = 1,
    }

    public sealed class HotkeyInvokedEventArgs : EventArgs
    {
        public string HotkeyId { get; init; } = string.Empty;
        public HotkeyTrigger Trigger { get; init; }
    }

    public static class HotkeyManager
    {
        public const string Action_BackupSelectedFolder = "core.backup.selected";

        private static readonly object _lock = new();

        private static Window? _window;
        private static UIElement? _root;
        private static NativeHotkeyService? _native;

        private static readonly Dictionary<string, HotkeyDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<HotkeyTrigger, Task>> _handlers = new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<KeyboardAccelerator> _installedAccelerators = new();

        public static event EventHandler<HotkeyInvokedEventArgs>? Invoked;
        public static event EventHandler? DefinitionsChanged;

        public static void Initialize(Window window, UIElement rootElement)
        {
            lock (_lock)
            {
                _window = window;
                _root = rootElement;

                try
                {
                    _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
                }
                catch
                {
                }

                _native ??= new NativeHotkeyService(window);
                _native.Hook();

                EnsureCoreDefinitionsRegistered();
                ApplyBindingsToUiAndNative();
            }
        }

        public static void EnsureCoreDefinitionsRegistered()
        {
            lock (_lock)
            {
                if (_definitions.ContainsKey(Action_BackupSelectedFolder)) return;

                RegisterDefinitions(new[]
                {
                    new HotkeyDefinition
                    {
                        Id = Action_BackupSelectedFolder,
                        DisplayName = I18n.GetString("Hotkeys_BackupSelected_DisplayName"),
                        Description = I18n.GetString("Hotkeys_BackupSelected_Description"),
                        DefaultGesture = "Ctrl+S",
                        Scope = HotkeyScope.Shortcut,
                    },
                });
            }
        }

        public static void RegisterDefinitions(IEnumerable<HotkeyDefinition> definitions)
        {
            if (definitions == null) return;

            bool changed = false;
            lock (_lock)
            {
                foreach (var def in definitions)
                {
                    if (def == null) continue;
                    if (string.IsNullOrWhiteSpace(def.Id)) continue;

                    _definitions[def.Id] = def;
                    changed = true;
                }
            }

            if (changed)
            {
                DefinitionsChanged?.Invoke(null, EventArgs.Empty);
                ApplyBindingsToUiAndNative();
            }
        }

        public static void RegisterHandler(string hotkeyId, Func<HotkeyTrigger, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(hotkeyId) || handler == null) return;
            lock (_lock)
            {
                _handlers[hotkeyId] = handler;
            }
        }

        public static IReadOnlyList<HotkeyDefinition> GetDefinitionsSnapshot()
        {
            lock (_lock)
            {
                return _definitions.Values
                    .OrderBy(d => d.OwnerPluginId == null ? 0 : 1)
                    .ThenBy(d => d.OwnerPluginName)
                    .ThenBy(d => d.DisplayName)
                    .ToList();
            }
        }

        public static string GetEffectiveGestureString(string hotkeyId)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Hotkeys;
            if (settings?.Bindings != null && settings.Bindings.TryGetValue(hotkeyId, out var value))
            {
                return value ?? string.Empty;
            }

            lock (_lock)
            {
                if (_definitions.TryGetValue(hotkeyId, out var def))
                {
                    return def.DefaultGesture ?? string.Empty;
                }
            }

            return string.Empty;
        }

        public static void SetGestureOverride(string hotkeyId, string? gestureString)
        {
            var global = ConfigService.CurrentConfig?.GlobalSettings;
            if (global == null) return;
            if (global.Hotkeys == null) global.Hotkeys = new HotkeySettings();
            global.Hotkeys.Bindings ??= new Dictionary<string, string>();

            global.Hotkeys.Bindings[hotkeyId] = gestureString ?? string.Empty;
            ConfigService.Save();
            ApplyBindingsToUiAndNative();
        }

        public static void ResetGestureOverride(string hotkeyId)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Hotkeys;
            if (settings?.Bindings == null) return;
            if (settings.Bindings.Remove(hotkeyId))
            {
                ConfigService.Save();
                ApplyBindingsToUiAndNative();
            }
        }

        public static void ApplyBindingsToUiAndNative()
        {
            lock (_lock)
            {
                if (_root == null) return;

                // Remove previously installed accelerators
                foreach (var acc in _installedAccelerators)
                {
                    try { _root.KeyboardAccelerators.Remove(acc); } catch { }
                }
                _installedAccelerators.Clear();

                _native?.ClearAll();

                // ��ͻ����
                var usedShortcut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var usedGlobal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var def in _definitions.Values)
                {
                    var effective = GetEffectiveGestureString(def.Id);
                    if (string.IsNullOrWhiteSpace(effective)) continue; // disabled

                    if (!HotkeyGesture.TryParse(effective, out var gesture))
                    {
                        LogService.Log(I18n.Format("Hotkeys_InvalidGestureSkipped", def.Id, effective));
                        continue;
                    }

                    if (def.Scope == HotkeyScope.Shortcut)
                    {
                        var key = gesture.ToString();
                        if (usedShortcut.TryGetValue(key, out var existing))
                        {
                            LogService.Log(I18n.Format("Hotkeys_ConflictShortcut", key, existing, def.Id));
                            continue;
                        }

                        usedShortcut[key] = def.Id;

                        var acc = new KeyboardAccelerator
                        {
                            Key = gesture.Key,
                            Modifiers = gesture.ToVirtualKeyModifiers(),
                        };

                        acc.Invoked += async (_, e) =>
                        {
                            e.Handled = true;
                            await InvokeAsync(def.Id, HotkeyTrigger.Shortcut);
                        };

                        _root.KeyboardAccelerators.Add(acc);
                        _installedAccelerators.Add(acc);
                    }
                    else
                    {
                        var key = gesture.ToString();
                        if (usedGlobal.TryGetValue(key, out var existing))
                        {
                            LogService.Log(I18n.Format("Hotkeys_ConflictGlobal", key, existing, def.Id));
                            continue;
                        }

                        usedGlobal[key] = def.Id;

                        _native?.RegisterOrUpdate(def.Id, gesture, () =>
                        {
                            _ = InvokeAsync(def.Id, HotkeyTrigger.GlobalHotkey);
                            return true;
                        });
                    }
                }
            }
        }

        public static async Task InvokeAsync(string hotkeyId, HotkeyTrigger trigger)
        {
            try
            {
                Invoked?.Invoke(null, new HotkeyInvokedEventArgs { HotkeyId = hotkeyId, Trigger = trigger });

                Func<HotkeyTrigger, Task>? handler = null;
                lock (_lock)
                {
                    _handlers.TryGetValue(hotkeyId, out handler);
                }

                if (handler != null)
                {
                    await handler(trigger);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("Hotkeys_InvokeFailed", ex.Message), nameof(HotkeyManager), ex);
            }
        }

        public static void RegisterPluginHotkeys(PluginInstallManifest manifest, IFolderRewindPlugin instance)
        {
            if (manifest == null || instance == null) return;

            if (instance is IFolderRewindHotkeyProvider provider)
            {
                try
                {
                    var defs = provider.GetHotkeyDefinitions() ?? Array.Empty<PluginHotkeyDefinition>();
                    var normalized = defs
                        .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id))
                        .Select(d => new HotkeyDefinition
                        {
                            Id = $"plugin.{manifest.Id}.{d.Id}",
                            DisplayName = d.DisplayName ?? d.Id,
                            Description = d.Description,
                            DefaultGesture = d.DefaultGesture ?? string.Empty,
                            Scope = d.IsGlobalHotkey ? HotkeyScope.GlobalHotkey : HotkeyScope.Shortcut,
                            OwnerPluginId = manifest.Id,
                            OwnerPluginName = manifest.Name,
                        })
                        .ToList();

                    RegisterDefinitions(normalized);

                    foreach (var d in defs)
                    {
                        if (d == null || string.IsNullOrWhiteSpace(d.Id)) continue;
                        var fullId = $"plugin.{manifest.Id}.{d.Id}";
                        RegisterHandler(fullId, async trig =>
                        {
                            var settings = PluginService.GetPluginSettings(manifest.Id);
                            var ctx = PluginHostContext.CreateForCurrentApp(manifest.Id, manifest.Name);
                            await provider.OnHotkeyInvokedAsync(d.Id, trig == HotkeyTrigger.GlobalHotkey, settings, ctx);
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("Hotkeys_PluginRegisterFailed", manifest.Id, ex.Message), nameof(HotkeyManager), ex);
                }
            }
        }
    }
}
