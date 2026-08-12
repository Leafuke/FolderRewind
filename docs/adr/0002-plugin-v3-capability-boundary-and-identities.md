---
status: accepted
---

# Plugin v3 uses capabilities and role-specific identities

FolderRewind Plugin System v3 replaces ordered hooks, first-claim provider scanning, and Host object access with a clean-break capability boundary. `PluginId`, `ConfigKind`, `DiscoveryProviderId`, and `StateOwnerId` are separate roles even when MineRewind uses the same reverse-domain text for several of them; plugins propose immutable drafts or patches while the Host owns persistence and operation order. A v2 runtime compatibility layer was rejected because it would preserve the ambiguous ownership and mutable Host coupling that v3 exists to remove, so only one-time data migration remains.

## Consequences

- A plugin may register at most one implementation of each capability contract in an activation session and must compose any internal multiplicity itself.
- Kind-scoped behavior routes through the persisted Config Kind owner; discovery identity never elects a runtime owner.
- MineRewind must compile against the public Abstractions package without referencing the FolderRewind application project.
