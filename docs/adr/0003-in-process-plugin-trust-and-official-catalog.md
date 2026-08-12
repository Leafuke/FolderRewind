---
status: accepted
---

# In-process plugins use a curated catalog, not a security sandbox

FolderRewind v3 continues to run code plugins in-process because process isolation is outside the 1.9.0 scope, so requested Host Services are compatibility declarations and user-facing disclosures rather than security permissions. Official discovery and updates come from a separately governed catalog that names an exact artifact and SHA-256; GitHub Releases host artifacts but do not establish trust, and package manifests cannot self-declare Official or Trusted status. Publisher signatures and arbitrary catalog sources were rejected for v3.0 to keep the first trust model reviewable without pretending that hash validation can sandbox malicious code.

## Consequences

- Manual packages and catalog packages use the same static validator and Plugin API.
- Installation never activates plugin code or runs install scripts, and newly installed plugins start disabled.
- UI must state that enabled plugins run with the same user privileges as FolderRewind.
