---
status: accepted
---

# In-process plugins use a curated catalog, not a security sandbox

FolderRewind v3 continues to run code plugins in-process because process isolation is outside the 1.9.0 scope. Requested Host Services are enforced gates around FolderRewind's formal service façade and are also user-facing disclosures, but they are not .NET or OS security permissions. Official discovery and updates come from a separately governed catalog that names an exact artifact and SHA-256; GitHub Releases host artifacts but do not establish trust, and package manifests cannot self-declare Official or Trusted status. Publisher signatures and arbitrary catalog sources were rejected for v3.0 to keep the first trust model reviewable without pretending that API gating or hash validation can sandbox malicious code.

## Consequences

- Manual packages and catalog packages use the same static validator and Plugin API.
- Installation never activates plugin code or runs install scripts, and newly installed plugins start disabled.
- Static installation validation may inspect archives, Manifest/settings JSON and PE/assembly metadata, but must not load the candidate assembly, create plugin objects or run lifecycle methods.
- A plugin can call only the formal Host services declared by its Manifest; it still has the ambient operating-system privileges of the FolderRewind process.
- UI must state that enabled plugins run with the same user privileges as FolderRewind.
- Artifact read/materialization declarations receive a separate high-impact disclosure because they may expose decrypted backup content; this improves informed consent but is not presented as an enforceable sandbox permission.
