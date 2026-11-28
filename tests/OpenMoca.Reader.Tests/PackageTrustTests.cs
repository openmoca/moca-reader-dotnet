using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// The trust call (<see cref="PackageTrust"/>): a package's trust state from its manifest, file digests and attestation
/// files gives what opening the package gives, for every package in the conformance corpus, with its dates.
/// </summary>
public sealed class PackageTrustTests
{
    private static readonly DateTimeOffset DefaultNow = DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Lazy<JsonObject> Corpus = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(MocaSpec.Conformance, "cases.json")))!.AsObject());

    private static string Fixture(string name) => Path.Combine(MocaSpec.Conformance, "fixtures", name);

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.Value["cases"]!.AsArray())
        {
            data.Add(c!["id"]!.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Evaluate_ForEveryCorpusPackage_GivesTheStateOpeningGives(string id)
    {
        var c = Corpus.Value["cases"]!.AsArray().OfType<JsonObject>().Single(x => JsonValues.StringOf(x["id"]) == id);
        var o = c["options"] as JsonObject ?? [];
        var now = Dates.Parse(JsonValues.StringOf(o["now"])) ?? DefaultNow;
        var options = new ReadOptions
        {
            TrustRoot = JsonValues.StringOf(o["trustRoot"]) is { } trust ? TrustRoot.LoadFile(Path.Combine(MocaSpec.Conformance, trust)) : null,
            RequireSignature = o["requireSignature"]?.GetValue<bool>() == true,
            Strict = o["strict"]?.GetValue<bool>() == true,
            SigstoreVerifier = new OpenMoca.Reader.Sigstore.SigstoreVerifier(),
            Clock = () => now,
        };

        var targets = new[] { JsonValues.StringOf(c["target"]) }
            .Concat((c["packages"] as JsonArray ?? []).Select(JsonValues.StringOf))
            .OfType<string>()
            .Distinct();
        foreach (var target in targets)
        {
            var opened = await MocaReader.ReadPackageAsync(Path.Combine(MocaSpec.Conformance, target), options, TestContext.Current.CancellationToken);
            // The trust call is for a package a store holds: one with a digest and a manifest. Others are refused on open.
            if (opened.Digest is null || !opened.FileBytes.TryGetValue(PackageDigest.ManifestFile, out var manifest) || opened.Envelope is not null)
            {
                continue;
            }

            var state = PackageTrust.Evaluate(manifest, opened.FileDigests, AttestationsOf(opened), options);
            AssertSame(opened, state, $"{id} {target}");
        }
    }

    [Fact]
    public async Task Evaluate_KeyRemovedBeforeItsExpiry_IsNoLongerSigned()
    {
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        var full = TrustRoot.LoadFile(Fixture("trust-root.json"));
        var trusted = PackageTrust.Evaluate(manifest, digests, attestations, full, DefaultNow);
        Assert.True(trusted.Signed);
        Assert.Equal(DateTimeOffset.Parse("2031-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), trusted.TrustedUntil);

        // The host removes the publisher's key in 2026, five years before it would expire.
        var document = (JsonObject)full.Document.DeepClone();
        var keys = (JsonArray)document["keys"]!;
        keys.Remove(keys.OfType<JsonObject>().Single(k => JsonValues.StringOf(k["keyid"]) == "example-publisher-2026"));
        var narrowed = TrustRoot.Load(document);
        var state = PackageTrust.Evaluate(manifest, digests, attestations, narrowed, DefaultNow);

        Assert.False(state.Signed);
        Assert.Contains(state.Diagnostics, d => d.Code == DiagnosticCodes.A002AttestationInvalid);
        var opened = await MocaReader.ReadPackageAsync(Fixture("signed-reviewed"), new ReadOptions { TrustRoot = narrowed, Clock = () => DefaultNow }, TestContext.Current.CancellationToken);
        AssertSame(opened, state, "key removed");

        // The stored package state follows: the record a store search returns is no longer signed.
        var stored = PackageState.From(await MocaReader.ReadPackageAsync(Fixture("signed-reviewed"), new ReadOptions { TrustRoot = full, Clock = () => DefaultNow }, TestContext.Current.CancellationToken));
        Assert.NotEmpty(stored.Signers);
        Assert.Equal(Json(PackageState.From(opened)), Json(stored.WithTrust(state)));
    }

    [Fact]
    public async Task Evaluate_FileChangedAfterSigning_EnvelopeDoesNotMatchTheDigests()
    {
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        var changed = new Dictionary<string, string>(digests, StringComparer.Ordinal)
        {
            ["content/faq.md"] = PackageDigest.Sha256Hex("changed after signing\n"u8),
        };
        var trust = TrustRoot.LoadFile(Fixture("trust-root.json"));
        var state = PackageTrust.Evaluate(manifest, changed, attestations, trust, DefaultNow);

        Assert.False(state.Signed);
        Assert.Contains(state.Diagnostics, d => d.Code == DiagnosticCodes.A008PackageDigestMismatch);

        // The same as opening a copy of the package with that file changed.
        var copy = CopyOf(Fixture("signed-reviewed"));
        try
        {
            File.WriteAllText(Path.Combine(copy, "content", "faq.md"), "changed after signing\n");
            var opened = await MocaReader.ReadPackageAsync(copy, new ReadOptions { TrustRoot = trust, Clock = () => DefaultNow }, TestContext.Current.CancellationToken);
            Assert.Equal(opened.Digest, state.Digest);
            AssertSame(opened, state, "file changed");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public async Task Evaluate_EmptyTrustRoot_TrustsNoOne()
    {
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        var empty = TrustRoot.Load(new JsonObject { ["keys"] = new JsonArray() });
        var state = PackageTrust.Evaluate(manifest, digests, attestations, empty, DefaultNow);

        Assert.False(state.Signed);
        Assert.Empty(state.Keys);
        Assert.Null(state.TrustedUntil);
        Assert.All(state.Attestations, a => Assert.NotEqual("valid", a.Outcome));
        var opened = await MocaReader.ReadPackageAsync(Fixture("signed-reviewed"), new ReadOptions { TrustRoot = empty, Clock = () => DefaultNow }, TestContext.Current.CancellationToken);
        AssertSame(opened, state, "empty trust root");

        // No trust root at all: every attestation is unverifiable (contract §6).
        var none = PackageTrust.Evaluate(manifest, digests, attestations, null, DefaultNow);
        Assert.All(none.Attestations, a => Assert.Equal("unverifiable", a.Outcome));
        Assert.Contains(none.Diagnostics, d => d.Code == DiagnosticCodes.A003AttestationUnverifiable);
    }

    [Fact]
    public async Task Evaluate_Dates_AreTheEarliestKeyExpiryAndThePackagesOwnWindow()
    {
        var trust = TrustRoot.LoadFile(Fixture("trust-root.json"));
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        var expiry = DateTimeOffset.Parse("2031-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var state = PackageTrust.Evaluate(manifest, digests, attestations, trust, DefaultNow);

        Assert.Null(state.TrustedFrom);
        Assert.Equal(expiry, state.TrustedUntil);
        Assert.Equal(2, state.Keys.Count);
        Assert.All(state.Keys, k => Assert.Equal(expiry, k.Expires));
        Assert.True(state.Holds(DefaultNow));
        Assert.False(state.Holds(expiry));

        // At the expiry the key is no longer trusted, and opening says the same.
        var later = PackageTrust.Evaluate(manifest, digests, attestations, trust, expiry);
        Assert.False(later.Signed);

        // A key expiring sooner than the package's own window ends it first, and the window's start is the package's.
        var document = (JsonObject)trust.Document.DeepClone();
        var soon = DateTimeOffset.Parse("2027-03-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        ((JsonArray)document["keys"]!).OfType<JsonObject>().Single(k => JsonValues.StringOf(k["keyid"]) == "example-reviewer-2026")["expires"] = "2027-03-01T00:00:00Z";
        var withWindow = WithManifest(manifest, digests, m =>
        {
            m["validFrom"] = "2026-01-01T00:00:00Z";
            m["validUntil"] = "2028-01-01T00:00:00Z";
        });
        var windowed = PackageTrust.Evaluate(withWindow.Manifest, withWindow.Digests, attestations, TrustRoot.Load(document), DefaultNow);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), windowed.TrustedFrom);
        Assert.Equal(soon, windowed.TrustedUntil);

        // Without the early key, the package's own validUntil comes first.
        var untilOnly = PackageTrust.Evaluate(withWindow.Manifest, withWindow.Digests, attestations, trust, DefaultNow);
        Assert.Equal(DateTimeOffset.Parse("2028-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), untilOnly.TrustedUntil);
    }

    [Fact]
    public async Task Evaluate_InputsThatAreNotOnePackage_AreTheHostsError()
    {
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        var other = Encoding.UTF8.GetBytes("{\"id\":\"https://example.com/other\",\"version\":\"1.0.0\"}");
        Assert.Throws<ArgumentException>(() => PackageTrust.Evaluate(other, digests, attestations, null, DefaultNow));

        var misplaced = new Dictionary<string, byte[]>(attestations, StringComparer.Ordinal) { ["content/extra.dsse.json"] = [] };
        Assert.Throws<ArgumentException>(() => PackageTrust.Evaluate(manifest, digests, misplaced, null, DefaultNow));

        var upper = digests.ToDictionary(kv => kv.Key, kv => kv.Value.ToUpperInvariant(), StringComparer.Ordinal);
        Assert.Throws<ArgumentException>(() => PackageTrust.Evaluate(manifest, upper, attestations, null, DefaultNow));
    }

    [Fact]
    public async Task Evaluate_WhenTraced_RecordsCountsAndNoContent()
    {
        var (manifest, digests, attestations) = await Stored("signed-reviewed");
        // The listener hears the Reader's spans from every test that runs at the same time, on other threads: collect
        // them in a thread-safe queue, and keep only this call's, the spans in this test's own trace.
        var spans = new ConcurrentQueue<Activity>();
        using var testSource = new ActivitySource("OpenMoca.Reader.Tests.PackageTrust");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ReaderTelemetry.SourceName || s.Name == testSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId trace;
        using (var parent = testSource.StartActivity("test"))
        {
            trace = parent!.TraceId;
            PackageTrust.Evaluate(manifest, digests, attestations, TrustRoot.LoadFile(Fixture("trust-root.json")), DefaultNow);
        }

        var span = Assert.Single(spans.ToArray(), s => s.TraceId == trace && s.OperationName == ReaderTelemetry.PackageTrustSpan);
        Assert.Equal(2, span.GetTagItem("moca.attestations.count"));
        Assert.Equal(1, span.GetTagItem("moca.signers.count"));
        Assert.Equal(true, span.GetTagItem("moca.trust.signed"));
        Assert.All(span.TagObjects, t => Assert.StartsWith("moca.", t.Key, StringComparison.Ordinal));
        Assert.True(span.Duration > TimeSpan.Zero);
    }

    internal static Dictionary<string, byte[]> AttestationsOf(PackageResult result) =>
        result.FileBytes.Where(kv => PackageDigest.IsAttestationPath(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static async Task<(byte[] Manifest, IReadOnlyDictionary<string, string> Digests, Dictionary<string, byte[]> Attestations)> Stored(string fixture)
    {
        var opened = await MocaReader.ReadPackageAsync(Fixture(fixture), new ReadOptions(), TestContext.Current.CancellationToken);
        return (opened.FileBytes[PackageDigest.ManifestFile], opened.FileDigests, AttestationsOf(opened));
    }

    private static (byte[] Manifest, Dictionary<string, string> Digests) WithManifest(byte[] manifest, IReadOnlyDictionary<string, string> digests, Action<JsonObject> change)
    {
        var m = JsonNode.Parse(manifest)!.AsObject();
        change(m);
        var bytes = Encoding.UTF8.GetBytes(m.ToJsonString());
        var changed = new Dictionary<string, string>(digests, StringComparer.Ordinal) { [PackageDigest.ManifestFile] = PackageDigest.Sha256Hex(bytes) };
        return (bytes, changed);
    }

    private static string CopyOf(string directory)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"moca-trust-{Guid.NewGuid():N}");
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return copy;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, CitationJson.Options);

    // Contract §6 and attestations §7: everything opening concludes about trust, compared field by field.
    private static void AssertSame(PackageResult opened, TrustState state, string what)
    {
        Assert.True(opened.Digest == state.Digest, $"{what}: digest");
        Assert.True(opened.Id == state.Id && opened.Version == state.Version, $"{what}: id and version");
        Assert.True(opened.Signers.SequenceEqual(state.Signers), $"{what}: signers [{string.Join(", ", opened.Signers)}] against [{string.Join(", ", state.Signers)}]");
        Assert.True(opened.RelationScopes.SequenceEqual(state.RelationScopes), $"{what}: relation scopes");
        Assert.True(opened.Capabilities.Contains("signed") == state.Signed || !opened.Valid, $"{what}: signed");
        Assert.True(Json(opened.IssuedCopy) == Json(state.IssuedCopy), $"{what}: issued copy {Json(opened.IssuedCopy)} against {Json(state.IssuedCopy)}");
        Assert.True(Json(opened.ReviewsByFile) == Json(state.Reviews), $"{what}: reviews");
        var records = opened.Attestations.Select(a => (a.File, a.Outcome, a.Signer, a.Reason, string.Join(",", a.Current), string.Join(",", a.Outdated), a.KeyId)).ToList();
        var stateRecords = state.Attestations.Select(a => (a.File, a.Outcome, a.Signer, a.Reason, string.Join(",", a.Current), string.Join(",", a.Outdated), a.KeyId)).ToList();
        Assert.True(records.SequenceEqual(stateRecords), $"{what}: attestation records\n  {string.Join("\n  ", records)}\nagainst\n  {string.Join("\n  ", stateRecords)}");
        var trustCodes = opened.Diagnostics.Where(d => d.Code.StartsWith("A0", StringComparison.Ordinal) || d.Code == DiagnosticCodes.R004RelationUnauthorised)
            .Select(d => (d.Code, d.Severity, d.File, d.Message)).ToList();
        var stateCodes = state.Diagnostics.Select(d => (d.Code, d.Severity, d.File, d.Message)).ToList();
        Assert.True(trustCodes.SequenceEqual(stateCodes), $"{what}: diagnostics\n  {string.Join("\n  ", trustCodes)}\nagainst\n  {string.Join("\n  ", stateCodes)}");
        if (opened.Terms is { } terms)
        {
            Assert.True(terms.Authority == state.TermsAuthority, $"{what}: terms authority");
        }

        if (opened.Valid)
        {
            var stored = PackageState.From(opened);
            Assert.True(Json(stored) == Json(stored.WithTrust(state)), $"{what}: package state");
        }
    }
}
