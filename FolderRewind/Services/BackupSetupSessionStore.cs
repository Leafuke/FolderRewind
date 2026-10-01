using FolderRewind.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class BackupSetupSessionStore
{
    public static string DraftPath => Path.Combine(ConfigService.ConfigDirectory, "onboarding", "backup-setup.v1.json");
    public static async Task SaveAsync(BackupSetupSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
        // 仅显式“稍后继续”保存路径；使用原子文件，草稿不进入正式配置/历史。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, AppJsonContext.Default.BackupSetupSession);
        if (bytes.Length > 256 * 1024 || session.SourcePaths.Count > OnboardingOperationBudgets.DiscoveryCandidates || session.Selections.Count > OnboardingOperationBudgets.DiscoveryCandidates)
            throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        await AtomicFileService.WriteAsync(DraftPath, (stream, token) => stream.WriteAsync(bytes, token).AsTask());
    }
    public static BackupSetupSession? Load()
    {
        if (!File.Exists(DraftPath)) return null;
        if (new FileInfo(DraftPath).Length > 256 * 1024) throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        var session = JsonSerializer.Deserialize(File.ReadAllBytes(DraftPath), AppJsonContext.Default.BackupSetupSession);
        if (session?.SchemaVersion != 1 || session.SourcePaths.Count > OnboardingOperationBudgets.DiscoveryCandidates
            || session.Selections.Count > OnboardingOperationBudgets.DiscoveryCandidates
            || session.DiscoveryReentry?.ResumingSelections.Count > OnboardingOperationBudgets.DiscoveryCandidates)
            throw new InvalidDataException(I18n.GetString("Setup_DraftInvalid"));
        return session;
    }
    public static void Discard() { if (File.Exists(DraftPath)) File.Delete(DraftPath); }
}
