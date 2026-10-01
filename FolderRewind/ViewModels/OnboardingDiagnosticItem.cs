using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed class OnboardingDiagnosticItem(OnboardingCheck check, OnboardingRepairContext context)
{
    public OnboardingCheck Check { get; } = check;
    public OnboardingRepairContext Context { get; } = context;
    public OnboardingRepairTarget Target => OnboardingRepairPolicy.Resolve(Check.Code);
    public string ComponentName => I18n.GetString("CheckComponent_" + Check.Code.Replace('.', '_').Replace('-', '_'));
    public string StatusText => I18n.GetString("CheckState_" + Check.State);
    public string Summary => Check.Code.StartsWith("cloud.", System.StringComparison.Ordinal)
        ? I18n.GetString(Check.State == OnboardingCheckState.Ready ? "Check_CloudReady" : "Check_CloudRepair")
        : Check.Code is "minecraft.game-response" or "minecraft.hot-backup" or "minecraft.hot-restore" or "minecraft.game-schedule"
            ? I18n.GetString("CheckSummary_" + Check.Code.Replace('.', '_').Replace('-', '_'))
            : CloudCommandSecurity.Redact(Check.Message);
    public SemanticStatus Status => Check.State switch
    {
        OnboardingCheckState.Ready => SemanticStatus.Success,
        OnboardingCheckState.Blocked => SemanticStatus.Error,
        OnboardingCheckState.NeedsInput => SemanticStatus.Warning,
        _ => SemanticStatus.Neutral
    };
    public string Display => Check.Code + " · " + I18n.GetString("CheckState_" + Check.State) + "\n" + CloudCommandSecurity.Redact(Check.Message)
        + "\n" + UserDisplayFormatter.LongDateTime(Check.CheckedAtUtc.LocalDateTime);
    public bool CanRepair => Target != OnboardingRepairTarget.None && Check.State != OnboardingCheckState.Ready;
    public string RepairLabel => I18n.GetString("Repair_" + Target);
    public string AutomationId => "OnboardingRepair_" + Check.Code + "_" + Check.CheckedAtUtc.UtcTicks;
}
