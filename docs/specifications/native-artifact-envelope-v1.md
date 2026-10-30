# Native Artifact Envelope v1

This specification describes the current storage envelope. Capture eligibility and plugin capabilities are governed by Host policy and the public contract; historical P2/P3 implementation plans are not the current format specification.

## Storage identity

`NativeArtifactStore.ForConfig` selects `<Destination>/.folderrewind/native-artifacts-v2/<SHA256(ConfigId)>`, using the lowercase UTF-8 hash. This root is separate from the History metadata repository and isolates configurations sharing a destination. The existing Artifact ledger owns DAG identities and dependencies.

A sealed node has a canonical `artifacts/<ArtifactId in N format>` relative path and contains `envelope.json` and `payload.7z`. The envelope is serialized by the existing .NET serializer with these fields:

```json
{"Version":1,"Encrypted":true}
```

Only envelope version 1 is accepted by the current decoder. `Encrypted` describes the storage blob; passwords and compression remain Host-owned and are not passed to plugins. Ledger facts distinguish logical tree hash/size from compressed storage hash/size. Reading verifies the relevant storage and logical facts under bounded extraction; committed storage does not retain an extra plaintext tree.

## Transactions and leases

The storage root uses an in-process gate and an exclusive cross-process mutation handle. Reads, capture, import, repair, GC and compatibility checks retain their required leases. Temporary workspaces are Host-owned and operation-scoped. Canonical paths, reparse-point checks, ownership evidence and extraction budgets remain mandatory.

Pending capture and transfer intents protect uncommitted work. History Pack durability establishes the committed fact; recovery completes local state without replaying plugin code or deleting unrelated existing nodes. Malformed or conflicting intent cannot authorize cleanup. Missing providers or disabled plugins do not delete History or Artifact facts.

## Transfer and repair

The store retains its closure-bundle transfer path and the object-closure path. Object-closure descriptors use version 2, retain envelope version 1, and validate ConfigId, FolderId, root identity, DAG and canonical node paths. The current descriptor reader is bounded to 16 MiB and 4,096 objects. Object keys include the configuration hash and the storage SHA-256, ending in `.7z`.

Replica publication must follow the selected transfer protocol's completed payload and integrity checks. Reuse is governed by the transport's configured verification policy; it does not always imply fresh content hashing. Download, materialization and repair still validate their expected identities, lengths, hashes, bounds and graph bindings before publishing usable local state. Mutable ledger state is not a replacement for immutable cloud facts.

Source-code relocation does not change this storage root, envelope version, public plugin API or existing compatibility rules. The regression suites cover real archives, bounds, cancellation, missing/corrupt objects and recovery; generated evidence belongs in ignored artifacts.
