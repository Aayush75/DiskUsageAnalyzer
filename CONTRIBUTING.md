# Contributing

Use Windows 11 x64 and the .NET SDK pinned in `global.json`. Run `./eng/Build.ps1 -UiTests -Coverage` before submitting changes. The UI harness must run in a Windows desktop session and exercises real production windows/commands on newly created fixture data. Never point destructive tests at real user Temp, documents, caches, or drive roots.

Architecture: Core contains models/consent/policy; Scanning streams into Storage; Analysis recognizes read-only findings; Windows isolates native metadata/handles; Cleanup authorizes/revalidates mutations; App composes WPF views and commands. Scanner/rule assemblies must not reference mutation assemblies. Contracts and safety assertions matter more than a blanket coverage percentage.

Use focused commits with relevant tests. Preserve history; revert shared changes instead of rewriting them. Do not suppress audit failures, remove confirmation, add recursive/path-based deletion, auto-select files, auto-purge recovery, or weaken identity guards to make a feature work. New detectors need positive and misleading-name/structure fixtures, narrow exclusions, a plain-language consequence, and regeneration limitations. Folder age/name alone never proves unused data.

Include before/after behavior and test evidence in pull requests. Native changes need real NTFS replacement/locking tests and a safety ADR. Accessibility changes need keyboard and actual rendered-window checks. Dependency updates update lockfiles and rerun all gates. Use synthetic data in screenshots; redact personal filenames.

Benchmarks run with `dotnet run --project benchmarks -c Release -- 100000 artifacts/benchmarks`. Fixtures are intentionally retained; they are not recursively removed automatically. Performance claims must name hardware, OS, cache state, dataset, sample count, and any concurrent workload.

The project uses Apache-2.0. Contributions must be compatible with that license; retain third-party notices and do not copy cleanup definitions from incompatible projects.
