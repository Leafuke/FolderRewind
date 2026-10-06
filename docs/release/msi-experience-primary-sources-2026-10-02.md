# MSI 侧载体验：官方资料核对

查阅日期：2026-10-02。范围：WiX 7.0.0、Windows Installer、未打包 WinUI 的安装范围、语言、选项、图标和升级。本文是资料审阅；实际部署结论以本轮安装测试记录为准。

以下“当前源码”描述是初始 1.9.0.0 审阅的快照。修复后使用双范围、Burn 和内嵌中文转换，实际状态见 [1.9.1.0 最终验收](msi-implementation-acceptance-2026-10-02.md)。

## 1. Program Files 安装失败是范围设计问题

当前 `Installer/Package.wxs` 使用 `Scope="perUser"` 和 `LocalAppDataFolder\Programs`。WiX 官方定义：`perUser` 设置 limited 安装权限；`perMachine` 要求提升并设置 `ALLUSERS=1`；`perUserOrMachine` 设置 `ALLUSERS=2`、`MSIINSTALLPERUSER=1`，默认当前用户；WiX 7 新增 `perMachineOrUser`，默认所有用户。[WiX PackageScopeType](https://docs.firegiant.com/wix/schema/wxs/packagescopetype/)

因此允许用户在目录页选择 `C:\Program Files` 并不会自动切换范围或触发正确提升。若只要求安装到 Program Files，最小一致方案是 per-machine 包、对应程序目录和机器级组件。如果提供“仅当前用户 / 所有用户”选项，必须把安装范围、目录、快捷方式及注册表根共同设计，而不是只改一项 Scope。Microsoft 要求双范围包使用相应可重定向目录，并用 Registry Root=-1 实现 HKCU/HKLM 重定向；当前安装路径和快捷方式标记硬编码 HKCU，需要审阅。[Single Package Authoring](https://learn.microsoft.com/en-us/windows/win32/msi/single-package-authoring)、[Installation Context](https://learn.microsoft.com/en-us/windows/win32/msi/installation-context)

`MSIINSTALLPERUSER` 只有 `ALLUSERS=2` 时才有效。所有用户安装需要获得相应权限；双范围包应让用户通过安装界面或命令行选择，并测试普通用户输入另一个管理员账号凭据的情况。不要把“以管理员身份启动应用”当成安装范围修复。[ALLUSERS](https://learn.microsoft.com/en-us/windows/win32/msi/allusers)、[MSIINSTALLPERUSER](https://learn.microsoft.com/en-us/windows/win32/msi/msiinstallperuser)

## 2. 中文界面可由官方资源支持，但不会跟随应用语言自动改变

WiX UI 扩展内置 `zh-CN`。MSBuild 项目通过 `Cultures` 选择语言；包语言也需正确设置，例如简体中文 LCID 2052，或使用本地化 `WixUILanguageId`。只修改应用的本地化资源不会改变 MSI 对话框。要中文错误/进度文字，还需显式引用 `WixUI_ErrorProgressText`。自写的 `DowngradeErrorMessage` 也需本地化。[WiX UI localization](https://docs.firegiant.com/wix/tools/wixext/wixui/#localization)、[Package Language](https://docs.firegiant.com/wix/schema/wxs/package/)

`Cultures=en-US;zh-CN` 构建多个本地化输出，不等价于一个 MSI 运行时自动选择全部语言。应明确交付独立中文版，或另行设计多语言分发/引导程序。

## 3. 默认界面的选项不足，许可证占位内容需处理

`WixUI_InstallDir` 支持选择整体安装目录，不提供功能选择。v7.0.0 官方源明确其新装流程为欢迎、许可证、目录、确认；维护流程只有修复和移除，并设置 `ARPNOMODIFY=1`。因此当前没有安装范围、桌面快捷方式选择以及完成后启动逻辑，是现有 authoring 的结果，而非 MSI 格式限制。[官方 UI 说明](https://docs.firegiant.com/wix/tools/wixext/wixui/)、[v7.0.0 WixUI_InstallDir.wxs](https://github.com/wixtoolset/wix/blob/v7.0.0/src/ext/UI/wixlib/WixUI_InstallDir.wxs)

WiX UI 自带的是 **placeholder license agreement**。当前未覆盖 `WixUILicenseRtf`，应替换为项目真实许可文本，或按官方方法移除许可证页；不能把 WiX 构建工具的许可当作应用许可。[Specifying a license file / Customizing dialogs](https://docs.firegiant.com/wix/tools/wixext/wixui/#specifying-a-license-file)

完成页可用 `WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT` 显示“完成后启动”，但还需 Finish 的 `DoAction` / 启动动作；仅显示 checkbox 不会执行启动。v7 的官方 ExitDialog 对该控件使用 `NOT Installed`，维护/卸载时不会显示。桌面快捷方式应有独立组件/选项，并测试取消选择、修复、升级和卸载行为。[Adding a checkbox](https://docs.firegiant.com/wix/tools/wixext/wixui/#adding-text-or-a-checkbox-to-the-completion-dialog)、[v7.0.0 ExitDialog.wxs](https://github.com/wixtoolset/wix/blob/v7.0.0/src/ext/UI/wixlib/ExitDialog.wxs)

`WixUI_Advanced` 虽包含 per-user / per-machine 选择，但 v7.0.0 官方源顶部明确写有仍在开发、未来不兼容以及 `Use at your own risk`，还有 APPLICATIONFOLDER 的 TODO。其流程使用 `Privileged`、`ALLUSERS` 和 `APPLICATIONFOLDER`，不能认为替换 Id 就能无条件支持当前包。建议基于现有 UI 增加明确选项并验证双范围，或把 Advanced 作为需要测试的候选方案。[v7.0.0 WixUI_Advanced.wxs](https://github.com/wixtoolset/wix/blob/v7.0.0/src/ext/UI/wixlib/WixUI_Advanced.wxs)

## 4. MSI 下有四个独立图标入口

当前源码检索发现 `Assets/logo.ico` 被发布，应用内 `DesktopShortcutService` 引用该图标；但未找到项目 `ApplicationIcon`、主窗口 `AppWindow.SetIcon`、显式 AppUserModelID、安装器 Shortcut.Icon 或 `ARPPRODUCTICON` authoring。仅把 ICO 复制到 Assets 不会让所有 Shell 表面自动使用它。

| 表面 | 对应配置 | 官方依据 |
| --- | --- | --- |
| EXE 在文件管理器中的图标 | 项目 `ApplicationIcon`，编译器对应 `Win32Icon` | [Roslyn MSBuild targets](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/MSBuildTask/Microsoft.CSharp.Core.targets)、[C# Win32Icon](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/resources#win32icon) |
| 运行中的窗口图标 | `AppWindow.SetIcon` | [AppWindow.SetIcon](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.seticon) |
| 开始菜单 / 桌面快捷方式图标 | MSI `Icon` 与 `Shortcut.Icon` / `IconIndex` | [WiX Shortcut](https://docs.firegiant.com/wix/schema/wxs/shortcut/) |
| “已安装的应用”/卸载列表图标 | MSI `ARPPRODUCTICON` 指向 Icon 表主键 | [ARPPRODUCTICON](https://learn.microsoft.com/en-us/windows/win32/msi/arpproducticon) |

WinUI `SetIcon(string)` 只支持 ICO，参数应是完全限定路径；官方建议 .NET 用 `AppContext.BaseDirectory` 拼接。MSI 属于 unpackaged 应用，不能假设 `Package.Current` 一定可用。文档要求图标 Build Action 为 Content，本项目已经如此设置，但还需窗口调用和可靠发布路径。[SetIcon remarks](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.seticon)

## 5. 固定任务栏还需要一致的应用身份

Windows Shell 使用 AppUserModelID 完成任务栏分组及重启。显式进程 ID 应在任何 UI 出现之前调用 `SetCurrentProcessExplicitAppUserModelID` 设置；快捷方式应使用相同的 `System.AppUserModel.ID`。只设置窗口图标不解决“已固定图标与运行窗口分成两组”或升级后的固定启动入口。[Application User Model IDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids)、[SetCurrentProcessExplicitAppUserModelID](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-setcurrentprocessexplicitappusermodelid)、[System.AppUserModel.ID](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)

WiX 的 `ShortcutProperty` 可以写 Shell 属性。建议为 MSI 通道选用稳定 ID，在进程、安装器创建的快捷方式和应用自行创建的快捷方式中保持一致；对 MSIX 则尊重其包身份。验证直接启动 EXE、开始菜单、桌面入口、固定任务栏重启，以及 MSI/MSIX 并存时的分组策略。[WiX ShortcutProperty](https://docs.firegiant.com/wix/schema/wxs/shortcutproperty/)

图标缓存是否需刷新、用户已有的旧固定快捷方式如何迁移，应以实际升级/固定测试决定；缺少 authoring 已有源码证据，但不能据此断言所有视觉异常只有一个原因。

## 6. 改安装范围时必须验证升级与卸载

Microsoft 明确：应用安装后，后续更新、修复和删除保持同一安装上下文。应把旧 per-user 包到新 per-machine 包的迁移作为单独场景验证，不能把改 Scope 当成已验证的无损升级。[Single Package Authoring](https://learn.microsoft.com/en-us/windows/win32/msi/single-package-authoring)

Windows Installer 的 ProductVersion 比较只看前三段。当前版本若保持 `1.9.0.x`，第四段变化不能作为可靠的 major upgrade 识别依据；建议 MSI 发布递增前三段。WiX 的 `AllowSameVersionUpgrades=yes` 也会容许同前三段的“降级”，需明确策略。[ProductVersion](https://learn.microsoft.com/en-us/windows/win32/msi/productversion)、[MajorUpgrade](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/)

当前 `Schedule="afterInstallInitialize"` 会先移除旧版本，但后续安装失败时可回滚并恢复旧版本；这比默认 `afterInstallValidate` 更利于失败恢复，不过仍需真实失败/回滚测试。[MajorUpgrade Schedule](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/)、[RemoveExistingProducts](https://learn.microsoft.com/en-us/windows/win32/msi/removeexistingproducts-action)

另需实测：托盘运行时升级/卸载的文件占用提示；卸载是否移除安装器创建的快捷方式及自启动注册表；修复是否恢复这些入口；用户备份和配置是否按产品策略保留；普通用户在机器安装下能否正常保存配置与更新。它们不能仅通过一次 MSI 构建成功来确认。

## 7. Burn 安装位置独立登记

WiX v7.0.0 的 Burn 引擎在 ARP 登记处仍把 InstallLocation 留作 TODO，不能认为内部 MSI 的 ARPINSTALLLOCATION 会自动填充可见 Bundle 条目。依据：[registration.cpp](https://github.com/wixtoolset/wix/blob/v7.0.0/src/burn/engine/registration.cpp)。本机源码副本与实际 ARP 查询都已保存，最终验收把字段为空列为未通过。

注册表视图核对参考：[Registry Redirector](https://learn.microsoft.com/en-us/windows/win32/winprog64/registry-redirector)、[Registry Keys Affected by WOW64](https://learn.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys)、[RegGetValueW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-reggetvaluew)。本机独立 x86 探针能够读取 Run 值，MSI 清理动作的 query=2 仍需在其真实安全上下文调查，不能仅凭 x86 架构推断已找到根因。
