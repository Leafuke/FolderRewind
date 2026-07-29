# Windows App SDK 2.3.1 local performance check

`Measure-WindowsAppSdk231.ps1` performs the lightweight local comparison used
for the Windows App SDK 2.3.1 upgrade. It publishes and measures exactly these
configurations:

1. Windows App SDK 2.2.0 baseline
2. Windows App SDK 2.3.1 with no optional XAML changes
3. Each of the four optional changes enabled by itself
4. All four optional changes enabled together

It intentionally does not test two-change or three-change combinations.

## Run

Exit every running FolderRewind instance and make sure file logging is enabled
in the app settings. Then run from the repository root:

```powershell
.\tools\performance\Measure-WindowsAppSdk231.ps1
```

The default is one warm-up and five measured launches per configuration. To
reuse payloads from a previous successful run:

```powershell
.\tools\performance\Measure-WindowsAppSdk231.ps1 -SkipBuild
```

Published payloads and CSV/Markdown results are written below
`artifacts/performance`, which is excluded from source control.

## Manual smoke check

For each single-change payload, check startup, the home page, navigation to the
settings page, scrolling, navigation back, and exit. For the all-changes
payload, also check:

- settings/history list scrollbars;
- NavigationView, tray, Mini window, and dynamic icon alignment;
- ContentDialog, ComboBox, and dynamically created TextBlock content;
- context menus and copy behavior on selectable text;
- light/dark theme and font changes.

The comparison is deliberately lightweight. Investigate a repeatable startup
median regression above 5%. Performance-neutral optional changes are acceptable
when the smoke checks pass.
