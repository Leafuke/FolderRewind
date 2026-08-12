# FolderRewind Plugin Abstractions

Public, BCL-only contracts for FolderRewind Plugin API 3. Plugins reference this
package instead of the FolderRewind application or UI projects.

## Compatibility

- Target framework: `net10.0`.
- Package/API version: `3.0.0`.
- Assembly version remains `3.0.0.0` throughout API 3.x.
- A Host accepts a manifest only when its API major matches and its minor is at
  least the plugin's requested minor.

API 3 exchanges immutable snapshots, drafts, patches, requests, results, and
descriptors. Plugins register capabilities during `ActivateAsync`; the Host
validates and commits the activation before those capabilities become visible.
Activation may read settings and snapshots but cannot use the plugin DataStore.

Manifest declarations and settings schemas are static package data. Requested
Host services are compatibility and user-consent declarations, not a security
sandbox. Plugins execute in the FolderRewind process and must therefore be
trusted before installation.
