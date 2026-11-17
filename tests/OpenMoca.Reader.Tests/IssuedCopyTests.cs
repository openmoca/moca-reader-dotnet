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

    /// <summary>Sidecar spec §5: the changed file's items are replaced, and later vector rows stay with their own items.</summary>
    [Fact]
    public async Task ADenseSidecar_BoundToAnIssuedCopy_KeepsEachVectorWithItsItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("moca-sidecar-").FullName;
        var source = Path.Combine(Fixtures, "sidecars", "issued-original");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var index = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "index.json")))!;
        index["indexType"] = "dense";
        index["model"] = new JsonObject { ["name"] = "test-model", ["dimensions"] = 2 };
        ((JsonObject)index["storage"]!)["vectors"] = new JsonObject { ["file"] = "payload/vectors.f32", ["dtype"] = "float32", ["dimensions"] = 2 };
        File.WriteAllText(Path.Combine(dir, "index.json"), index.ToJsonString());
        // One row per item, in payload order: intro.md, lessons/one.md (changed in the copy), lessons/two.md.
        float[] rows = [1, 0, 0.5f, 0.5f, 0, 1];
        var bytes = new byte[rows.Length * 4];
        for (var i = 0; i < rows.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), rows[i]);
        }

        File.WriteAllBytes(Path.Combine(dir, "payload", "vectors.f32"), bytes);
        var copy = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "issued", "copy-valid"), Options(), ct);
        var bound = OpenMoca.Reader.Sidecars.SidecarBinder.Bind(dir, copy);
        Assert.True(bound.Usable);
        Assert.Equal(["S009_SIDECAR_ITEMS_REPLACED"], bound.Diagnostics.Select(d => d.Code));
        var byPath = bound.Chunks.ToDictionary(c => c.Path);
        Assert.Equal([1f, 0f], byPath["intro.md"].Vector!);
        Assert.Equal([0f, 1f], byPath["lessons/two.md"].Vector!);
        Assert.Null(byPath["lessons/one.md"].Vector);
        var rep = copy.Nodes.Single(n => n.Path == "lessons/one.md").Representations[0];
        Assert.Equal(rep.BodyOffset, byPath["lessons/one.md"].Start);
        Assert.Equal(rep.BodyOffset + System.Text.Encoding.UTF8.GetByteCount(rep.Body), byPath["lessons/one.md"].End);
    }

    /// <summary>Reader contract §3: the member limit counts every depth, so a large composite package needs room.</summary>
    [Fact]
    public void MaxMembers_DefaultsTo4096()
    {
        Assert.Equal(4096, ReaderLimits.Default.MaxMembers);
        Assert.Equal(8, ReaderLimits.Default.MaxMemberDepth);
    }

    /// <summary>Reader contract §9.4: a package state's bindings by composition survive JSON, so a host can keep them.</summary>
    [Fact]
    public async Task APackageState_KeepsItsCompositionThroughJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var composite = Path.Combine(Fixtures, "composite");
        var result = await MocaReader.ReadPackageAsync(Path.Combine(composite, "show"), new ReadOptions { ResolveMember = MemberResolvers.Directories([composite]) }, ct);
        var state = PackageState.From(result);
        Assert.NotNull(state.Composition);
        var back = JsonSerializer.Deserialize<PackageState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(JsonSerializer.Serialize(state.Composition), JsonSerializer.Serialize(back.Composition));
        Assert.Equal(4, back.Composition!.Members.Count); // the season, and the three episodes
    }
}
