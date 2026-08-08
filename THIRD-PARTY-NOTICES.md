# Third-party notices for game discovery

FolderRewind 1.9.0 Beta can consume the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) as an optional game-definition source.

- The Ludusavi manifest repository declares the MIT License for the repository and format implementation materials.
- Its README states that the primary manifest is compiled mainly from [PCGamingWiki](https://www.pcgamingwiki.com/), whose [disclaimer and content terms](https://www.pcgamingwiki.com/wiki/PCGamingWiki:Disclaimer) may impose separate attribution, non-commercial, or share-alike conditions on source content.
- FolderRewind does not treat Ludusavi native cloud metadata as FolderRewind cloud configuration.

## Distribution decision for 1.9.0 Beta

FolderRewind does not bundle, mirror, publish to its own CDN, or commit either `manifest.yaml` or a compiled Ludusavi index. The client fetches the primary manifest directly from the upstream URL only after explicit user action, or imports a local file selected by the user. Derived indexes remain in the user's versioned local cache.

This is the project's conservative distribution boundary pending any additional upstream confirmation. It is an engineering and release decision, not legal advice. Any later redistribution of the manifest or compiled indexes requires a new license review and an explicit recorded decision.
