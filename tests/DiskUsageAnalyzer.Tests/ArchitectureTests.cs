using System.Xml.Linq;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.Tests;

public sealed class ArchitectureTests
{
    [Fact] public void ScannerAndRulesCannotReferenceMutationAssemblies()
    {
        foreach (var assembly in new[] { typeof(Scanning.DiskScanner).Assembly, typeof(Analysis.FindingAnalyzer).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), r => r.Name is "DiskUsageAnalyzer.Cleanup" or "DiskUsageAnalyzer.Windows" or "DiskUsageAnalyzer.App");
    }
    [Fact] public void CoreDoesNotReferenceDesktopOrStorage()
        => Assert.DoesNotContain(typeof(ConsentGate).Assembly.GetReferencedAssemblies(), r => r.Name is "PresentationFramework" or "Microsoft.Data.Sqlite" or "DiskUsageAnalyzer.Windows");
    [Theory] [InlineData(0, "0 B")] [InlineData(1024, "1.0 KB")] [InlineData(1073741824, "1.0 GB")]
    public void UserFacingSizesUseStableBinaryUnits(long value, string expected) => Assert.Equal(expected, ByteSize.Format(value));
}
