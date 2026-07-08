# Localization Text Optimization Design

**Date:** 2026-05-27
**Scope:** Full audit and systematic rewrite of zh-CN and en-US resw localization files
**Files:** `FolderRewind/Strings/zh-CN/Resources.resw`, `FolderRewind/Strings/en-US/Resources.resw`

## Goal

Optimize all user-facing text in both locales for clarity, consistency, professionalism, and WinUI 3 convention compliance.

## Terminology Glossary (Canonical Terms)

| Concept | zh-CN | en-US | Notes |
|---|---|---|---|
| Clean restore mode | 安全还原 | Clean restore | Unify from: Clean 还原, 清洁还原, 安全还原（Clean 模式）|
| Overwrite mode | 覆写 | Overwrite | Unify from: 覆写, 覆盖 |
| Full backup | 全量备份 | Full backup | Remove English "Full" from zh-CN |
| Smart/Incremental | 智能增量 | Smart incremental | Remove English "Smart" from zh-CN |
| Configuration | 配置 | Configuration | Short form "config" OK in technical context |
| Restore (backup context) | 还原 | Restore | Unify from: 还原, 恢复 |
| Metadata | 元数据 | Metadata | zh-CN should use Chinese term |
| Proper nouns | Keep English | Keep English | rclone, 7-Zip, KnotLink, Mica, Acrylic, GitHub |
| App name (zh-CN) | 存档时光机 | FolderRewind | zh-CN uses Chinese name, en-US uses English brand |

## Optimization Principles

### zh-CN
- **Default tone**: Professional, rigorous, accessible — like Windows system apps
- **Casual touches**: Allowed only in specific places (e.g., sponsor description `解锁一点点外观设置项~`)
- **Technical terms**: Translate where possible (metadata → 元数据), keep proper nouns in English
- **Descriptions**: One sentence preferred, max two sentences. No trailing "..." unless placeholder
- **ToggleSwitch**: Use `开启`/`关闭` instead of `已开启`/`已关闭`
- **No mixed language**: Avoid patterns like "Smart 备份", "Clean 还原", "Full 备份"

### en-US
- **Follow Microsoft WinUI 3 text guidelines**: sentence case, action-oriented, concise
- **ToggleSwitch**: Use `On`/`Off` (standard WinUI pattern)
- **Dialog titles**: Noun phrases, not sentences
- **Button text**: Verb phrases without trailing punctuation
- **Natural English**: Not translationese — read like it was written natively
- **Descriptions**: One sentence preferred, max two sentences

## Issues to Fix

### A. Terminology Inconsistencies
1. "Clean" restore: `Clean 还原` → `安全还原` / `Clean restore`
2. "覆写" vs "覆盖": Unify to `覆写` / `Overwrite`
3. "恢复" vs "还原": Unify to `还原` / `Restore` (backup context)
4. "metadata" vs "元数据": Unify to `元数据` / `metadata`
5. "Smart 备份" → `智能增量备份` / `Smart incremental backup`
6. "Full 备份" → `全量备份` / `Full backup`

### B. Outdated Content
1. Copyright: `© 2025` → `© 2025-2026`
2. `Sponsor_Status_Locked`: "加载项" is ambiguous → use "附加功能" or clearer term

### C. Awkward zh-CN Phrasing
1. `SettingsPage_PresetsDesc`: Too casual → make more professional
2. `FolderManager_CommentPlaceholder`: "bug" → "问题"
3. `SettingsPage_FontSizeDesc`: "文字区块" is vague → clarify
4. `ConfigSettingsDialog_SafeRestore.OnContent`: Technical jargon → explain better

### D. Awkward en-US Phrasing
1. `FolderManager_DuplicateDisplayName_Title`: Rewrite for clarity
2. `ShellPage_FolderConflict_Footer`: Too technical → simplify
3. `ConfigSettingsDialog_SafeRestore.OnContent`: Confusing → clarify
4. `SettingsPage_PresetsDesc`: "live here" is odd → rewrite
5. `BackupService_RestoreMissingBaseFull_Content`: Too long → break up

### E. WinUI 3 Convention Fixes
1. ToggleSwitch `OnContent`/`OffContent`: Short labels, not past-tense
2. Ensure sentence case in en-US headers
3. Remove unnecessary punctuation from button text

## Scope

### In Scope
- All user-facing text in both resw files (~4700 lines each)
- Terminology unification across all strings
- WinUI 3 convention compliance
- Cross-checking key parity between locales

### Out of Scope
- Log strings (prefixed with `[xxx]`) — only fix obvious terminology inconsistencies (e.g., "恢复" → "还原"), not full rewrite
- XAML code changes
- C# code changes
- Adding new string keys (unless needed for split descriptions)

## Process

1. Build canonical glossary (done)
2. zh-CN systematic pass: apply glossary, fix wording, improve clarity
3. en-US systematic pass: apply glossary, natural English, WinUI conventions
4. Cross-check: verify both files have identical keys
5. Review and commit
