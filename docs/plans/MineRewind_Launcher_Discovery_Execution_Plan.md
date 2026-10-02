# MineRewind 多启动器轻量存档发现执行计划

日期：2026-10-02。按用户批准的计划执行，只做本地 Git 提交，不发布远端包或 Release；不新增测试用例、测试项目或测试脚本。

## 目标与默认边界

首发识别官方 Java、HMCL、PCL2、PCLCE、Prism Launcher、Modrinth App、网易中国版和 Bedrock 的常见位置，复用现有候选选择与配置创建流程。世界目录存在 `level.dat` 即作为候选，不增加 NBT、LevelDB 或存档健康验证。Prism 保持 `minecraft` 优先、`.minecraft` 回退；不做全盘搜索、快捷方式扫描或进程监控。适配器只读目录相关字段，不写回启动器配置或注册表。现有 Java 能力和 mods 草稿行为保持，复杂位置手选补足。

## 架构与 Host 接入

- MineRewind 的 `V3/Discovery` 拆分位置适配器、共用布局扫描器、协调器。提示包含路径、类型、版别、名称和来源；共用扫描器展开 saves、versions/*/saves、世界集合和单世界。
- 常规路径规范化去重，合并来源；Java 按实例、Bedrock 按世界集合分组；Java 路径哈希身份算法保留。
- API 3.6 在原 `DiscoveryRequest(UserRoots)` 构造器之外增加 `IncludeKnownLocations`，默认 false。全机、预设和开启的启动自动创建传 true；手选及向现有配置添加源传 false。
- Host 按插件保存完成扫描的手选根，默认空集合；后续全机发现合并记忆根及已有配置附近目录。保存失败补偿内存状态；界面设置保存保留扫描期间新增的记忆根。
- AutoDiscoverSaves / AutoCreateConfigs 设置和默认值不改变；不增加启动器开关页。
- 后台扫描逐目录检查取消、逐来源隔离异常；配置读取最多 1 MiB，最多 500 个实例。插件在 Host 的 30 秒截止前检查自身预算，超限返回已完成结果及说明；单次文件系统调用本身仍受系统 I/O 限制。

## 启动器规则

| 来源 | 规则 |
| --- | --- |
| 官方 Java | `%APPDATA%/.minecraft`，不额外解析官启 profile 的自定义 gameDir。 |
| HMCL | 全局 user-game-directories.json；已知工作区的新版 game-directories.json、旧 hmcl.json/settings.json。只取路径和可选名称，工作区明确才展开相对路径。 |
| PCL2 | 指定注册表分支 PCL / PCLDebug 的 LaunchFolders 和 CacheDownloadFolder；缺值正常跳过。 |
| PCLCE | AppData/PCLCE/config.v1.json，兼容 PCLCE/config.json 和 .PCLCE/Config.json；只取同名目录列表和缓存线索，不假定与 PCL2 共用注册表。 |
| Prism | AppData / Scoop / 手选数据根，InstanceDir，相对值锚定数据根；instance.cfg 和两种游戏子目录。 |
| Modrinth | 新旧 AppData 的 profiles 及可见 THESEUS_CONFIG_DIR；不打开 app.db 获取自定义位置或名称。 |
| 网易 | 注册表 DownloadPath 下 Game/.minecraft；已知 MinecraftPE_Netease/minecraftWorlds。 |
| Bedrock | Minecraft Bedrock/Users/*/games/com.mojang/minecraftWorlds 和旧 UWP 世界集合；可读 levelname.txt 首行取名称。 |

PCL 定位顺序是明确绝对游戏根优先、缓存辅助、手选补足。CacheDownloadFolder 从自身向上最多三层，以嵌套 PCL/Setup.ini、直接 Setup.ini/config.v1.yml 和已知可执行文件识别工作区或数据根；不固定删除目录层级。直接数据根的父目录及同级可执行文件根作有限候选。已确认基目录才展开 Select 中的 `$`，兼容 `$.minecraft`、`$/.minecraft` 和带反斜杠的等价形式；INI 在第一处冒号分割，不拆坏盘符。读取确认根和直接子目录的已知布局，未确认线索跳过。PCLCE 本地 YAML 仅作标记，不新增 YAML 依赖。

手选入口支持启动器目录、游戏根、实例库、saves、minecraftWorlds 和单世界。

## Bedrock 与版本交付

