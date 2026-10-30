# Contributing to FolderRewind

Code, documentation, reproducible issues, test feedback, plugins and templates are welcome. Include the application/system version, reproduction steps, expected behavior and actual result in issues.

- Follow [AGENTS.md](AGENTS.md) for module boundaries, localization and validation. Preserve existing data, public plugin contracts and user changes.
- Use Conventional Commits (`type(scope): subject`), primarily Chinese subjects. Each change should have one explainable purpose.
- Keep MVVM; put user-visible strings in both resource languages. Use the existing configuration and notification entry points.
- Run `scripts/test/Test-RepositoryLayout.ps1`, the affected tests and the appropriate build. Core safety changes require meaningful regressions; UI acceptance uses winapp.
- Normal tests reference production Core. Run recovery acceptance with `FolderRewindFaultInjection=true`; its outputs must not enter a shipping payload.
- User/developer guides belong to the bilingual website. Host docs retains terminology, effective ADRs and safety/format specifications. Plans, progress and run evidence belong in ignored artifacts.

See the [development guide](https://folderrewind.top/en/docs/architecture/development) and [documentation policy](docs/README.md). Explain what changed, why, actual validation and remaining limits in your pull request. File moves and behavior changes should be reviewed separately.
