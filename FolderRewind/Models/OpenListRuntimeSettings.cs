namespace FolderRewind.Models;

public enum OpenListOwnership { External, ManagedLocal }

public sealed class OpenListRuntimeSettings
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public string ConfigFilePath { get; set; } = string.Empty;
    public string ServiceBaseUri { get; set; } = string.Empty;
    // 意图与本机授权分离；跨设备导入清除授权，路径/二进制/配置变化后重新确认。
    public bool AllowStartOnDemand { get; set; }
    public OpenListOwnership Ownership { get; set; }
    public string AuthorizationMachine { get; set; } = "";
    public string AuthorizationSignature { get; set; } = "";
}
