# FolderRewind Plugin Abstractions

Public, BCL-only contracts for FolderRewind Plugin API 3. Plugins reference this
package instead of the FolderRewind application or UI projects.

## Compatibility

- Target framework: `net10.0`.
- Package/API version: `3.1.0`.
- Assembly version remains `3.0.0.0` throughout API 3.x.
- A Host accepts a manifest only when its API major matches and its minor is at
  least the plugin's requested minor.

API 3 exchanges immutable snapshots, drafts, patches, requests, results, and
descriptors. Plugins register capabilities during `ActivateAsync`; the Host
validates and commits the activation before those capabilities become visible.
Activation may read settings and snapshots but cannot use the plugin DataStore.

API 3.1 adds the optional `IDiscoveryDefinitionCatalog` metadata extension.
An existing discovery capability may implement it to expose stable game
definitions to Host game discovery and provider-targeted backup presets. It is
not registered or declared as a separate capability, so API 3.0 discovery
plugins remain compatible but do not participate in those definition-based
flows.

Catalog definitions must be non-null, use non-empty `DefinitionId` values that
are unique within the provider using ordinal comparison, and expose non-null
alias and external-ID collections. `ResolveDefinitionId` may return only one of
those declared IDs or `null`.

Config Reconciliation returns revision-bound proposals; it never saves a user
configuration directly. Backup Artifact extensions operate only through
Host-owned immutable graph transactions and bounded staging. Restore
Materializers write an isolated Host workspace, while the Host retains
preflight, integrity, Safe Restore, and target mutation ownership.

Manifest declarations and settings schemas are static package data. Requested
Host services are compatibility and user-consent declarations, not a security
sandbox. Plugins execute in the FolderRewind process and must therefore be
trusted before installation.
