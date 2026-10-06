param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$RunId,
    [string]$BranchName = ('ui-dialog-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
)
$ErrorActionPreference = 'Stop'
function Invoke-Ui {
    & winapp ui @args
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $args" }
}
# Open History for an isolated test configuration and select the batch view first.
Invoke-Ui invoke "HistoryRunCreateBranch_$RunId" -a $AppPid
Invoke-Ui wait-for AppDialogTextInput -a $AppPid -t 3000
Invoke-Ui set-value AppDialogTextInput "$BranchName-cancelled" -a $AppPid
Invoke-Ui invoke CloseButton -a $AppPid
Invoke-Ui wait-for "HistoryRunCreateBranch_$RunId" -a $AppPid -p IsEnabled --value true -t 5000
Start-Sleep -Milliseconds 500
Invoke-Ui invoke "HistoryRunCreateBranch_$RunId" -a $AppPid
Invoke-Ui wait-for AppDialogTextInput -a $AppPid -t 3000
Invoke-Ui set-value AppDialogTextInput "  $BranchName  " -a $AppPid
Invoke-Ui invoke PrimaryButton -a $AppPid
Invoke-Ui invoke HistoryBranchFilter -a $AppPid
Invoke-Ui wait-for $BranchName -a $AppPid -t 5000
$found = winapp ui search "$BranchName-cancelled" -a $AppPid --json | ConvertFrom-Json
if ($found.matchCount -ne 0) { throw 'Cancelling created a branch.' }
Invoke-Ui send-keys esc -a $AppPid --via send-input
Write-Output "PASS: cancel, consecutive dialogs, trimmed branch creation: $BranchName"
