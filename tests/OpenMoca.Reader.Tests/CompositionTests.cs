using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Composition limits (package spec §7.1, contract §3): a member past either limit is not resolved.</summary>
public sealed class CompositionTests : IDisposable
{
    private const string Node = "---\ntype: Note\ntitle: A\n---\n\n# A\n\nText about refunds.\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-composition-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Members_PastTheDepthOrCountLimit_AreNotResolved()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] leaves = [Package("leaf1", "{\"id\":\"https://example.com/leaf1\",\"version\":\"1.0.0\",\"title\":\"L\"}", Node), Package("leaf2", "{\"id\":\"https://example.com/leaf2\",\"version\":\"1.0.0\",\"title\":\"L\"}", Node)];
        var digests = new List<string>();
        foreach (var leaf in leaves)
        {
            digests.Add((await MocaReader.ReadPackageAsync(leaf, cancellationToken: ct)).Digest!);
        }

        var pins = string.Join(",", digests.Select((d, i) => $"{{\"id\":\"https://example.com/leaf{i + 1}\",\"version\":\"1.0.0\",\"digest\":\"{d}\"}}"));
        var composite = Package("c", $"{{\"id\":\"https://example.com/c\",\"version\":\"1.0.0\",\"title\":\"C\",\"members\":[{pins}]}}", null);
        Func<MemberReference, CancellationToken, ValueTask<PackageTarget?>> resolve =
            (m, _) => ValueTask.FromResult<PackageTarget?>(leaves[m.Id[^1] - '1']);

        var ok = await MocaReader.ReadPackageAsync(composite, new ReadOptions { ResolveMember = resolve }, ct);
        var shallow = await MocaReader.ReadPackageAsync(composite, new ReadOptions { ResolveMember = resolve, Limits = new ReaderLimits { MaxMemberDepth = 0 } }, ct);
        var few = await MocaReader.ReadPackageAsync(composite, new ReadOptions { ResolveMember = resolve, Limits = new ReaderLimits { MaxMembers = 1 } }, ct);

        Assert.Equal(["resolved", "resolved"], ok.Members.Select(m => m.Status));
        Assert.Equal([DiagnosticCodes.R001MemberUnresolved, DiagnosticCodes.R001MemberUnresolved], shallow.Diagnostics.Select(d => d.Code));
        Assert.Contains("deeper than 0 levels", shallow.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(["resolved", "unresolved"], few.Members.Select(m => m.Status));
        Assert.Contains(few.Diagnostics, d => d.Message.Contains("more than 1 members", StringComparison.Ordinal));
    }

    private string Package(string name, string manifest, string? node)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"), manifest);
        if (node is not null)
        {
            File.WriteAllText(Path.Combine(dir, "content", "a.md"), node);
        }

        return dir;
    }
}
