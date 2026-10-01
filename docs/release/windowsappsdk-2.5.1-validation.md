# Windows App SDK 2.5.1 升级验证

2026-10-02，Windows x64，.NET SDK 10.0.401，WinApp CLI 0.7.0。
本次完成依赖升级、构建和发布检查及简单界面冒烟；没有执行性能基准或正式发布。

## 依赖与必要修复

| 包 | 升级后版本 |
| --- | --- |
| Microsoft.WindowsAppSDK | 2.5.1 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 |
| Microsoft.WindowsAppSDK.AI | 2.5.5 |
| Microsoft.WindowsAppSDK.ML | 2.1.94 |
| Microsoft.WindowsAppSDK.Search | 2.5.5，新增排除资产引用 |
| Microsoft.Data.Sqlite | 10.0.12，应用与测试同步 |
| System.Security.Cryptography.ProtectedData | 10.0.12 |

保留四项 XAML 优化、原有覆盖开关及历史属性名，默认启用条件改为 SDK 2.3.1 及以上。
AI、ML、Search 延续 `ExcludeAssets="all"` 和 `PrivateAssets="all"`；其余可选组件及底层 MachineLearning 引用不变。
MSI 自包含配置和合并 PRI 覆盖、验证逻辑保持不变。

严格构建发现 40 处固定 TextBlock 标签引用触发 WUI2011。
这些 `LabeledBy` 引用显式标注 `Mode=OneTime`，保持原来默认行为，并同步现有无障碍测试断言；没有关闭分析器或降低警告要求。
MSI 检查发现插件项目 PDB 随发布进入目录，因此给现有 CI 的 MSI 发布命令补上全局 `DebugType=None`、`DebugSymbols=false`，与 MSIX 参数保持一致。

## 验证结果

- 解决方案 x64 依赖还原通过，无 NU1605 或依赖冲突。
- x64 Debug 解决方案构建通过。随后对最终应用代码分别执行 Debug、Release `/warnaserror` 构建，均为 0 警告、0 错误。
- `FolderRewind.Tests`：700 通过、6 按既有外部环境条件跳过、0 失败。标签绑定调整后另运行相关无障碍测试，4 通过。
- 测试项目仍有 7 条现有源码警告（CS8602、MSTEST0037）；没有修改测试工具版本或屏蔽这些警告。
- MSBuild 属性检查确认：默认四项优化启用；显式关闭、自定义列表有效；SDK 2.2.0 不自动启用，2.3.1 仍启用。
- x64 未签名 MSIX 生成成功；MSI 自包含发布使用 `/warnaserror` 通过。
- 现有 `Prepare-MsiPackage.ps1` 验证合并 PRI 中的 WinUI 主题资源并生成 MSI、SHA-256 文件，构建为 0 警告、0 错误。
- 最终 MSI 发布目录共 483 个文件、246,297,685 字节；检查未发现未使用的 AI、ML、Search、Widgets、DWrite 可选组件资产及 PDB。
- 发布目录 EXE 正常打开主窗口。现有 UI 检查脚本报告 8 项通过，包括日志键盘操作、页面导航和浅色/深色主题。
- 临时数据备份和还原通过，修改后的文件恢复到备份前 SHA-256；还原对话框取消不修改源文件。
- 文件夹选择器确认、取消及返回后的键盘焦点检查通过。确认采用实际鼠标点击后，主窗口恢复焦点，Tab 导航正常。
- 导航栏开关、最大化/还原、跨 1120 DIP 阈值缩放、MiniWindow 打开/关闭、设置滚动和字号输入完成冒烟检查。
- 本地冒烟使用临时配置，退出时验证原配置逐字节恢复。

## 验证边界与观察

Windows Sandbox 未启用，因此使用本地 UI 检查。没有安装验证包、导入证书、执行 MSI 安装升级卸载或 ARM64 设备测试；这些发布流程继续由现有 CI 覆盖。
文件选择、保存选择器和长历史列表未单独遍历；没有新增测试矩阵。

托盘点击和菜单交互未完成验证：UI 工具无法可靠激活任务栏隐藏图标窗口，不将其记为通过。
截图复核发现日志工具栏自动滚动开关存在局部裁切；没有为这次升级改动布局，也没有将其判定为 SDK 回归。
启动日志出现插件迁移信任哈希错误；源目录与发布目录的插件文件哈希均匹配各自 SHA-256 旁文件。本次未处理插件迁移问题。

详细日志、TRX、截图、临时检查脚本和安装包保存在忽略目录
`artifacts/windowsappsdk-2.5.1-validation/`，不随源码提交。
应用发行版本、公共 API、配置格式和数据库结构未改动。
