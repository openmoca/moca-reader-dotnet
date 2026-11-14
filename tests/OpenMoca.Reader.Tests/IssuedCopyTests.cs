using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Attestations §3a and §7.1. The corpus covers the rules; these cover what it cannot.</summary>
public sealed class IssuedCopyTests
{
    private static readonly string Fixtures = Path.Combine(MocaSpec.Conformance, "fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject TrustDocument() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "trust-root-issued.json")))!;

    private static ReadOptions Options() => new() { TrustRoot = TrustRoot.Load(TrustDocument()), Clock = () => Now };

    [Fact]
    public void ATrustRoot_ThatGivesIssuedCopyWithoutIds_IsRefused()
    {
        var doc = TrustDocument();
        var entry = (doc["keys"] as JsonArray)!.OfType<JsonObject>().First(k => (k["roles"] as JsonArray)!.Any(r => r!.GetValue<string>() == "issued-copy"));
        entry.Remove("ids");
        Assert.Throws<ArgumentException>(() => TrustRoot.Load(doc));
    }

    [Fact]
    public void Keys_AreComparedAsEd25519Bytes_NotAsPemText()
    {
        var pem = File.ReadAllText(Path.Combine(MocaSpec.Root, "fixtures", "signing-keys", "INSECURE-issuer.pub.pem"));
        var folded = pem.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(Dsse.Ed25519KeyBytes(pem), Dsse.Ed25519KeyBytes(folded));
        Assert.Null(Dsse.Ed25519KeyBytes("not a key"));
    }

    [Fact]
    public async Task AStoreSearch_RecomputesIssuedCopyAndIssuedChange_FromThePackageState()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "issued", "copy-valid"), Options(), ct);
        var json = JsonSerializer.Serialize(PackageState.From(result));
        var state = JsonSerializer.Deserialize<PackageState>(json)!;
        Assert.Equal(["content/lessons/one.md"], state.IssuedCopy!.ChangedFiles);
        var store = await new MemoryStoreBackend(filterPushdown: false).IngestAsync(new Library(new LibraryOptions { Clock = () => Now }).Add(result), ct);
        var records = await new MocaSearch([state], () => Now, store).SearchAsync("lesson", new SearchOptions { Limit = 10 }, ct);
        var one = records.Single(r => r.Node.Path == "lessons/one.md");
        Assert.True(one.Node.IssuedChange);
        Assert.Equal(result.IssuedCopy!.OriginalDigest, one.Package.IssuedCopy!.OriginalDigest);

        // Against a state without the issued copy (an issuer the host no longer trusts), both are gone.
        var again = await new MocaSearch([state with { IssuedCopy = null }], () => Now, store).SearchAsync("lesson", new SearchOptions { Limit = 10 }, ct);
        Assert.All(again, r => Assert.True(r.Node.IssuedChange is null && r.Package.IssuedCopy is null));
    }

    [Fact]
    public async Task AMalformedChangedFilesEntry_IsA002_NeverAnException()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "issued", "copy-valid"), Options(), ct);
        var statement = (JsonObject)JsonNode.Parse("""{"subject":[{"name":"x","digest":[1]}],"predicateType":"https://w3id.org/moca/attestation/issued-copy/v1","predicate":{"original":[],"changedFiles":[]}}""")!;
        var check = IssuedCopies.Check(statement, new SignatureCheck("valid", "s", null, [], null), result.Manifest!, result.Digest!, result.FileDigests, true);
        Assert.NotNull(check.Error);
    }
}
