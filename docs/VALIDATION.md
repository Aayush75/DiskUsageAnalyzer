# Validation evidence

Local validation: 8 October 2026, Windows 11 x64 build 26200, 16 logical processors, fixed local NTFS, SDK 10.0.401/runtime 10.0.12. Exact raw outputs, coverage XML, fixture directories and rendered screenshots are in ignored `artifacts/`. No user files were used as destructive fixtures.

## Automated checks

The core suite preserves unrelated/future-version databases before any schema changes and covers authorization denial/expiry/reuse/concurrency/session changes, immutable manifests, protected/boundary/device paths, misleading detector structures, paging, cancellation/partial scans, interrupted history, and assembly dependency boundaries. Real Windows tests verify recovery content/restore collisions, exclusive locks, file replacement/modification, ancestor rename guards, hardlinks, junctions, long Unicode paths, read-only files, duplicate keeper content and newly created unapproved files.

The production-window WPF harness verifies scanning, review-only selection, the actual confirmation modal, default Cancel, acknowledgment gating, Cancel/modal close, approved recovery, separately confirmed permanent deletion/recovery removal, unselected sentinels, duplicates, themes and compact layout. Packaged smoke mode launches the actual published executable through Windows UI Automation with global .NET disabled, verifies a completed scan, checks that `coreclr.dll` comes from its publish directory, and closes it cleanly.

Final validation counts: **71 core tests, 38 WPF/packaged assertions, and 16 installer assertions plus 6 installed-executable assertions**. No core tests were skipped. ConsentGate, CleanupManifest and CleanupExecutor have 100% measured line and branch coverage; PathPolicy has 100% line / about 97% branch coverage. This evidence does not substitute for OS-level integration tests, which also passed.

The installer journey uses a unique folder with spaces and Unicode, refuses to replace any pre-existing registered installation, checks installation and same-version reinstall, and launches the installed executable with global .NET disabled. It activates the real uninstall confirmation's No and Yes buttons separately, proving cancellation preserves installation and approved uninstall retains unowned files, scan data and a recovery fixture. This local journey does not establish compatibility with older release versions or substitute for independent clean-VM testing.

## Recorded scalability measurements

The final transitive NuGet advisory audit reported no known vulnerable packages for all ten projects. Packaging includes exact bundled-runtime versions/hashes, runtime MIT/third-party notices, and pinned WPF/SQLitePCLRaw redistribution notices. This records the audit result on the validation date; it is not a guarantee about future advisories.

Both fixtures contain zero-byte regular files in directories of 1,000 files plus one marker file. Measurements are one warm-cache run each, after fixture creation, on this machine; the million-file run overlapped some development work. These are reproducible local observations, not general speed claims.

| Fixture | Metadata scan | First progress | Peak working set | SQLite index |
| --- | --- | --- | --- | --- |
| 100,001 files / 101 directories | 19.36 s | 0.179 s | 71.1 MiB | 35.0 MiB |
| 1,000,001 files / 1,001 directories | 139.89 s | 0.166 s | 73.0 MiB | 360.3 MiB |

The scanner's memory remains bounded while its on-disk index grows with record count. The GUI displays progressive largest-file map entries during scanning, but measured first progress is not a measurement of first rendered UI response. The treemap uses at most 120 visible items; file lists page 200 entries; review is capped at 10,000 files. Duplicate results also have explicit limits to bound retained records. Fixture files are intentionally retained; no automatic recursive teardown exists.

## Visual inspection

Rendered real WPF windows were inspected for the dark/light overview, review queue, confirmation, history and 1024×768 layout. Public overview screenshots use synthetic file contents/names and a demo display path. Native accessible control invocation was used in UI tests. This is not an independent Narrator usability assessment or a measured 95th-percentile input-latency test.

## Remaining release validation

- Independent clean Windows VM without development tooling; multiple supported Windows builds; install/upgrade/uninstall end-to-end outside the development host.
- Real configured cloud-placeholder providers/no-hydration checks, unusual filesystem filters, disk-full injection, forced process termination at each native/journal boundary, and rotational/removable/network device behavior. Such paths remain excluded from cleanup rather than assumed safe.
- DPI scaling beyond the current host, mixed-monitor moves, screen-reader/high-contrast testing, and independent feedback from unfamiliar users.
- Mutation testing that deliberately removes each safety guard, full per-branch fault injection, statistical UI-latency sampling, and cold-cache/repeated/hardware-varied benchmarks.
- Code signing and Windows reputation checks, GitHub CI execution/branch protection and verified private vulnerability reporting. No public repository/release currently exists.

These gaps prevent calling the initial build a fully field-validated stable v1. The application works and local critical workflows pass; distribute as an unsigned prerelease with these limits until the external release checks are recorded.
