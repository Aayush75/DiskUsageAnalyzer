# Using DiskUsageAnalyzer

## Install or run

Use the Windows x64 installer for a per-user install, or extract the portable ZIP completely and open `DiskUsageAnalyzer.App.exe`. Keep the accompanying DLLs and native files together. No .NET SDK, account, or administrator session is required. The initial build is unsigned; verify its SHA-256 checksum against the release's checksum file and expect Windows trust warnings.

## Explore

Application data defaults to `%LOCALAPPDATA%\DiskUsageAnalyzer`. A custom `--data-dir` must be a new empty folder or recognized application data. Unrelated `scan.db` files and newer database versions are rejected without adoption or schema changes; preserve existing files and choose a new folder instead.

Enter a local folder or choose Browse/a drive, then select Scan storage. Scanning reads metadata and does not clean anything. Stop produces an explicitly partial scan; inaccessible and linked areas are reported as skipped. Protected and unknown space means the result may not match Windows' volume-used total.

Overview shows logical size, file count, volume free space, a map, and file types. Explore lists folders/files in pages of 200. Open a folder, go Up, or choose Largest files. On disk uses allocation metadata; unknown/hardlinked allocation is not treated as reclaimable bytes. The map groups smaller entries into Other. Use the list as the keyboard-accessible alternative.

Cleanup findings recognize specific structures and explain consequences. “Low risk” is not a guarantee that you do not need a file. A `bin` folder alone is not considered a .NET artifact, and JavaScript dependencies require project and lockfile markers. No detector claims to prove that dependencies or environments are unused.

Duplicate files is opt-in and reads contents locally. It verifies hashes with byte comparisons. Select a group, choose the copy to KEEP, and select another copy to review. Identical contents do not mean both paths are dispensable. Limits are disclosed in the result status: 500 groups, 10,000 result files, 2,000 candidates per size and 5,000 sizes.

## Delete deliberately

Add a file or folder to review. Folder selections expand to scanned regular files; the folder itself and any subsequently created files are not recursively deleted. Inspect all full paths and explanations in Review queue. There is a 10,000-file batch limit. Nothing is preselected.

Delete selected opens an **Are you sure?** modal. Cancel is focused/default and Escape cancels. The positive button remains disabled until you acknowledge that you reviewed the paths and consequences. Closing the modal is cancellation. No preference removes this confirmation.

The default mode moves exact files into a **local recovery folder**, using their retained Windows handles. It does not use the Windows Recycle Bin and does not free storage. History offers Restore, which never replaces an existing original-path file. Keep the history database and recovery folders together; no recovery file expires automatically.

To actually reclaim storage, deliberately choose permanent deletion before confirming, or select a recovered file in History and choose Permanently delete recovered file. Each requires a new irreversible-loss confirmation. There is no built-in undo for permanent deletion.

Locked, changed, linked, protected, or unsupported files are skipped/failed with reasons. Operations are sequential; Stop prevents later items from starting but cannot reverse a committed operation. Recovery and permanent-deletion outcomes are recorded in History. Observed free-space deltas can include unrelated disk activity and delayed deallocation; they are not a promise of precise reclamation.

## Interrupted operation

Pending/Unknown history rows mean the process stopped before its outcome could be reliably recorded. No operation resumes automatically. Inspect the original path and the recorded recovery folder manually. Close the app and preserve the database before troubleshooting. Do not blindly repeat a deletion. Recovery folders contain a `.owner` marker and uniquely named `.recovered` files; original paths and identities are recorded in the database.

## Limits

Cleanup supports standard regular files on fixed local NTFS volumes. Known sync locations, placeholders, reparse points, hardlinks, protected system/application areas, network shares, and removable volumes are excluded. Recovery requires a writable original parent and creates a private sibling folder. Unknown sync providers and special filesystem filters may need extra caution. No administrator mode, MFT scanner, Windows servicing, registry cleaner, Docker/WSL prune, browser profile cleaner, named snapshots/import, or scheduled cleanup is included.

Uninstall asks for confirmation and removes installed program files, retaining history, recovery content, and your files. Git/source rollback does not undo filesystem deletion.
