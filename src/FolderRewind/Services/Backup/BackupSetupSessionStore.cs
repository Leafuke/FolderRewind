using FolderRewind.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class BackupSetupSessionStore
{
    public static string DraftPath => Path.Combine(ConfigService.ConfigDirectory, "onboarding", "backup-setup.v1.json");
    public static bool HasDraft => File.Exists(DraftPath);
    public static async Task SaveAsync(BackupSetupSession session)
        => _ = await SaveWithGenerationAsync(session);
    internal static async Task<string> SaveWithGenerationAsync(BackupSetupSession session)
    {
        session.GenerationId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
        // 仅显式“稍后继续”保存路径；使用原子文件，草稿不进入正式配置/历史。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, AppJsonContext.Default.BackupSetupSession);
        if (bytes.Length > 256 * 1024 || session.SourcePaths.Count > OnboardingOperationBudgets.DiscoveryCandidates || session.Selections.Count > OnboardingOperationBudgets.DiscoveryCandidates)
            throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        return await DraftFileGeneration.SaveAsync(DraftPath, bytes);
    }
    public static BackupSetupSession? Load()
        => Load(out _);
    internal static BackupSetupSession? Load(out string? generation)
    {
        generation = null;
        if (!File.Exists(DraftPath)) return null;
        if (new FileInfo(DraftPath).Length > 256 * 1024) throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        var bytes = File.ReadAllBytes(DraftPath);
        var session = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.BackupSetupSession);
        if (session?.SchemaVersion != 1 || session.SourcePaths.Count > OnboardingOperationBudgets.DiscoveryCandidates
            || session.Selections.Count > OnboardingOperationBudgets.DiscoveryCandidates
            || session.DiscoveryReentry?.ResumingSelections.Count > OnboardingOperationBudgets.DiscoveryCandidates)
            throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        generation = DraftFileGeneration.Token(bytes);
        return session;
    }
    public static void Discard() { if (File.Exists(DraftPath)) File.Delete(DraftPath); }
    internal static Task<bool> DiscardIfCurrentAsync(string? generation) => DraftFileGeneration.DiscardIfCurrentAsync(DraftPath, generation);
}
