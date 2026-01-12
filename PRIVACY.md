# Privacy Policy

FolderRewind is a desktop application for folder backup and management. We store only essential data locally and do not collect telemetry or upload personal data.

What we do NOT collect

- No device identifiers, tracking or analytics are collected by the app.
- No automatic usage telemetry or behavioral analytics are sent to remote servers.
- We do not sell or share your personal data with third parties.

Local data stored (local only)

- Configuration: `config.json` under the app LocalState when packaged.
  This file contains settings, backup configurations, and plugin settings.

- Logs: `logs/app.log` — timestamps, level, messages and exceptions. Logs may include limited environment info (OS version, app version) for troubleshooting. Logs are stored locally and are not uploaded by default.
- Plugins: stored under `plugins/` in the app data directory. Plugin packages and extracted files live here (managed by `PluginService`). Plugin-specific settings are saved in `config.json` under `GlobalSettings.Plugins` or in plugin folders.
- Backup metadata: written to the backup target or metadata directories as part of backup operations; these are stored on user-controlled storage locations.

Network usage

- Plugin store and downloads: FolderRewind may query GitHub Releases for plugin repositories (for example `https://api.github.com/repos/{owner}/{repo}/releases/latest`) and download release assets. Plugin-related requests use a User-Agent of `FolderRewind/1.0`.
- Updates: When distributed via the Microsoft Store, application updates are delivered and managed by the Microsoft Store infrastructure. FolderRewind itself does not perform automatic update uploads; the app does not contact arbitrary update servers to push user data.
- Plugins: plugins run locally and may perform network operations if implemented by the plugin author — such behavior is outside FolderRewind's control. Review plugin sources and permissions before enabling.

Logs and crash information

- Logs are used for local troubleshooting. If you choose to share logs (for example when reporting an issue), they may contain file paths or error stacks — please review before sharing and remove sensitive content.

Deleting data

- To remove all application data, exit FolderRewind and delete the app LocalState folder (example path shown above) or the relevant `%LOCALAPPDATA%` app data directory when unpackaged.
- Plugins can be uninstalled via the Settings page; you may also delete the `plugins/` folder to remove plugin files.

Permissions

- File system access: for reading/writing configs, logs, plugins and performing backups.
- Network access: for plugin downloads and optional UI-initiated actions; updates are handled by Microsoft Store when distributed there.
- System info: only limited environment info may be recorded for diagnostics and troubleshooting; not used to identify individual users.

Third parties

- Plugins are provided by third parties. FolderRewind restricts plugin storage to the app data directory, but plugins may access user-specified paths. Any network activity performed by plugins is the responsibility of the plugin author.

Changes & Contact

- Privacy policy updates will be published in the app or on the project page. For questions, open an Issue on the project's GitHub page.

> Last Updated: 2026-01-12