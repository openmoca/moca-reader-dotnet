using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Every example package in moca-spec reads as valid, and its nodes produce citation records.</summary>
public sealed class ExamplePackageTests
{
    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var manifest in Directory.EnumerateFiles(MocaSpec.Examples, "moca.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(MocaSpec.Examples, Path.GetDirectoryName(manifest)!));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Example_WhenRead_IsValidAndCitable(string example)
    {
        // A composed example (handbook) finds its members in the sibling folders.
        var path = Path.Combine(MocaSpec.Examples, example);
        var options = new ReadOptions { ResolveMember = MemberResolvers.Directories([Path.GetDirectoryName(path)!]) };

        var result = await MocaReader.ReadPackageAsync(path, options, TestContext.Current.CancellationToken);

        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code} {d.File}: {d.Message}");
        Assert.True(result.Valid, string.Join("\n", errors));
        var records = new Library().Add(result).Citations(includeText: true);
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Equal($"{r.Package.Id}@{r.Package.Version}#{r.Node.Path}", r.Node.Ref));
    }
}
