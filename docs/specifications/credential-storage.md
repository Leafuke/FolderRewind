# Credential storage boundaries and revision evidence

## Directory identities

The Host-selected credential/task storage parent may traverse supported directory junctions or symbolic links, including packaged application data redirection. A Windows directory lease binds the link entries and physical directories during access. A managed descendant, credential file, task entry or evidence file must not redirect outside that selected boundary; internal aliases remain unsupported even if they happen to point inside it. Credentials with multiple hard links remain unsupported.

Logical credential paths, StorageKeys, origin markers, task partitions and existing lock names retain their identities. Physical binding does not rewrite persisted references. The same selected-root policy applies to private Artifact workspaces, version export destinations and archive rewrite storage. Core's restore identity types remain in Core; the common Windows directory binding implementation belongs to Runtime, which does not depend on Core.

## Uncertain task evidence

An uncertain task outcome does not itself prove that an unchanged static WebDAV credential is unusable. A token-free WebDAV configuration with the ordinary vendor may admit new calls when its source, original revision and snapshot still agree. The uncertain task, ownership record and snapshot remain intact. This rule never replays the old remote operation and does not waive the Host's job lifecycle or duplicate-operation checks.

OAuth and unsupported authentication modes retain refresh-conflict handling. Changed static snapshots are not discarded or silently merged. Unknown legacy task evidence remains preserved. Enumeration is streaming and cancellable; a count of unrelated legacy directories is not an invalid-evidence diagnosis and cannot impose a global connection limit.

## Revision ledger rollover

The existing `<credential>.folderrewind-revisions.json` remains the mutable ledger head. Version 1 is read without migration until its head reaches 1000 revisions. Version 2 adds a nullable `PreviousBlock` SHA-256 reference and keeps at most 1000 revisions in the head.

Historical blocks live in `<credential>.folderrewind-revision-blocks/<SHA256>.json`. Each block contains `Version = 1`, `TargetRevision`, up to 1000 `Revisions`, and a nullable `PreviousBlock`. Its filename is the uppercase SHA-256 of its exact UTF-8 JSON bytes. Individual head/block documents are bounded to 128 KiB. Readers validate hashes, schema, target identity and reference cycles before accepting archived revision evidence.

When a head fills, persist its immutable block before writing the new credential bytes. Publish the new head after verifying the credential write. The existing attempt journal retains original and refreshed bytes through this transition, so interruption after the credential write can replay ledger publication idempotently. An unpublished block is harmless evidence; it must not authorize a credential revision until reachable from the head. Checkpoint failure before credential publication preserves the old credential and unique refreshed snapshot.

All historic proofs remain reachable, including fresh-origin markers and frozen revisions held by durable jobs or inactive contexts. There is no credential-lifetime revision count cap and no arbitrary truncation of old proofs. This protocol does not authorize automatic deletion of credentials or historical evidence.

Older applications that only understand ledger version 1 cannot use a credential after its first version-2 rollover. Downgrading must retain the original credential and evidence; an explicit new authorization can establish a separate supported credential. Config, origin and snapshot identities are unchanged.

## Scope of file traversal

Source/merge traversal proves exclusions before inspecting an excluded subtree's contents. A whitelist's ancestors may still need traversal even when those directories are not themselves included. Selected, unsupported links are not followed by merge. The System attribute alone does not exclude user files from backup capture. Support for other reparse tags and cloud placeholder hydration requires an explicit capability policy; this specification does not enable following arbitrary source links.
