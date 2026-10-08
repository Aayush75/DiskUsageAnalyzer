# Third-party notices

DiskUsageAnalyzer is licensed under Apache-2.0. Its dependencies retain their own licenses. Exact resolved versions and integrity hashes are checked into each project's `packages.lock.json`; release packages contain `dependencies.json` and copies of available upstream license/notice files in `licenses/`.

| Component | License | Upstream |
| --- | --- | --- |
| .NET runtime, WPF, and Microsoft.Data.Sqlite | MIT, with additional runtime third-party notices | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf · https://github.com/dotnet/efcore |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SQLitePCLRaw core/provider/config/bundle | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite native library | Public domain | https://sqlite.org/copyright.html |
| xUnit and Visual Studio test runner (development only) | Apache-2.0 | https://github.com/xunit/xunit |
| Coverlet (development only) | MIT | https://github.com/coverlet-coverage/coverlet |
| Microsoft.NET.Test.Sdk (development only) | MIT | https://github.com/microsoft/vstest |

The self-contained release bundles .NET runtime/native components; retain their upstream notices when redistributing. Inno Setup is a build tool and produces the installer under its own distribution terms; it is not linked into application business logic. See https://jrsoftware.org/isinfo.php and the compiler's license for its terms.

Framework-pack licenses and .NET runtime notices are copied from the exact downloaded runtime packages. The vendored WPF notices and SQLitePCLRaw license are pinned in `third-party/versions.json`; packaging refuses a runtime/library version mismatch until these notices are reviewed and updated. The .NET Foundation MIT notice also covers Microsoft.Data.Sqlite.

No WinDirStat, WizTree, BleachBit source code, branding, assets, or cleanup definitions were copied. Research links in the implementation plan are design references.
