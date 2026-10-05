# Plugin API 3.7 / MineRewind 1.9.6 空间预览

## 交付边界

从管理页的文件夹操作菜单打开“地图预览”。Host按Config Kind owner选择`ISpatialPreviewCapability`，提供通用二维容器；MineRewind负责世界布局、MCA/NBT读取和地表像素。Host不引用插件源码或Minecraft领域程序集。

仅浏览当前配置源目录，采用只读、尽力一致的读取，不触发保存游戏、整世界快照、备份、还原或History写入。刷新更换generation并重新枚举世界。缓存是预览优化，不是capture baseline或内容等价证据。

支持Java 1.18–1.21.x和26.1–26.1.2正式版DataVersion；每chunk独立判断。支持GZip/ZLib/未压缩及外置`.mcc`。LZ4、旧版本、快照和未来版本保留占用显示及诊断，不假装空chunk。

主世界/末地扫描全高，下界默认最大Y=120，可手动改变。颜色为自行维护的近似色表；未知方块用备用颜色。无材质包、生物群系染色、光照、水深透明度或自动洞穴识别。

## 实现与约束

- `MineRewind.World`仅依赖BCL和fNbt；复用世界根解析；读取与地表投影分开，保留向未来Merge共享读取能力的边界。
- `Describe/Render/Inspect`只返回BCL数据，Host每次调用短租约并冻结输出。tile固定256×256 BGRA8预乘格式。取消和generation检查防止旧结果覆盖新图。
- 图层/坐标标签/网格/高度设置由provider描述。配置变化使会话失效，页面不持有插件实例或长租约，不阻碍插件Draining。
- 近景按chunk解码；远景只读header或region目录，不为冷缩略图解码全世界。4个并发请求，领域surface缓存128MiB，Host位图缓存64MiB；无磁盘缓存。
- 重用既有NativeHostMutationContext阻止preview回调通过正式Host façade发起嵌套备份/还原。v3仍为同进程受信插件模型，不宣称OS沙箱。
- Public API仅增量新增，程序集identity仍为3.0.0.0。History、Artifact、Capture和Restore契约不变。

## 开发组合

API 3.7是本地候选，未向NuGet.org发布。先`dotnet pack` Abstractions到本地feed，再使用包含该feed和nuget.org的临时NuGet.Config还原MineRewind。禁止添加插件到Host App的ProjectReference。公开发布包前，独立CI需要能够取得对应3.7包。

Host内置固定`MineRewind-1.9.6.frplugin`与SHA256；包包含MineRewind.World/fNbt，排除Abstractions DLL。更新旧构建目录时确保Assets/Plugins只剩一个内置版本；正式clean build只携带当前项目列出的版本。

## 验证

- MineRewind：86通过。新增5个领域测试方法（含参数化数据，共17个case）以及1个preview组合方法；覆盖负坐标、压缩/外置记录、越界/重叠、padded longs、负section、版本、读取无写入、刷新隔离和远景无NBT解码。
- Abstractions：15通过，已人工检查PublicApi新增签名；Runtime：145通过，新增1个租约/取消/像素冻结测试，复用原有activation与manifest基础。
- Host定向：16通过，包含2个新增viewport/旧结果失效测试及既有capture/checkout边界回归。
- WinApp CLI x64 Debug构建通过，0 warning / 0 error（修正了首轮ObservableObject歧义及x:Bind诊断）。
- Windows Sandbox未启用；使用独立`Leafuke.FolderRewind.PreviewAcceptance`包身份、本地生成Anvil世界和单独LocalState完成UI检查，没有修改真实配置或世界。
- UI确认：管理页入口、地表tiles、坐标定位、中心点详情（128,128→chunk 8,8）、最大Y改变、下界默认120及netherrack、刷新、返回重进。修复NumberBox文本未提交时定位读旧Value的问题。
- 没有添加截图黄金测试、属性转发测试或长期UI自动化框架。UI截图和生成器仅存在ignored artifacts目录。

尚未声称完成：真实游戏生成世界/大型modpack显示验收、x86/ARM64运行、完整高对比与读屏人工验收。生成fixtures验证格式与产品接线，不等于真实Minecraft加载验收；本阶段无写回功能。

## 参考

- MCA Selector固定参考commit `d31bee6a7a4e894f250bb3ab47347e8c85b6ca5a`：https://github.com/Querz/mcaselector/tree/d31bee6a7a4e894f250bb3ab47347e8c85b6ca5a
- 参考其现代section/palette、逐列扫描和任务代际设计；本实现为自行编写的C#代码，没有复制Java实现或分发Minecraft纹理。
- DataVersion正式版表参考 https://github.com/misode/mcmeta/tree/summary/versions ，核对日期2026-10-05。
