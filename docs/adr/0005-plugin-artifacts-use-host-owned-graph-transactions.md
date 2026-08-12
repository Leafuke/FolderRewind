---
status: accepted
---

# Plugin artifacts use Host-owned graph transactions

Plugin System v3 allows trusted in-process plugins to transform staged backup artifacts and materialize plugin-owned formats, but it does not provide free post-backup mutation or Backup/Restore Engine takeover. The Host owns immutable Artifact identities, dependency graphs, copy-on-write transaction commit, encryption envelopes, History roots, reachability-based retention/garbage collection, Cloud, integrity, Safe Restore, and target mutation; a transformer proposes a graph patch from Host staging, while a restore materializer reconstructs content only into an isolated Host workspace. This boundary lets semantic formats such as reverse MCA deltas participate without recreating the lifecycle, chain-poisoning, deletion, and Cloud inconsistencies demonstrated by post-backup archive rewriting.

## Consequences

- Core Full/Smart/Overwrite capture remains the primary backup engine; an optional transformer is an orthogonal, explicitly selected Artifact policy.
- A transformer may be owned by a different plugin than the Config Kind, but only through an explicit user-owned policy and static compatibility declaration; the Host never discovers cross-plugin middleware by claim order.
- Transformation occurs after Core primary capture, so it can optimize retained history representation but does not claim to reduce source scanning or primary capture cost; content-aware capture remains a separate future engine boundary.
- Committed Artifact payloads are immutable. Replacement uses copy-on-write staging plus a journaled graph/root switch; deleting a History root does not delete still-reachable dependencies, and explicit deletion of a reachable Artifact is blocked.
- Restore Mode, Restore Materializer, and Restore Coordinator remain separate: the materializer builds an isolated source tree, the Host applies it to the destination, and the coordinator surrounds that once-only mutation.
- A read-only completion observer may perform post-commit integration work, but it cannot alter Artifact graph state or retroactively change a successful core result.
