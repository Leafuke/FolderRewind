# Plugin API 3.8 / MineRewind 1.9.7 空间预览

后续修复与当前构建入口见 [API 3.9 / MineRewind 1.9.8 修复记录](spatial-preview-repair-2026-10-06.md)。下文保留上一轮交付事实。

## 本轮交付

地图优先布局：移除页面重复返回按钮，维度选择与导航、坐标、显示、刷新集中到紧凑工具栏。详情按需展开，窄窗口为浮层；网格默认关闭。拖动立即变换已有画面，40ms 限流只控制瓦片需求计算；滚轮围绕鼠标缩放，最多四个瓦片请求，保留可复用分辨率图像直到新图完成。

MineRewind 使用来自 Querz/MCA Selector 固定 commit `d31bee6a7a4e894f250bb3ab47347e8c85b6ca5a` 的六份版本化代表色色表。方块状态、生物群系、水深及相邻高度参与着色；运行时不读取游戏 JAR 或原始纹理。资源及改编算法的来源、MIT 许可在插件 `Colors/MCASelector.LICENSE.txt` 和领域项目 `Colors/README.md`。普通缩放（1/2/4/8 blocks-per-pixel）保留彩色地形，≥16 使用 region header 的实际 chunk 占用概览；不是所有缩放下的完整纹理渲染。

玩家导航读取上次保存位置，覆盖 level.dat 内嵌 Player、playerdata 和 26.1 players/data；UUID 转换复用领域 helper。单人玩家可以一键定位，多人通过列表选择；名称仅使用受控目录内可用的数据，不联网查身份。首次定位优先单人玩家、出生点、适应范围；当前应用会话内按世界/维度保留最多128个轻量视角记录。导航不进行下界比例换算，也不自动进入洞穴模式。

## API 与生命周期

`ISpatialPreviewCapability` 增加 `GetNavigationTargetsAsync` 和幂等 `CloseAsync`，默认实现允许没有缓存/导航的旧 provider 继续工作。导航返回通用 ID、分组、名称、图层与坐标；DefaultTargetId 用于初始定位，QuickTargetId 用于主按钮快捷定位。LayerLabel、NavigationLabel、CellLabel 是可本地化描述，不给 Core 引入 Minecraft 类型。

Host 每次调用仍使用短租约和冻结输出，导航数量限制4096。会话关闭取消并排空该会话的调用（包括未完成的 Describe），然后独立执行 Close；不依赖旧配置仍有效或原加载 token 未取消。插件停用时 Deactivate 兜底清理。仅 Describe 创建缓存会话，Render/Inspect/Navigation 不会重建关闭的会话。插件缓存关闭后禁止迟到写入；Host 清理位图、详情及导航引用。

Host 位图预算64MiB包含回退缩放级别；插件地表缓存预算128MiB，粗图的临时引用按行裁剪。世界索引、在途解码、平台图形资源不包含在这两个缓存预算中，不能把它们描述为进程内存上限。未使用强制GC；无持久磁盘地图缓存。

## 边界

只读当前配置源目录，尽力一致，显式刷新开始新 generation；不调用备份、还原、History 或世界写回。沿用 Java 1.18–1.21.x、26.1–26.1.2 正式版 DataVersion 白名单和 gzip/zlib/raw、外置 mcc 支持。LZ4、旧版、快照及未来格式仍有诊断，不因色表版本扩大格式支持。预览投影和颜色不能作为未来 merge 的 chunk 相等依据。

未实现：资源包导入、模组颜色提取、完整模型/纹理渲染、自动洞穴、磁盘缓存、语义合并。未知方块用中性缺色纹理及详情标识，与损坏/暂不支持分开。

## 构建与验证

API 3.8.0 是本地候选，未发布 NuGet。先 pack Abstractions 到 `artifacts/preview-feed`，使用该 feed 与 nuget.org 还原 MineRewind；本轮使用独立 `artifacts/preview38-packages`，没有清理机器原有 NuGet 缓存。插件1.9.7通过既有 MineRewind.Pack 工具打包，排除 Abstractions DLL，同步替换 Host 内置包与 SHA256。

按用户要求不新增测试方法、用例、测试框架或UI自动化；只更新现有 PublicApi 快照、版本断言和受变更影响的预览测试步骤（刷新先Describe、远景阈值16）。执行现有API、Runtime、Host定向和MineRewind回归，以及WinUI x64 Debug构建。本轮现有测试通过：API 15、Runtime 145、Host定向16、MineRewind 86，合计262项；WinUI x64 Debug和插件Release构建均0警告、0错误。内置包已核对manifest版本、API要求、领域程序集、MIT许可、排除Host API DLL及SHA256。

## 待用户人工审核

- 原版村庄与建筑、森林、山地、海岸/河流：辨识度、群系色与水深效果；灰色缺色与黄色/红色异常样式不混淆。
- 连续拖动、鼠标锚点缩放、冷加载时已有图像保留，普通缩放与远景概览过渡。
- 窄窗口、长维度名称、工具栏溢出、详情开关、键盘操作和高对比主题。
- 单人/多人定位、维度切换、缺少玩家数据、出生点回退、刷新及重新进入后的视角。
- 加载过程中退出、连续刷新及反复进入退出：旧请求不覆盖新页面，内存不随次数持续增长。应区分实际存活资源与操作系统工作集，不能要求退出瞬间工作集归零。

未启动应用、自动演示或真实世界操作。上述视觉效果、实际跟手程度和内存平台表现均待用户审核，构建与现有测试通过不能替代人工验收。
