param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][ValidateSet('local', 'sandbox')][string]$Target,
    [Parameter(Mandatory)][ValidateSet('local', 'sandbox')][string]$AppScope,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot ('../../artifacts/winui-review-' + [guid]::NewGuid().ToString('N'))),
    [switch]$ContrastThemes
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($AppPid -le 0 -or $AppScope -ne $Target) { throw 'Use a positive PID from the same live target.' }
if ($ContrastThemes -and $Target -ne 'local') { throw 'Contrast palette tests currently require the local target.' }
$scopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$originalTheme = $null
$originalContrast = $null
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$windows = @(& winapp ui list-windows @scopeArgs -a $AppPid --json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Cannot discover app window.' }
$main = $windows | Where-Object { $_.hwnd -and $_.className -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1
if (!$main) { throw 'No live main window.' }
$hwnd = [string]$main.hwnd

function Invoke-Ui([string[]]$Arguments) {
    $output = @(& winapp ui @Arguments @scopeArgs -w $hwnd)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")" }
    $output
}
function Test-UI([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null; $results.Add(@{ name = $Name; status = 'PASS' }) }
    catch { $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" }) }
}
function Flatten($nodes) {
    foreach ($node in $nodes) { if ($null -ne $node) { $node; Flatten $node.children } }
}
function Open-Appearance {
    Invoke-Ui @('invoke', 'SettingsItem') | Out-Null
    $tree = Invoke-Ui @('inspect', 'SettingsPersonalizationHeader', '--ancestors', '--json') | ConvertFrom-Json
    $expander = @(Flatten $tree.windows.elements | Where-Object { $_.className -eq 'Microsoft.UI.Xaml.Controls.Expander' })
    if ($expander.Count -ne 1) { throw 'Cannot uniquely locate appearance expander.' }
    if ($expander[0].expandState -eq 'collapsed') { Invoke-Ui @('invoke', $expander[0].selector) | Out-Null }
    Invoke-Ui @('wait-for', 'SettingsTheme', '-t', '3000') | Out-Null
}
function Select-Theme([string]$Name) {
    Invoke-Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
    Open-Appearance
    Invoke-Ui @('invoke', 'SettingsTheme') | Out-Null
    $popup = Invoke-Ui @('inspect', '--interactive', '--json', '-d', '16') | ConvertFrom-Json
    $selectors = @(Flatten $popup.windows.elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name } |
        Select-Object -ExpandProperty selector -Unique)
    if ($selectors.Count -ne 1) { throw "Cannot uniquely locate theme option '$Name'." }
    Invoke-Ui @('invoke', $selectors[0]) | Out-Null
    Invoke-Ui @('wait-for', 'SettingsTheme', '--value', $Name, '-t', '3000') | Out-Null
    Invoke-Ui @('invoke', 'NavLogs') | Out-Null
    Invoke-Ui @('wait-for', 'LogLiveRefresh', '-t', '3000') | Out-Null
}
function Save-Screenshot([string]$Name) {
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Evidence already exists: $path" }
    Invoke-Ui @('screenshot', '--output', $path, '--json') | Out-Null
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'Screenshot was not delivered.' }
    $screenshots.Add($path)
}

