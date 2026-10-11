# Data migration P0/P1 acceptance — 2026-10-11

Baseline: `1.9.x / 092a556`. GUI automation is explicitly excluded; the checks below require manual completion before release. No production backup data needs to be moved or deleted for acceptance.

## Implemented boundaries

- Configuration is prepared now and applied on restart, with a latest-config safety copy and rollback of interrupted activation.
- v2 history export selects projects explicitly; v1 single-project files remain readable. A batch commits per repository.
- Project/source identities remain unchanged. Orphans wait for the original configuration.
- Controlled source/path setup creates Unknown local state and an empty catalog where missing, preserves valid state and rejects corruption.
- Archives are **unverified**. This change neither scans payload availability nor claims that copied archives can be restored automatically.

## Automated evidence

- `FolderRewind.Tests`: **961 passed, 0 failed, 0 skipped**, using the repository's `7za.exe` through `FOLDERREWIND_TEST_7Z` (48 seconds). A previous run without the override had two failures starting the WindowsApps `7z.exe` execution alias; no archive-tool production change was made.
- x64 Debug packaged application build: **0 warnings, 0 errors**, including the existing WinUI analyzer.
- Website `npm run check:all`: passed translation/release script tests, bilingual parity, type checks, documentation contracts/images/SEO, both locale builds and built-route/anchor checks.
- No UI automation or manual GUI acceptance was performed.


The regression suite covers retained original Packs, interrupted cross-Pack unions, cancellation before a decision, duplicate/identity conflicts, v1/v2 imports, multiple projects, partial failure/retry, unloaded matching configuration, damaged ZIP structure, changed files after preview, local-state replay and corruption protection.

Configuration tests cover a blocked old writer while a candidate is prepared, activation after queue drain, latest-state rollback after interrupted startup, damaged candidates, cancellation and setup confirmation belonging to one import even for identical bytes.

Functional tests verify an independent full backup after migration without invented ancestry/dependencies, and the existing filesystem restore/Checkout transactions after Unknown-state initialization using the deterministic test materializer. They do not verify real-world archive availability.

## Manual acceptance (not executed by the agent)

1. Create two disposable projects with distinct sources, branches and backup directories. Export original configuration and history. Save independent copies of the original test environment.
2. Export history with all projects selected; repeat with a subset. Confirm project names/IDs and the metadata-only description, including a project with no history. A project with a missing bound repository must be reported instead of silently omitted.
3. Import configuration from local JSON: inspect the project/source/path preview, cancel once, then prepare it. Confirm the running configuration stays unchanged and normal saves cannot replace the candidate. Test Cancel pending import.
4. Prepare again and use Restart and apply configuration. Confirm a new process, original project/source IDs, disabled automatic backup/cloud uploads and accessible Complete migration setup. Repeat with a verified cloud configuration connection and inspect its downloaded preview.
5. With a long test backup/cloud transfer active, attempt restart. Confirm it waits or reports a recoverable exit failure, retains the pending candidate, and leaves the current application usable.
6. In a fresh disposable target, test configuration-first and history-first order. Inspect association, Pack counts, local initialization requirements and archive-unverified wording. Importing history first must not silently create a new differently identified project.
7. Complete setup: preview a root replacement (e.g. `D:\Games` → `E:\Games`), inspect each source ID/path, make a per-source adjustment and confirm. Test missing source, destination permissions/overlap, missing bound history, unavailable required plugin and missing encryption material. Errors must retain a usable correction route.
8. After setup, perform the first backup with Smart mode configured. Confirm it is self-contained and starts a separate branch. Use a test version with a registered payload to restore/Checkout; existing protection and dependency checks must still apply.
9. Reimport history with Runtime loaded and unloaded. Repeat the same bundle. Confirm deduplication, preserved valid local state, accurate statuses and refreshed history display.
10. Test a batch with one conflicting project and a second valid project. Confirm the valid project stays committed; retry is safe. Review diagnostic logs for identity/permission/disk errors rather than generic swallowed failures.
11. Check dialogs and status messages with keyboard-only input, 100%/150% scaling, light/dark/high-contrast themes and both supported languages. Record screenshots and any clipping or focus issues.

## Release gate

Require successful .NET regression tests, an x64 application build with the existing WinUI analyzer, documentation parity/contracts/build checks, and recorded manual results for the applicable installation channels. A missing manual result is pending acceptance, not a pass.
