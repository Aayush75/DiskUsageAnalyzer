# DiskUsageAnalyzer

A planned open-source Windows disk space analyzer and cleanup manager, focused on understandable findings, responsive exploration, and explicitly approved cleanup.

**Current status:** planning only. No application or executable has been implemented yet.

Read the [implementation plan](IMPLEMENTATION_PLAN.md) for the selected stack, architecture, safety contract, test strategy, release gates, distribution approach, and commit milestones.

The primary requirement is mandatory approval: scanning and analysis never delete user files. Every cleanup requires an explicit Delete action and an “Are you sure?” confirmation, enforced below the UI. No automatic cleanup or confirmation bypass is planned.

The proposed stack is C#/.NET 10 with WPF, a bounded scanner, local SQLite storage, narrow read-only cleanup rules, and isolated Windows mutation adapters. The first release will provide self-contained Windows packages without accounts or telemetry.
