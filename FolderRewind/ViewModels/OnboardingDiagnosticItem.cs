using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed class OnboardingDiagnosticItem(OnboardingCheck check, OnboardingRepairContext context)
{
    public OnboardingCheck Check { get; } = check;
    public OnboardingRepairContext Context { get; } = context;
    public OnboardingRepairTarget Target => OnboardingRepairPolicy.Resolve(Check.Code);
    public string Display => Check.Code + " · " + I18n.GetString("CheckState_" + Check.State) + "\n" + CloudCommandSecurity.Redact(Check.Message)
        + "\n" + UserDisplayFormatter.LongDateTime(Check.CheckedAtUtc.LocalDateTime);
    public bool CanRepair => Target != OnboardingRepairTarget.None && Check.State != OnboardingCheckState.Ready;
    public string RepairLabel => I18n.GetString("Repair_" + Target);
    public string AutomationId => "OnboardingRepair_" + Check.Code + "_" + Check.CheckedAtUtc.UtcTicks;
}
