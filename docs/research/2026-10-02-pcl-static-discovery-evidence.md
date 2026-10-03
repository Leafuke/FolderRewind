# PCL2 静态 Minecraft 存档发现：上游证据与小型辅助方案

> 实施修订：用户提供的真实注册表中没有 LaunchFolders，仅有 CacheDownloadFolder 这一目录线索。现行实施以目录列表和缓存线索组合定位，并独立读取 PCLCE 的共享 JSON；定位建议已按 [执行计划](../plans/MineRewind_Launcher_Discovery_Execution_Plan.md) 更新。

研究日期：2026-10-02。目的：为 MineRewind 的小型发现辅助核实 PCL2 静态定位方式。按用户修正，首发目标包含官方 Java、HMCL、PCL2、Prism、Modrinth、网易和 Bedrock；本报告只进一步核实 PCL2，不沿用此前推迟部分启动器的范围建议。不引入 NBT、存档健康验证、全盘搜索或复杂路径兼容要求。

初次研究仅读取公开上游源码、官方仓库 issue；实施验收另外只读了本机目录相关字段并扫描已知游戏目录，没有读取账号字段。公开源码下载到 `C:/Users/admin/AppData/Local/Temp/FolderRewind-PclResearch-20261002`。PCL 固定源码为 [Meloong-Git/PCL commit 0e0d12fdce6a2804916fb2be60e41144da637c18](https://github.com/Meloong-Git/PCL/tree/0e0d12fdce6a2804916fb2be60e41144da637c18)。旧 `Hex-Dragon/PCL2` 仓库访问重定向到该仓库；所查 ModBase.vb 的版本常量为 2.13.1.1。以下事实只对检查的源码快照及所引用官方说明成立，不代表每个历史或未来发行版均相同。

## 结论

**PCL2 可以静态发现部分游戏根，实施路线是只读当前用户注册表的 `LaunchFolders`（可能不存在）和 `CacheDownloadFolder`，缓存作为待验证锚点，再以用户指定目录补足。** `LaunchFolderSelect` 是存在启动器旁的普通配置；PCLCE 新版则另读 `%APPDATA%/PCLCE/config.v1.json` 的同名列表与缓存字段。首发不使用进程和快捷方式搜索。

静态注册表路线能覆盖已登记的自定义游戏根；不能发现所有任意位置的便携 PCL，因为未经改名的自动当前目录不保存在该列表，也没有从本次检查源码确认到一个可供全局定位 PCL exe 的安装路径登记项。快捷方式可作为一般 Windows 路径来源，但不是 PCL 保证存在的目录索引。未命中的用户选一次启动器目录或游戏根，即可完成首发辅助目标。

补充核实：`LaunchFolders` 默认值为空；缺值读取仅缓存默认值，写入值与缓存一致时直接返回。只有自动默认目录且没有自定义/改名项时，注册表可能根本不创建该值。`CacheDownloadFolder` 仅作为路径线索，不承诺它总是默认目录、不可自定义或不会过期。从缓存目录向上最多三层，以 PCL 本地配置和已知可执行文件确认根，不固定删除两级目录。

本机只读取目录相关字段的实际核对结果：PCLCE 的共享 JSON 列表指向 `D:/Games/MC/.minecraft`，下载缓存为 `D:/Games/MC/PCL/MyDownload`；其目录含 CE 可执行文件、Setup.ini 和 config.v1.yml。新扫描器通过列表与缓存两条来源找到该游戏根下的隔离世界并合并来源，不读取登录字段。

## 1. 键名和持久化位置

[Settings.vb，88–89 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Pages/PageSetup/Settings.vb#L88-L89) 定义：

```vb
New Setting("LaunchFolderSelect", ""),
New Setting("LaunchFolders", "", Source:=Sources.Registry),
```

**真实列表键是 `LaunchFolders`，不是 `LaunchFolderList`。** 后者在本次完整公开源码搜索中未找到。Setting 构造器默认 Source.Normal、Encrypted.False；Normal 保存到 `WriteIni("Setup", Key, Value)`，Registry 保存到 `HKEY_CURRENT_USER\Software\{RegFolder}`。相应读取也是 Normal `ReadIni("Setup", ...)` 与 Registry `RegistryUtils.TryRead(...)`。[默认和持久化实现，217–270 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Pages/PageSetup/Settings.vb#L217-L270)、[读取实现，329–338 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Pages/PageSetup/Settings.vb#L329-L338)

普通命名 INI 文件的解析会映射到 `Paths.Base + "PCL\\" + FileName + ".ini"`，故 Select 位于 **`<启动器目录>/PCL/Setup.ini`**。INI 是按冒号写 `Key:Value`，不是标准 section/equals 格式；查找应在第一处冒号切割，以保留 `D:\` 的冒号。[INI 路径及读取，相关代码](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Base/ModBase.vb#L446-L564)

注册表分支须区分：

- 公开开源源码的 `RegFolder = "PCLDebug"`，注释明确说它与常规版隔离以防数据冲突。因此此快照实际读取的是 **`HKCU\Software\PCLDebug\LaunchFolders`**。[ModSecret.vb，5–8 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/ModSecret.vb#L5-L8)
- 常规版 **`HKCU\Software\PCL`** 的路径由官方仓库 MEMBER `LTCatt` 的维护说明直接确认：出错后手动删除 `计算机\HKEY_CURRENT_USER\Software\PCL` 的错误项。[官方成员说明](https://github.com/Meloong-Git/PCL/issues/9179#issuecomment-5527183195)。另有上游 CONTRIBUTOR 描述“备份注册表”调用 `reg export HKEY_CURRENT_USER\Software\PCL PCLRegBackup.reg`，只作旁证。[备份实现说明](https://github.com/Meloong-Git/PCL/issues/3054#issuecomment-1872522555)

因此首发可优先读取 `HKCU\Software\PCL` 的 `LaunchFolders`，需要兼容开源构建时额外尝试 PCLDebug。只读取指定路径值即可，不需导出或枚举整个启动器注册表。**不能从公开源码里 PCLDebug 的常量直接推导常规版名；两条证据应分别保留。**

## 2. `LaunchFolders` 的内容和静态覆盖

[ModMinecraft.vb，88–120 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L88-L120) 明确注释格式为 `名称>路径|名称>路径`。代码以 `|` 拆条目，以第一处 `>` 分开名称和路径，并要求路径以反斜杠结尾。示意：

```text
Fabric整合包>D:\Games\Fabric\.minecraft\|原版>E:\MC\.minecraft\
```

这是**未加密的路径列表字符串**。新增目录时把选定 FolderPath 补结尾 `\` 后保存，路径没有使用 `$` 替换；仅 Select 会替换 `$`。显示名禁止 `>` 和 `|`，所以简单分隔解析有上游生成规则支持。[名称约束和新增保存，149–214 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Pages/PageSelectLeft.xaml.vb#L149-L214)

FolderPath 来自目录选择器并经过路径规范化，但静态辅助仍应独立限制为存在、可读的绝对目录；没有 launcher base 时不猜相对路径、不展开列表内的 `$`。坏条目跳过即可，无须重复 PCL 的弹框、写入权限检查或配置同步。每个根交给已有通用识别：`saves/<world>/level.dat` 和 `versions/<version>/saves/<world>/level.dat`。

**列表并非完整所有目录表。** PCL 每次先从 exe 当前目录和直接子目录产生 Vanilla 候选，然后从注册表增补 Custom 或重命名 Vanilla，最后仅把 **Type 不为 Vanilla** 的目录保存回 LaunchFolders。因此未经改名的自动当前目录、官启目录不会因被发现就登记进去；空列表不能解释为 PCL 没有存档。[默认探测，63–82 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L63-L82)、[仅保存非 Vanilla，114–120 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L114-L120)

注册表列表属于当前用户/发行版分支，并没有按 launcher exe 路径分区。它不是安装位置索引，不能靠列表反推出哪个 PCL exe 正在使用某个游戏根。对发现辅助而言只需将它标为“PCL 登记目录”，按最终 saves 路径去重即可。

## 3. 便携目录和 `LaunchFolderSelect` 的 `$` 语义

启动器在 base 旁建立 PCL/Pictures、PCL/Musics，并在无权限时提示把 PCL 移动到其他文件夹，游戏根也会在 base 旁自动探测或创建。因此 exe 位置可以变化，不能假定固定 Program Files/AppData 安装路径。它同时使用用户注册表，故“可移动 exe/局部文件”不等于所有配置完全便携。[Application.xaml.vb，68–74 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Application.xaml.vb#L68-L74)、[默认游戏根创建，124–127 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L124-L127)

Select 保存逻辑为 `Value.Replace(Paths.Base, "$")`；读取为 `Value.Replace("$", Paths.Base)`。`Paths.Base` 是**带尾分隔符**的启动器基目录：代码直接拼 `AppDomain...ApplicationName` 为 exe 路径，直接拼 `PCL\Pictures\` 为目录。[Select 实现，17–21 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L17-L21)、[exe 拼接，42 行](https://github.com/Meloong-Git/PCL/blob/0e0d12fdce6a2804916fb2be60e41144da637c18/Plain%20Craft%20Launcher%202/Modules/Base/ModBase.vb#L42)

假设 base 为 `D:\Launcher\`，默认根为 `D:\Launcher\.minecraft\`，上游保存结果是：

```text
LaunchFolderSelect:$.minecraft\
```

因此 `$` 本身包含 base 的尾分隔语义，不能把它直接替换成无尾分隔的 `D:\Launcher`。第一代 `Pcl2ProcessDiscoveryProvider.cpp:157–159` 用 `executableDirectory.wstring() + wide.substr(1)`，而测试使用 `$/.minecraft`（tests/Pcl2ProcessDiscoveryProviderTests.cpp:78），可能把真实 `$.minecraft\` 拼成 `D:\Launcher.minecraft\`；该错配应在第二代避免，不必复制第一代假设。[第一代展开](D:/Programs/MineBackup/MineBackup/src/core/Pcl2ProcessDiscoveryProvider.cpp:147)、[第一代样例](D:/Programs/MineBackup/tests/Pcl2ProcessDiscoveryProviderTests.cpp:78)

建议小型 parser 仅支持：绝对 Select；或开头 `$` 代表带尾分隔的用户选定启动器根，再容忍去掉后缀开头多余 `\` / `/`，以兼容 `$.minecraft\`、`$\.minecraft\`、`$/.minecraft`。其他复杂相对字符串跳过，保留手动选游戏根。上游实际是替换所有 `$`，首发无需模仿复杂嵌入形式。

## 4. 静态来源的优先级与边界

| 来源 | 能提供什么 | 小型首发建议 |
| --- | --- | --- |
| HKCU/PCL/LaunchFolders | 已登记自定义/重命名游戏根，关闭启动器亦可读取 | 主静态来源；PCLDebug 可作为一个小扩展 |
| FolderRewind 已保存的用户指定启动器根/游戏根 | 后续扫描的确定锚点，不依赖运行状态 | 保留少量本地根记录即可；与配置的最终 saves 身份去重 |
| 用户选择启动器目录 | 读取旁边 PCL/Setup.ini 的 Select；探测根、.minecraft、直接子目录 | 主要兜底；文件名可重命名，不以 exe 名作为必需条件 |
| 用户选择 Minecraft 根/saves | 直接通用结构识别，与 PCL 身份无关 | 最简单兜底，复杂路径无需深究 |
| HKCU/PCL/CacheDownloadFolder | 可能过期或自定义的缓存目录 | 从自身向上最多三层，以本地配置或已知 exe 标记确认工作区；不能固定裁剪目录层级 |
| PCLCE 共享 JSON | 新版及旧版配置中的目录列表、缓存字段 | 独立适配器读取，不假设与 PCL2 共用注册表 |

本次完整源码检查未找到 PCL 保证生成并登记一个桌面/开始菜单快捷方式，也未确认专门的 InstallPath/exe 全局登记项。不能将“可能有快捷方式”当默认覆盖保证；快捷方式可能未创建、目标被移动或只指向中间脚本。首发采用**注册表列表 + 经标记确认的缓存线索 + 手选根**，不接入快捷方式扫描、Windows 最近使用、搜索索引、全盘 exe 搜索或后台进程监控。

当便携 PCL 放在任意目录、其默认游戏根未经登记、没有可用快捷方式、用户也没有提供根时，静态有限规则没有定位锚点。此时提示“没有发现，可选择启动器目录或存档目录”即可；这是定位信息的缺失，不要求扩大首发范围。

## 5. 首发落地建议

1. 自动来源只读 `HKCU\Software\PCL` 和 PCLDebug 的 `LaunchFolders`、`CacheDownloadFolder`；列表按 `|` / 第一处 `>` 解析，保留显示名，限绝对目录，交通用 Java 检查器。缺列表值正常；缓存从自身向上最多三层，经本地配置/已知 exe 标记确认位置。配置读取上限 1 MiB，候选上限 500 个实例，不引入 NBT。
2. 允许手选 PCL 启动器目录，完成扫描后记住这个根；读取确认根的 `PCL/Setup.ini` 或直接 `Setup.ini` 的 Select，正确展开 `$`，检查根、.minecraft 和直接子目录。也允许直接选 Minecraft 根、saves 或单世界。
3. 只返回来源、路径和世界列表，确认后交 FolderRewind 常规配置创建；发现与账户、存档健康、实时游戏状态分离。无需读取任何登录值，无需写回 PCL 配置/注册表。
4. PCLCE 不应仅因第一代进程白名单包含 CE 名就视为与 PCL2 同源。首发独立读取 `%APPDATA%/PCLCE/config.v1.json` 的 LaunchFolders/CacheDownloadFolder，兼容 PCLCE/config.json 与 .PCLCE/Config.json；本地 YAML 仅作为目录标记，特殊路径覆写手选补足。补充读取的 CE 固定快照 [a42a7699948aebd1e2563df025274af255c1f559](https://github.com/PCL-Community/PCL-CE/tree/a42a7699948aebd1e2563df025274af255c1f559) 已有新的配置体系和 `PCL_PATH` 等路径覆写。[CE Paths.cs](https://github.com/PCL-Community/PCL-CE/blob/a42a7699948aebd1e2563df025274af255c1f559/PCL.Core/App/Paths.cs#L64-L84)

该方案符合用户给定的“小型发现辅助”：首发包含所有计划启动器的简单路径 adapter；PCL2 不必为了任意便携位置做到全自动，也不要求运行启动器。本机 PCL/PCLCE 只读验收找到 12 个实例、23 个世界，来源合并且路径不重复；其他安装布局的实际覆盖仍受可用定位线索限制。
