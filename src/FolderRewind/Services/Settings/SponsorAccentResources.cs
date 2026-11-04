using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace FolderRewind.Services;

/// <summary>Owns the accent palette and the control aliases that WinUI resolves with StaticResource.</summary>
internal sealed class SponsorAccentResources : ResourceDictionary
{
    private enum Shade { Accent, Light1, Light2, Light3, Dark1, Dark2, Dark3 }

    private static readonly string[] ColorKeys =
    [
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"
    ];

    private readonly ResourceDictionary _light = new();
    private readonly ResourceDictionary _dark = new();
    private readonly List<(SolidColorBrush Brush, Shade Shade)> _solidBrushes = new();
    private readonly List<(AcrylicBrush Brush, Shade Shade)> _acrylicBrushes = new();

    internal SponsorAccentResources(Color accent)
    {
        ThemeDictionaries["Light"] = _light;
        ThemeDictionaries["Dark"] = _dark;
        // Let the platform supply all high-contrast resources.
        ThemeDictionaries["HighContrast"] = new ResourceDictionary();

        for (var i = 0; i < ColorKeys.Length; i++)
            AddSolid((Shade)i, (Shade)i, [ColorKeys[i] + "Brush"]);
        AddSolid(Shade.Accent, Shade.Accent, ["SystemColorControlAccentBrush"]);

        // Lightweight resource aliases from Windows App SDK 2.6.5 generic.xaml.
        // Overriding only AccentFillColorDefaultBrush leaves these cached aliases on the system palette.
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlBackgroundAccentBrush",
            "JumpListDefaultEnabledBackground",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlDisabledAccentBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlRevealFocusVisualBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlForegroundAccentBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAccentBrush",
            "TopNavigationViewItemRevealBackgroundFocused",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAltAccentBrush",
            "PivotHeaderItemFocusPipeFill",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAltListAccentHighBrush",
        ], lightOpacity: 0.7, darkOpacity: 0.9);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAltListAccentLowBrush",
        ], lightOpacity: 0.4, darkOpacity: 0.6);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAltListAccentMediumBrush",
        ], lightOpacity: 0.6, darkOpacity: 0.8);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightListAccentHighBrush",
            "ListBoxItemBackgroundSelectedPressed",
        ], lightOpacity: 0.7, darkOpacity: 0.9);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightListAccentLowBrush",
            "ListBoxItemSelectedBackgroundThemeBrush",
            "ListBoxItemBackgroundSelected",
        ], lightOpacity: 0.4, darkOpacity: 0.6);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightListAccentMediumBrush",
            "ListBoxItemBackgroundSelectedPointerOver",
        ], lightOpacity: 0.6, darkOpacity: 0.8);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHyperlinkTextBrush",
            "HubSectionHeaderButtonForeground",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "ContentDialogBorderThemeBrush",
        ]);
        AddAcrylic(Shade.Accent, Shade.Accent,
        [
            "SystemControlAccentAcrylicWindowAccentMediumHighBrush",
        ], tintOpacity: 0.7, lightLuminosity: null, darkLuminosity: null);
        AddAcrylic(Shade.Accent, Shade.Accent,
        [
            "SystemControlAccentAcrylicElementAccentMediumHighBrush",
        ], tintOpacity: 0.7, lightLuminosity: null, darkLuminosity: null);
        AddAcrylic(Shade.Dark1, Shade.Dark1,
        [
            "SystemControlAccentDark1AcrylicWindowAccentDark1Brush",
        ], tintOpacity: 0.8, lightLuminosity: null, darkLuminosity: null);
        AddAcrylic(Shade.Dark1, Shade.Dark1,
        [
            "SystemControlAccentDark1AcrylicElementAccentDark1Brush",
        ], tintOpacity: 0.8, lightLuminosity: null, darkLuminosity: null);
        AddAcrylic(Shade.Dark2, Shade.Dark2,
        [
            "SystemControlAccentDark2AcrylicWindowAccentDark2MediumHighBrush",
        ], tintOpacity: 0.7, lightLuminosity: null, darkLuminosity: null);
        AddAcrylic(Shade.Dark2, Shade.Dark2,
        [
            "SystemControlAccentDark2AcrylicElementAccentDark2MediumHighBrush",
        ], tintOpacity: 0.7, lightLuminosity: null, darkLuminosity: null);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlBackgroundAccentRevealBorderBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAccentRevealBorderBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightAccentRevealBackgroundBrush",
            "ToggleButtonRevealBackgroundChecked",
            "ToggleButtonRevealBackgroundCheckedPointerOver",
            "GridViewItemRevealBackgroundSelected",
            "MenuFlyoutSubItemRevealBackgroundPressed",
        ]);
        AddSolid(Shade.Light3, Shade.Dark3,
        [
            "SystemControlHighlightAccent3RevealBackgroundBrush",
            "ListViewItemRevealBackgroundSelected",
            "ComboBoxItemRevealBackgroundSelected",
            "ComboBoxItemRevealBackgroundSelectedUnfocused",
        ]);
        AddSolid(Shade.Light3, Shade.Dark3,
        [
            "SystemControlHighlightAccent3RevealAccent2BackgroundBrush",
            "ListViewItemRevealBackgroundSelectedPressed",
            "GridViewItemRevealBackgroundSelectedPressed",
            "ComboBoxItemRevealBackgroundSelectedPressed",
        ]);
        AddSolid(Shade.Light2, Shade.Dark2,
        [
            "SystemControlHighlightAccent2RevealBackgroundBrush",
            "ListViewItemRevealBackgroundSelectedPointerOver",
            "GridViewItemRevealBackgroundSelectedPointerOver",
            "ComboBoxItemRevealBackgroundSelectedPointerOver",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightListAccentVeryHighBrush",
        ], lightOpacity: 0.9, darkOpacity: 0.9);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "SystemControlHighlightListAccentMediumLowBrush",
        ], lightOpacity: 0.75, darkOpacity: 0.75);
        AddSolid(Shade.Dark2, Shade.Light3,
        [
            "AccentTextFillColorPrimaryBrush",
            "HyperlinkButtonForeground",
            "CalendarViewSelectedForeground",
            "HyperlinkForeground",
            "InkToolbarAccentColorForegroundThemeBrush",
            "CalendarViewSelectedHoverForeground",
            "InfoBarHyperlinkButtonForeground",
        ]);
        AddSolid(Shade.Dark3, Shade.Light3,
        [
            "AccentTextFillColorSecondaryBrush",
            "HyperlinkButtonForegroundPointerOver",
            "HyperlinkForegroundPointerOver",
            "InkToolbarAccentHoverColorForegroundThemeBrush",
        ]);
        AddSolid(Shade.Dark1, Shade.Light2,
        [
            "AccentTextFillColorTertiaryBrush",
            "HyperlinkButtonForegroundPressed",
            "HyperlinkForegroundPressed",
            "CalendarViewSelectedPressedForeground",
        ]);
        AddSolid(Shade.Accent, Shade.Accent,
        [
            "AccentFillColorSelectedTextBackgroundBrush",
            "TextControlSelectionHighlightColor",
        ]);
        AddSolid(Shade.Dark1, Shade.Light2,
        [
            "AccentFillColorDefaultBrush",
            "SliderThumbBackground",
            "SliderTrackValueFill",
            "RadioButtonOuterEllipseCheckedStroke",
            "RadioButtonOuterEllipseCheckedFill",
            "CheckBoxCheckBackgroundStrokeChecked",
            "CheckBoxCheckBackgroundStrokeIndeterminate",
            "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundFillIndeterminate",
            "ToggleSwitchFillOn",
            "ToggleSwitchStrokeOn",
            "ToggleButtonBackgroundChecked",
            "AccentButtonBackground",
            "CalendarViewSelectedBorderBrush",
            "PivotHeaderItemSelectedPipeFill",
            "RatingControlSelectedForeground",
            "RatingControlPointerOverSelectedForeground",
            "NavigationViewSelectionIndicatorForeground",
            "PersonPictureEllipseBadgeFillThemeBrush",
            "AppBarToggleButtonBackgroundChecked",
            "SwipeItemPostThresholdExecuteBackground",
            "SplitButtonBackgroundChecked",
            "InkToolbarToggleButtonBackgroundChecked",
            "GridViewItemSelectedBorderBrush",
            "GridViewItemCheckBoxSelectedBrush",
            "ListViewItemMultiArrangeOverlayTextBackground",
            "ListViewItemCheckBoxSelectedBrush",
            "ListViewItemSelectionIndicatorBrush",
            "ListViewItemSelectionIndicatorPointerOverBrush",
            "ListViewItemSelectionIndicatorPressedBrush",
            "CalendarViewTodayBackground",
            "InfoBadgeBackground",
            "CommandBarFlyoutAppBarButtonBackgroundChecked",
            "TreeViewItemSelectionIndicatorForeground",
            "TreeViewItemSelectionIndicatorForegroundPointerOver",
            "TreeViewItemSelectionIndicatorForegroundPressed",
            "ComboBoxItemPillFillBrush",
            "ProgressBarForeground",
            "ProgressRingForegroundThemeBrush",
            "PagerControlSelectionIndicatorForeground",
            "ItemContainerSelectionVisualBackground",
            "ItemContainerSelectionVisualPointerOverBackground",
            "ItemContainerSelectionVisualPressedBackground",
            "VerticalThumbBrush",
            "SelectorBarItemPillFill",
        ]);
        AddSolid(Shade.Dark1, Shade.Light2,
        [
            "AccentFillColorSecondaryBrush",
            "SliderThumbBackgroundPointerOver",
            "SliderTrackValueFillPointerOver",
            "RadioButtonOuterEllipseCheckedStrokePointerOver",
            "RadioButtonOuterEllipseCheckedFillPointerOver",
            "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver",
            "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
            "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchStrokeOnPointerOver",
            "ToggleButtonBackgroundCheckedPointerOver",
            "AccentButtonBackgroundPointerOver",
            "CalendarViewSelectedHoverBorderBrush",
            "AppBarToggleButtonBackgroundCheckedPointerOver",
            "SplitButtonBackgroundCheckedPointerOver",
            "InkToolbarToggleButtonBackgroundCheckedPointerOver",
            "GridViewItemSelectedPointerOverBorderBrush",
            "GridViewItemCheckBoxSelectedPointerOverBrush",
            "ListViewItemCheckBoxSelectedPointerOverBrush",
            "CalendarViewTodayHoverBackground",
            "CommandBarFlyoutAppBarButtonBackgroundCheckedPointerOver",
        ], lightOpacity: 0.9, darkOpacity: 0.9);
        AddSolid(Shade.Dark1, Shade.Light2,
        [
            "AccentFillColorTertiaryBrush",
            "SliderThumbBackgroundPressed",
            "SliderTrackValueFillPressed",
            "RadioButtonOuterEllipseCheckedStrokePressed",
            "RadioButtonOuterEllipseCheckedFillPressed",
            "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "CheckBoxCheckBackgroundStrokeIndeterminatePressed",
            "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundFillIndeterminatePressed",
            "ToggleSwitchFillOnPressed",
            "ToggleSwitchStrokeOnPressed",
            "ToggleButtonBackgroundCheckedPressed",
            "AccentButtonBackgroundPressed",
            "AppBarToggleButtonBackgroundCheckedPressed",
            "SplitButtonBackgroundCheckedPressed",
            "InkToolbarToggleButtonBackgroundCheckedPressed",
            "GridViewItemSelectedPressedBorderBrush",
            "GridViewItemCheckBoxSelectedPressedBrush",
            "ListViewItemCheckBoxSelectedPressedBrush",
            "CalendarViewTodayBlackoutBackground",
            "CalendarViewTodayPressedBackground",
            "CommandBarFlyoutAppBarButtonBackgroundCheckedPressed",
        ], lightOpacity: 0.8, darkOpacity: 0.8);
        AddSolid(Shade.Accent, Shade.Light2,
        [
            "SystemFillColorAttentionBrush",
            "InfoBarInformationalSeverityIconBackground",
        ]);
        AddSolid(Shade.Dark1, Shade.Light2,
        [
            "AccentAAFillColorDefaultBrush",
            "DatePickerFlyoutPresenterHighlightFill",
            "TimePickerFlyoutPresenterHighlightFill",
        ]);
        AddSolid(Shade.Dark2, Shade.Light3,
        [
            "AccentAAFillColorSecondaryBrush",
        ]);
        AddSolid(Shade.Accent, Shade.Light1,
        [
            "AccentAAFillColorTertiaryBrush",
        ]);
        AddAcrylic(Shade.Light3, Shade.Dark1,
        [
            "AccentAcrylicBackgroundFillColorDefaultBrush",
        ], tintOpacity: 0.8, lightLuminosity: 0.9, darkLuminosity: 0.8);
        AddAcrylic(Shade.Light3, Shade.Dark1,
        [
            "AccentAcrylicInAppFillColorDefaultBrush",
        ], tintOpacity: 0.8, lightLuminosity: 0.9, darkLuminosity: 0.8);
        AddAcrylic(Shade.Light3, Shade.Dark2,
        [
            "AccentAcrylicBackgroundFillColorBaseBrush",
        ], tintOpacity: 0.8, lightLuminosity: 0.9, darkLuminosity: 0.8);
        AddAcrylic(Shade.Light3, Shade.Dark2,
        [
            "AccentAcrylicInAppFillColorBaseBrush",
        ], tintOpacity: 0.8, lightLuminosity: 0.9, darkLuminosity: 0.8);

        // Calendar focus uses an accent only in the dark SDK palette.
        _dark["CalendarViewFocusBorderBrush"] = _dark["AccentFillColorSecondaryBrush"];

        Update(accent);
    }

    internal void Update(Color accent)
    {
        Color[] colors =
        [
            accent,
            Blend(accent, Colors.White, 0.18), Blend(accent, Colors.White, 0.36), Blend(accent, Colors.White, 0.54),
            Blend(accent, Colors.Black, 0.14), Blend(accent, Colors.Black, 0.28), Blend(accent, Colors.Black, 0.42)
        ];
        for (var i = 0; i < ColorKeys.Length; i++)
            this[ColorKeys[i]] = colors[i];
        this["SystemColorControlAccentColor"] = accent;

        // Keep brush identities stable for controls and code that already hold a reference.
        foreach (var (brush, shade) in _solidBrushes)
            brush.Color = colors[(int)shade];
        foreach (var (brush, shade) in _acrylicBrushes)
        {
            brush.TintColor = colors[(int)shade];
            brush.FallbackColor = colors[(int)shade];
        }
    }

    private void AddSolid(Shade lightShade, Shade darkShade, string[] keys,
        double lightOpacity = 1, double darkOpacity = 1)
    {
        var light = new SolidColorBrush { Opacity = lightOpacity };
        var dark = new SolidColorBrush { Opacity = darkOpacity };
        _solidBrushes.Add((light, lightShade));
        _solidBrushes.Add((dark, darkShade));
        foreach (var key in keys)
        {
            _light[key] = light;
            _dark[key] = dark;
        }
    }

    private void AddAcrylic(Shade lightShade, Shade darkShade, string[] keys, double tintOpacity,
        double? lightLuminosity, double? darkLuminosity)
    {
        var light = new AcrylicBrush { TintOpacity = tintOpacity, TintLuminosityOpacity = lightLuminosity };
        var dark = new AcrylicBrush { TintOpacity = tintOpacity, TintLuminosityOpacity = darkLuminosity };
        _acrylicBrushes.Add((light, lightShade));
        _acrylicBrushes.Add((dark, darkShade));
        foreach (var key in keys)
        {
            _light[key] = light;
            _dark[key] = dark;
        }
    }

    private static Color Blend(Color color, Color target, double amount)
        => Color.FromArgb(color.A,
            (byte)Math.Round(color.R + (target.R - color.R) * amount),
            (byte)Math.Round(color.G + (target.G - color.G) * amount),
            (byte)Math.Round(color.B + (target.B - color.B) * amount));
}
