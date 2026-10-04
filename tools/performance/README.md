# Local startup performance measurements

`Measure-WindowsAppSdk231.ps1` measures the SDK version currently declared by the app.
Pass `-HistoricalSdkMatrix` explicitly to publish the historical 2.2.0/2.3.1 variants.

Window activation, home-page Loaded, and history warmup completion have separate
monotonic timestamps measured from program entry. Home Loaded is not proof of a
presented frame. `AppReadyMs` remains an alias for `HomeLoadedMs` in older summaries.
A null `HistoryWarmupMs` means background warmup did not finish within the sampling
window; it does not mean zero elapsed time. Raw CSV fields retain these values.
No result is used as a performance gate or CI timing assertion.

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

The comparison is deliberately lightweight. Compare repeated medians with the same fixture and machine. Performance-neutral optional changes are acceptable
when the smoke checks pass.
