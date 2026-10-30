# 产品消息协议 v1

公告发布源为 Host 仓库 `dev` 分支的 `announcements/v1/index.json`。旧 `notice_zh` 与 `notice_en` 保持纯文本，服务旧客户端。新客户端不回退读取旧入口。先将有效索引发布到 dev，再分发新客户端；初始空索引表示暂无适用公告，不能伪造发布时间或发布结论。

## 索引

UTF-8 JSON，camelCase。`schemaVersion` 必须为 1，`items` 最多 50 条，完整响应与本地缓存最多 2 MiB。非法文档不替换此前有效缓存。Setup 发布工作流在 preflight 阶段校验索引身份、范围、时间和双语正文。

每条公告包含 `id`（不超过 100 字符，仅 ASCII 字母、数字、点、下划线及连字符）、`revision`、`publishedAt`、`expiresAt`、`channels`、`kind`、`locales`。日期使用带时区的 ISO 8601；过期必须晚于发布。`locales` 必须提供 `zh-CN` 和 `en-US`，各含 `title`（最多 256 字符）、`summary`（最多 1024）、`body`（最多 32768），均为非空纯文本。

`channels` 为非空数组，取值 Store、Setup、Sideload、Developer、Unknown。可选 `minVersion` 为包含下界，`maxVersionExclusive` 为排除上界，比较时补齐到四段版本号。没有匹配的版本或渠道不会投递。

`kind` 为 general、release、criticalCompatibility。release 必须提供三段 `releaseVersion`，只允许受 Host 稳定发布策略接受的版本。criticalCompatibility 必须明确上下版本边界；范围匹配后才进入重要消息。可选 `legacyVersions` 数组是旧 NoticeLastSeenVersion 的精确别名，禁止通过正文猜测对应关系。

正文不携带可执行命令或安装地址。更新入口仅由 Host 的渠道与资产策略解析。已发布 ID 跨语言、修订保持稳定；编辑 revision 静默更新且保留阅读选择。需要重新提醒的新事件使用新 ID。

## 本地状态与投递

公告身份为 notice:{id}，更新身份为 update:{channel}:{四段版本}。业务结果仍使用有界会话通知存储；产品的已读、归档、忽略、发现、实际投递回执与暂缓期限通过 ConfigService 保存。清理即归档，现有十秒撤销同步撤销归档；缓存恢复不会重置阅读状态或弹出提醒。

普通公告和更新仅首次前台轻提示，后台静默留存。严重兼容性公告的系统通知尊重通知开关和级别。七天暂缓在应用运行且进入前台后重新评估，不在操作系统创建唤醒任务。关闭产品开关停止自动联网，手动刷新保留。

相同关联版本的 release 公告与已验证更新合并；严重兼容性公告保持独立。公告不能确认商店可安装性。实际运行版本达到目标后不再推荐该目标。

缓存有效期最多为最后获取后 30 天，并受公告自身过期时间约束。新有效索引未包含的公告视为撤回；过期身份状态额外保留 30 天；身份状态最多 512 条，达到上限时先淘汰已不适用的旧身份。缓存失败只记录诊断，手动刷新失败就地显示。保存失败明确说明本次会话选择未获跨重启保证。
