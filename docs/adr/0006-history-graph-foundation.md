---
status: accepted
---

# History is an immutable graph synchronized by Commit Pack union

History uses immutable facts and Commit Pack union so concurrent device histories are preserved instead of overwritten. Logical Source Versions, physical Representations and storage Replicas have distinct identities; rebuildable indexes and device-local state are not shared authority.

The persistence, integrity and transaction constraints are maintained in [History integrity](../specifications/history-integrity.md). Source ownership of branches and checkpoints is governed by [ADR 0007](0007-source-owned-history-branches.md), which supersedes the original configuration-wide ownership. Durable publication cannot be retracted by a later local cleanup failure.
