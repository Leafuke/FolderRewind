# Privacy Policy

FolderRewind is a desktop backup manager. By default, data is processed locally on your device. The application does not include telemetry SDKs and does not upload personal files to our servers.

## What we do NOT collect

- No advertising ID, behavior tracking, or analytics profiling.
- No automatic upload of your file contents, backup archives, or personal folders.
- No sale of personal data.

## Local data stored

- Configuration (`config.json`): stores app settings, backup configurations, plugin host settings, and notice/update reminder state.
- Logs (`logs/app.log`): stores local diagnostic entries (timestamp, severity, message, exception stack when available).
- Plugins (`plugins/`): stores installed plugin packages and extracted plugin files.
- Backup metadata: saved in user-selected backup locations.

## Network usage

FolderRewind only performs limited network requests for features you use:

- Notice check: fetches announcement text from this repository's public files.
- Plugin store/update: queries GitHub Releases metadata and may download plugin assets when you choose to install/update.
- App update reminder: on startup, FolderRewind may request the latest release metadata from GitHub API (`/repos/Leafuke/FolderRewind/releases/latest`) to compare version tag and display release notes.
  - The request reads public release metadata (e.g., `tag_name`, `body`, `html_url`) only.
  - Release notes may contain Chinese/English sections separated by `---`; the app displays the section matching current UI language.
  - The app does not upload your files, backups, or folder list during this check.
- Microsoft Store updates: when installed via Store, binary update delivery is handled by Microsoft Store.

## Plugins and third-party behavior

Plugins run locally but are developed by third parties. A plugin may access user-authorized paths or network endpoints depending on plugin implementation. Review plugin source and trust level before enabling.

## Logs and data sharing

Logs remain local unless you manually share them (for example, in an issue report). Before sharing, review and remove sensitive paths/content.

## Data deletion

- Uninstall the app and remove app data directory (packaged LocalState or unpackaged `%LOCALAPPDATA%` path).
- Remove plugin files by uninstalling plugins in Settings or deleting `plugins/`.
- Remove backup files/metadata from your selected backup target folders.

## Permissions

- File system: required for backup/restore and local config/log/plugin storage.
- Network: required for notice fetch, plugin release metadata/assets, and optional startup update reminder.
- Basic environment info: may be recorded in logs for troubleshooting.

## Changes and contact

This policy may be updated as features evolve. Updates are published in the repository. For questions, open an issue on GitHub.

> Last Updated: 2026-02-28