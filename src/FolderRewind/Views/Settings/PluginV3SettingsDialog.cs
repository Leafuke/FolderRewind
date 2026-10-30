using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Settings;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderRewind.Views.Settings;

internal sealed class PluginV3SettingsDialog : ContentDialog
{
    private readonly PluginId _pluginId;
    private readonly PluginSettingsSchema _schema;
    private readonly Dictionary<string, JsonElement> _current;
    private readonly Dictionary<string, Func<JsonElement>> _getters = new(StringComparer.Ordinal);
    private readonly TextBlock _validation;

    public PluginV3SettingsDialog(string pluginName, PluginV3SettingsEditorData data, XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(data);
        _pluginId = data.Settings.PluginId;
        _schema = data.Schema;
        _current = data.Settings.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);

        Title = I18n.Format("Plugins_SettingsDialogTitle", pluginName);
        PrimaryButtonText = I18n.GetString("Common_Save");
        CloseButtonText = I18n.GetString("Common_Cancel");
        DefaultButton = ContentDialogButton.Primary;
        XamlRoot = xamlRoot;

        _validation = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(_validation);
        foreach (var definition in _schema.Settings)
            panel.Children.Add(CreateSettingEditor(definition));

        Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Closing += OnClosing;
        ThemeService.ApplyThemeToDialog(this);
    }

    public IReadOnlyDictionary<string, JsonElement>? ResultSettings { get; private set; }

    private FrameworkElement CreateSettingEditor(PluginSettingSchemaDefinition definition)
    {
        var displayName = I18n.PickBest(
            definition.LocalizedDisplayName,
            definition.DisplayName) ?? definition.Key;
        var description = I18n.PickBest(
            definition.LocalizedDescription,
            definition.Description) ?? string.Empty;
        var initial = _current.TryGetValue(definition.Key, out var persisted)
            ? persisted
            : definition.DefaultValue;

        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(new TextBlock
        {
            Text = displayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            group.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }

        var editor = CreateEditor(definition, initial, displayName);
        group.Children.Add(editor);
        return group;
    }

    private Control CreateEditor(
        PluginSettingSchemaDefinition definition,
        JsonElement? initial,
        string accessibleName)
    {
        Control editor;
        switch (definition.Type)
        {
            case PluginSettingValueType.Boolean:
                var toggle = new ToggleSwitch
                {
                    IsOn = initial.HasValue && initial.Value.ValueKind == JsonValueKind.True
                };
                _getters[definition.Key] = () => JsonSerializer.SerializeToElement(toggle.IsOn);
                editor = toggle;
                break;

            case PluginSettingValueType.Integer:
                var number = new NumberBox
                {
                    Value = initial.HasValue && initial.Value.TryGetInt64(out var integer)
                        ? integer
                        : 0,
                    Minimum = long.MinValue,
                    Maximum = long.MaxValue,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
                };
                _getters[definition.Key] = () => JsonSerializer.SerializeToElement(
                    NormalizeInteger(number.Value));
                editor = number;
                break;

            case PluginSettingValueType.Enum:
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var value in definition.EnumValues) combo.Items.Add(value);
                var selected = initial.HasValue && initial.Value.ValueKind == JsonValueKind.String
                    ? initial.Value.GetString()
                    : definition.EnumValues.FirstOrDefault();
                combo.SelectedItem = definition.EnumValues.FirstOrDefault(value =>
                    string.Equals(value, selected, StringComparison.Ordinal));
                combo.SelectedIndex = combo.SelectedIndex < 0 && combo.Items.Count > 0 ? 0 : combo.SelectedIndex;
                _getters[definition.Key] = () => JsonSerializer.SerializeToElement(
                    combo.SelectedItem as string ?? string.Empty);
                editor = combo;
                break;

            default:
                var text = new TextBox
                {
                    Text = initial.HasValue && initial.Value.ValueKind == JsonValueKind.String
                        ? initial.Value.GetString() ?? string.Empty
                        : string.Empty,
                    PlaceholderText = definition.Required ? I18n.GetString("Common_Required") : string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = definition.Type == PluginSettingValueType.Multiline,
                    MinHeight = definition.Type == PluginSettingValueType.Multiline ? 120 : 0
                };
                if (definition.Type == PluginSettingValueType.Multiline) text.MaxHeight = 260;
                _getters[definition.Key] = () => JsonSerializer.SerializeToElement(text.Text ?? string.Empty);
                editor = text;
                break;
        }

        AutomationProperties.SetName(editor, accessibleName);
        AutomationProperties.SetAutomationId(editor, $"PluginSetting_{SanitizeAutomationId(definition.Key)}");
        return editor;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary) return;

        var candidate = _current.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        foreach (var definition in _schema.Settings)
        {
            if (!_getters.TryGetValue(definition.Key, out var getter)) continue;
            var value = getter();
            if (definition.Required
                && value.ValueKind == JsonValueKind.String
                && string.IsNullOrWhiteSpace(value.GetString()))
            {
                var displayName = I18n.PickBest(
                    definition.LocalizedDisplayName,
                    definition.DisplayName) ?? definition.Key;
                _validation.Text = I18n.Format("Plugins_SettingsMissingRequired", displayName);
                args.Cancel = true;
                return;
            }
            candidate[definition.Key] = value;
        }

        var validation = _schema.Validate(new PluginSettingsSnapshot(_pluginId, candidate));
        if (!validation.IsValid)
        {
            _validation.Text = I18n.Format(
                "Plugins_SettingsValidationFailed",
                string.Join(", ", validation.Issues
                    .Where(issue => issue.Severity == DiagnosticSeverity.Error)
                    .Select(issue => issue.Key)));
            args.Cancel = true;
            return;
        }

        _validation.Text = string.Empty;
        ResultSettings = validation.NormalizedSettings.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string SanitizeAutomationId(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    // NumberBox 使用 double 承载数值；在转回 schema 的 Int64 前必须显式处理边界，
    // 避免极值经过浮点舍入后触发溢出或产生平台相关结果。
    private static long NormalizeInteger(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= long.MaxValue) return long.MaxValue;
        if (value <= long.MinValue) return long.MinValue;
        return Convert.ToInt64(Math.Round(value));
    }
}
