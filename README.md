# DiskUsageAnalyzer

A planned open-source Windows disk space analyzer and cleanup manager, focused on understandable findings, responsive exploration, and explicitly approved cleanup.

**Current status:** implementation in progress. The scanner, local storage, findings, duplicate analysis, and confirmed native cleanup core have 36 passing Windows/unit tests. The desktop UI and release packaging are being built.

Read the [implementation plan](IMPLEMENTATION_PLAN.md) for the selected stack, architecture, safety contract, test strategy, release gates, distribution approach, and commit milestones.

The primary requirement is mandatory approval: scanning and analysis never delete user files. Every cleanup requires an explicit Delete action and an “Are you sure?” confirmation, enforced below the UI. No automatic cleanup or confirmation bypass is planned.

The stack is C#/.NET 10 with WPF, a bounded scanner, local SQLite storage, narrow read-only cleanup rules, and isolated Windows mutation adapters. Read the [safety design](docs/SAFETY.md), including the deliberate local-recovery alternative to Windows Recycle Bin integration.
