# Cleanup safety and recovery

No application cleanup runs without a positive Delete action and a mandatory confirmation. Cancel is the default button. Confirmation is bound to an immutable preview, mode, session, and five-minute expiry, and is consumed exactly once. Restarting never resumes cleanup. Scanning and analysis do not reference the cleanup module.

The native backend opens the approved file with exclusive sharing and DELETE access only after authorization. It rechecks volume/file identity, creation/modification timestamps, size, attributes, and link count. Permanent deletion uses disposition on that same handle. A folder selection expands to individual scanned files; it never invokes recursive filesystem deletion. New, locked, changed, linked, protected, or unsupported files are skipped.

Real directory read handles without delete-sharing prevent ancestor renames. Metadata-only handles do not provide that guarantee, as verified during implementation. Source-file exclusive handles prevent replacement/content writes while the operation commits. Duplicate keepers remain open without write/delete sharing and are byte-compared again before removal.

## Decision: local recovery instead of a Shell Recycle Bin handoff

The first implementation uses a **local recovery folder** as the default mode. An identity-bound move through a retained handle preserves the contents on the same volume without an unguarded Shell handoff. This deliberately changes the initial plan's Recycle Bin default. Windows Recycle Bin integration is not enabled or claimed.

Recovery files are stored in `.DiskUsageAnalyzer-Recovery` beneath their original parent, with unique random filenames, a current-user ownership marker, a protected current-user/SYSTEM directory ACL, and a durable SQLite history entry recording the original path and identity. Rename does not replace a destination. The resulting handle path is checked before success is reported. Recovery locations are excluded from scans and ordinary cleanup. Empty source folders remain in place.

Moving to recovery does **not** free storage. History offers explicitly approved Restore, which preserves the original file identity and never overwrites a newly created file. Permanent deletion is a separate option with irreversible-loss wording, an acknowledgment checkbox, and a separate confirmation. Recovery files are never expired or purged automatically. Preserve the application history database; its entries are the original-path index. Individual recovery folders can be inspected manually if history is unavailable.

## Failures and boundaries

An operation writes durable intent before mutation and records its outcome afterward. A crash or database failure may leave Pending/Unknown entries. These are displayed for manual inspection and never replayed. Cancellation stops subsequent files; an already committed operation may complete. No atomic batch rollback or permanent-deletion undo is promised.

Cleanup supports regular files on fixed local NTFS volumes. Junctions, symbolic links, hardlinks, alternate streams, cloud placeholders, known synchronized paths, system/installer areas, and application storage are excluded. Unrecognized sync tools and filesystem filters remain a limitation. Root paths are normalized and protected locations require segment-boundary matching.

This design prevents accidental cleanup and normal file-change races within supported Windows semantics. It is not a sandbox against malicious administrators, kernel/filesystem failures, or arbitrary malicious code with the same user privileges. Sharing modes do not prevent all metadata writes; identity and eligibility checks supplement them. No security claim extends beyond the tested conditions.

Native tests mutate only uniquely created disposable fixture files. They intentionally do not recursively remove fixtures after testing. A source-code rollback cannot restore permanently deleted user files.