try {
    Open-Appearance
    $originalTheme = (Invoke-Ui @('get-value', 'SettingsTheme', '--json') | ConvertFrom-Json).text
    if (!$originalTheme) { throw 'Cannot capture original theme for restoration.' }
    Invoke-Ui @('invoke', 'NavLogs') | Out-Null
    Test-UI 'Log toggle names and unique identifiers' {
        $live = Invoke-Ui @('get-property', 'LogLiveRefresh', '--json') | ConvertFrom-Json
        $scroll = Invoke-Ui @('get-property', 'LogAutoScroll', '--json') | ConvertFrom-Json
        if (!$live.properties.Name -or !$scroll.properties.Name -or $live.properties.Name -eq $scroll.properties.Name) { throw 'Missing or duplicate toggle names.' }
        if ($live.properties.AutomationId -ne 'LogLiveRefresh' -or $scroll.properties.AutomationId -ne 'LogAutoScroll') { throw 'Unexpected toggle IDs.' }
        @($live, $scroll) | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'toggle-uia.json')
    }
    foreach ($toggle in @('LogLiveRefresh', 'LogAutoScroll')) {
        Test-UI "Keyboard operates $toggle" {
            $before = (Invoke-Ui @('get-property', $toggle, '--json') | ConvertFrom-Json).properties.ToggleState
            $after = if ($before -eq 'On') { 'Off' } else { 'On' }
            try {
                Invoke-Ui @('send-keys', 'space', '--target', $toggle, '--via', 'send-input')
                Invoke-Ui @('wait-for', $toggle, '--value', $after, '-t', '3000')
            } finally {
                $current = (Invoke-Ui @('get-property', $toggle, '--json') | ConvertFrom-Json).properties.ToggleState
                if ($current -ne $before) { Invoke-Ui @('invoke', $toggle) }
            }
        }
    }
    Test-UI 'Cached log page resumes after navigation' {
        Invoke-Ui @('invoke', 'NavHome')
        Invoke-Ui @('invoke', 'NavLogs')
        Invoke-Ui @('wait-for', 'LogEntries', '-t', '3000')
        Save-Screenshot '01-original-logs.png'
    }
    # Names follow the active app language; the app supports English and Chinese.
    $light = if ($originalTheme -match 'Light|Dark|System') { 'Light' } else { '浅色' }
    $dark = if ($originalTheme -match 'Light|Dark|System') { 'Dark' } else { '深色' }
    Test-UI 'Light log presentation' { Select-Theme $light; Save-Screenshot '02-light-logs.png' }
    Test-UI 'Dark log presentation' { Select-Theme $dark; Save-Screenshot '03-dark-logs.png' }

    if ($ContrastThemes) {
        Invoke-Ui @('send-keys', 'esc', '--via', 'send-input') | Out-Null
        Invoke-Ui @('invoke', 'NavLogs') | Out-Null
        # Change only the live contrast palette (no SPIF_UPDATEINIFILE). Always restore
        # original flags/scheme in finally, including when any assertion fails.
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ReviewContrast {
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint Size; public uint Flags; public IntPtr Scheme; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SystemParametersInfo(uint action, uint param, ref State state, uint flags);
    [DllImport("user32.dll")]
    static extern uint GetSysColor(int index);
    public static bool HasLightWindowPalette() {
        var c = GetSysColor(5); // COLOR_WINDOW, returned as COLORREF (BGR).
        return ((c & 255) + ((c >> 8) & 255) + ((c >> 16) & 255)) > 384;
    }
    public static State Get() {
        var state = new State { Size=(uint)Marshal.SizeOf<State>() };
        if (!SystemParametersInfo(0x42, state.Size, ref state, 0)) throw new System.ComponentModel.Win32Exception();
        return state;
    }
    public static void Set(uint flags, string scheme) {
        var ptr = Marshal.StringToHGlobalUni(scheme);
        try {
            var state = new State { Size=(uint)Marshal.SizeOf<State>(), Flags=flags, Scheme=ptr };
            if (!SystemParametersInfo(0x43, state.Size, ref state, 2)) throw new System.ComponentModel.Win32Exception();
        } finally { Marshal.FreeHGlobal(ptr); }
    }
}
'@
        $state = [ReviewContrast]::Get()
        $originalContrast = @{ Flags = $state.Flags; Scheme = [Runtime.InteropServices.Marshal]::PtrToStringUni($state.Scheme) }
        foreach ($palette in @('High Contrast Black', 'High Contrast White')) {
            Test-UI "Live switch to $palette without navigation" {
                [ReviewContrast]::Set(($originalContrast.Flags -bor 1), $palette)
                Start-Sleep -Milliseconds 1200
                if (!([ReviewContrast]::Get().Flags -band 1)) { throw 'High contrast did not activate.' }
                Invoke-Ui @('wait-for', 'LogEntries', '-t', '3000')
                Save-Screenshot ($palette.Replace(' ', '-') + '.png')
                if ([ReviewContrast]::HasLightWindowPalette() -ne ($palette -eq 'High Contrast White')) {
                    throw "Windows enabled contrast but did not apply the requested $palette palette; visual coverage is incomplete."
                }
            }
        }
        Test-UI 'Live contrast exit restores normal presentation' {
            [ReviewContrast]::Set($originalContrast.Flags, $originalContrast.Scheme)
            Start-Sleep -Milliseconds 1200
            Save-Screenshot '04-contrast-restored.png'
        }
    }
} catch {
    $results.Add(@{ name = 'UI setup'; status = 'FAIL'; detail = "$_" })
} finally {
    Test-UI 'Restore original contrast state' {
        if ($originalContrast) { [ReviewContrast]::Set($originalContrast.Flags, $originalContrast.Scheme) }
    }
    Test-UI 'Restore original app theme' { if ($originalTheme) { Select-Theme $originalTheme } }
    try { & winapp ui yield @scopeArgs | Out-Null }
    finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId }
}
$failed = @($results | Where-Object status -eq 'FAIL').Count
$report = @{ target = $Target; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed; results = $results.ToArray(); screenshots = $screenshots.ToArray() }
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'test-results.json') -Encoding utf8
$report | ConvertTo-Json -Depth 8
if ($failed) { exit 1 }
