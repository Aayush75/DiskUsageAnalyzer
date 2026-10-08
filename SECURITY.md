# Security and data-loss reports

Treat confirmation bypass, wrong-object deletion, protected-path escape, unintended permanent deletion, and recovery-content loss as high-priority security/safety issues. Stop using cleanup if you suspect such an issue; preserve history and avoid retrying the operation.

This repository is prepared locally and has not yet been published. No verified private reporting address exists yet. **Before public release, enable GitHub private vulnerability reporting** in the published repository and replace this paragraph with its verified reporting link. Do not publish sensitive paths, file contents, or full scan/history databases in a public issue.

A useful report includes the exact version/artifact checksum, Windows build/filesystem, reproduction on disposable files, expected/actual paths and outcomes with personal details redacted, and whether recovery content remains. Maintainers should acknowledge privately, reproduce in an isolated fixture, add a failing safety test, fix the issue, and publish a clear advisory/update. No remediation should trigger automatic cleanup on affected systems.

Supported security fixes target the latest released version. The current initial build must pass the documented gates before it is represented as a stable release. Signing keys must never enter the repository or untrusted pull-request workflows.
