namespace FolderRewind.Models;

public sealed class OpenListRuntimeSettings
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public string ConfigFilePath { get; set; } = string.Empty;
    public string ServiceBaseUri { get; set; } = string.Empty;
    // 仅保存用户意图，不能作为本机执行授权。本轮未通过占用预验证时始终不启动。
    public bool AllowStartOnDemand { get; set; }
}