新增 minecraft-bedrock Definition / minecraft-bedrock-saves Kind，rawWithWarnings / restoreCoordination:none。Bedrock 使用普通文件流程，不注册 Java 的 NBT、区域、玩家保留和 KnotLink 协调能力；Kind 说明提示关闭游戏，发现页显示该说明，显式 Java 热键命令拒绝其他 Kind。通用插件的标准发现预设匹配草稿 Kind，避免新 Bedrock 草稿被 Core 类型拦截。

SDK 版本 3.6.0，程序集身份仍 3.0.0.0；MineRewind 1.9.4，要求 API 3.6。SDK 先生成本地 nupkg，插件用临时 NuGet 源构建，不引用 Host 源码。同步内置 frplugin、SHA-256、预设和升级引用。源和包均为本地候选，不宣称 NuGet 或 GitHub 已发布。

提交顺序：

1. FolderRewind：`feat(plugin-api): 增加发现范围选项并支持记忆扫描目录`
2. MineRewind：`feat(minerewind): 支持多启动器与基岩版存档发现`
3. FolderRewind：`feat(minecraft): 接入新版存档发现并更新内置插件`
4. 各文档所属仓库：`docs(minecraft): 补充存档发现执行计划与支持边界`

## 验证与验收记录

- 构建 SDK、Runtime、Host、MineRewind，运行相关现有检查；不新增测试。更新 API/版本基线，原有 Java 定义检查仅改为明确选择 Java，继续验证同一行为。
- 验证包 ID、版本、API、SHA-256 和不包含 Abstractions DLL。
- 只读核对本机 PCL2 缺目录列表时的缓存路线、PCLCE 配置及手选根；其他启动器按一手路径/配置格式核对，不宣称本机安装了所有启动器。
- 检查手选范围、去重、已管理资源、取消/超限和异常隔离；检查 Bedrock 的普通路由与 Java 原有能力。
- 临时 HEAD 副本复跑 Host 全量检查，用于确认近期历史模块的既有失败；不修改历史代码或增加相关测试。

实际验收结果：

- SDK、Runtime、MineRewind 与 Host 构建成功。Host 使用 `dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:CETCompat=false -p:GenerateAppxPackageOnBuild=false -v minimal`，结果为 0 警告、0 错误。
- 现有 SDK 检查 15 项、Runtime 检查 138 项、MineRewind 检查 42 项、Host 发现/启动器/预设相关检查 68 项全部通过。未新增测试用例、项目或脚本。
- Host 全量检查 648 项通过、15 项失败。实施前 HEAD 的临时副本同样为 648/15，失败名称差集为 0，均为已有历史模块失败；未修改相关代码。日志在本地 `artifacts/discovery-host-tests.log`，TRX 在 `artifacts/discovery-validation/host`（不提交构建输出）。
- 本机实际 PCL 注册表缺少 LaunchFolders，缓存为 `D:/Games/MC/PCL/MyDownload`；PCLCE 共享 JSON 指向 `D:/Games/MC/.minecraft`。只读运行扫描得到 12 个实例、23 个世界，均位于该游戏根下，重复路径为 0，PCL 与 PCLCE 来源合并。
- 手选 `D:/Games/MC/PCL` 不包含全局来源；取消请求正常终止。运行时目录包含 Java 与 Bedrock 两个 Definition，Java 能力仍绑定 minecraft-saves；Bedrock 无 Java 协调器时预检为 Ready。未修改真实世界，未对真实存档执行备份或还原。
- 最终包 `MineRewind-1.9.4.frplugin` 的 SHA-256 为 `14fb12fee03d2592d7d7b6a526dfc5a4ec875e944e2112620ef653539ad3424b`。包内仅有 manifest.json、settings.schema.json、MineRewind.dll、fNbt.dll；Manifest 要求 API 3.6，未携带 Abstractions DLL。Host 包、摘要文件、预设与离线升级引用一致。
- SDK 的本地 nupkg 为 `artifacts/nuget/FolderRewind.Plugin.Abstractions.3.6.0.nupkg`；插件通过临时 NuGet 配置读取该本地源，不引入 Host 项目引用。

验收限制：其他启动器的配置格式和目录规则已按源码及文档核对，但本机没有安装全部启动器；真实 Bedrock 游戏运行、真实 Java 游戏备份/还原及完整发现页 UI 操作尚未逐项执行。结果是本地交付候选，SDK、插件包和 Release 均未发布远端。
