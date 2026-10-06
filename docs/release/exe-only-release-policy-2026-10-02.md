# GitHub Release 仅发布 Setup EXE

用户要求：仅发布 EXE 安装包，取消独立 MSI 发行。本次没有执行远程发布。

公开附件固定为每个版本的以下四个文件：

- `FolderRewind_<version>_Setup_x64.exe`
- `FolderRewind_<version>_Setup_x64.exe.sha256`
- `FolderRewind_<version>_Setup_arm64.exe`
- `FolderRewind_<version>_Setup_arm64.exe.sha256`

Burn 仍需构建并嵌入内部 MSI。MSI 产物和资源门禁结果保留在 `artifacts/installer-packages/`，用于构建和安装验收；仅 `Prepare-SetupReleaseAssets.ps1` 选出的 EXE 和校验文件进入公开目录。MSIX 保留未签名构建回归，不作为此 GitHub 工作流的发行附件，工作流不再依赖 MSIX 侧载证书。

发布脚本在访问 GitHub 前检查完整的 x64/ARM64 文件集合、版本名称和 SHA-256。混入 MSI/MSIX/7z、错误架构、缺少包或无效校验文件时阻止发布。目标 Release 已有不符合本版本名单的附件时也拒绝混合发布，不自动删除历史附件。

客户端按 OS 架构选择 Setup EXE；没有对应 EXE 时打开 Release 页面，取消 MSI 回退。内部安装通道和更新 action 数值保留，避免扩大本次变更。Store 更新继续走 Store；既有打包侧载客户端在 Release 没有 7z 资产时打开 Release 页面。旧版只寻找 MSI 的客户端需要用户前往 Release 页面下载安装 EXE。

校验：`Test-SetupReleaseAssets.ps1` 的 13 项策略测试通过，覆盖真实脚本暂存、漏入 MSI/MSIX/7z、错误架构、缺少 ARM64、坏哈希、旧文件污染，以及发布前校验和已有 Release 附件校验。GitHub CLI 在测试中模拟，没有连接账号或上传附件。结果在 `artifacts/exe-only-release-20261002/policy/results.json`。x64 未打包应用严格构建通过，0 警告、0 错误；工作流 YAML 和公开附件路由、PowerShell 语法检查通过。未执行远程 CI，也未重新部署应用。

原 MSI 安装验收失败项仍见 [安装体验报告](msi-implementation-acceptance-2026-10-02.md)。改变公开文件格式不改变内嵌安装引擎的验收结果。
