# Privacy

DiskUsageAnalyzer processes scans and file comparisons locally. It contains no accounts, analytics, telemetry client, automatic updater, cloud upload, advertising, or network request code. Standard disk scans read metadata; opt-in duplicate analysis reads resident file contents. Explorer reveal and manually exporting CSV are explicit actions.

The current scan index and cleanup/recovery history are stored in `%LOCALAPPDATA%\DiskUsageAnalyzer\scan.db`, including file paths, sizes, identities, and operation outcomes. `--data-dir <folder>` chooses a different local data location. Each new scan replaces the working scan index; cleanup history is retained. Named snapshot/import features are not included.

Recovered content stays on its original volume in `.DiskUsageAnalyzer-Recovery` beneath the original parent directory. It is never uploaded or automatically purged. Recovery folders and history survive uninstall. A report contains private paths and filenames: redact it before sharing. No diagnostic report is sent automatically.

The application does not attempt to control networking performed by Windows, antivirus software, Explorer, a sync provider, or a package manager used by developers. Known sync locations/placeholders are excluded from cleanup; an unrecognized provider is a documented limitation.

This application has no “clear history” or automatic retention purge. Persistent data can be inspected or managed manually with the application closed; preserve history while recovery files exist. Removing the history database removes the original-path index and must be a deliberate external action.
