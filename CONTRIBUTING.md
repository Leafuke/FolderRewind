# Contributing to FolderRewind

Thank you for your interest in contributing to FolderRewind. This project welcomes code, documentation, test feedback, templates, plugins, and issue reproductions.

## How to Help

- Submitting an Issue: Please include your system version, FolderRewind version, steps to reproduce, expected result, and actual result.
- Submitting a Pull Request: It is recommended to open an issue or a draft PR first to explain your motivation, so as to avoid misalignment after the work is done.
- Improving documentation: Typos, usage instructions, screenshots, and FAQs are all valuable. This is especially true for improvements to the [official documentation site](https://folderrewind.top/).
- Sharing templates or plugins: If your workflow can help other users, you are welcome to package it as a template or plugin.

## Development Notes

- Please try to maintain the MVVM pattern: place business logic in Service/ViewModel, and keep XAML.cs only for UI event bridging.
- User-visible text must go into `Strings/zh-CN/Resources.resw` and `Strings/en-US/Resources.resw`.
- When dealing with settings, prefer reusing `ConfigService`; when dealing with notifications, prefer reusing `NotificationService`.
- Before submitting, please run at least the following command:

```powershell
dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --no-restore
```

## Pull Request Checklist

- Build passes.
- Do not remove existing comments unless they are incorrect or misleading.
- Do not break compatibility with old configurations and historical data.
- New user-visible text has been supplemented with both Chinese and English resources.
- If the core backup/restore/history/plugin flow has been modified, please describe the manual verification path.

## Sponsor Edition and Contributions

FolderRewind is free and open-source software. The Sponsor Edition on the Microsoft Store is a voluntary way to support the project’s development and does not affect basic backup functionality. Friends who have made substantial contributions to the project will be gifted the Sponsor Edition by me.