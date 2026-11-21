using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Attestations §6 (moca-spec ADR-0040): a trust-root entry with the <c>package</c> role and <c>ids</c> signs only
/// packages whose id its <c>ids</c> cover. And only a <c>valid</c> outcome from a verifier counts.
/// </summary>
public sealed class SignerBindingTests
{
    private static readonly string Fixtures = Path.Combine(MocaSpec.Conformance, "fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    // signed-reviewed's id is https://example.com/conformance/signed; its publisher key is example-publisher-2026.
    private static ReadOptions WithPublisherIds(JsonArray? ids, bool requireSignature = false)
    {
        var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "trust-root.json")))!;
        var publisher = doc["keys"]!.AsArray().OfType<JsonObject>().Single(k => (string?)k["keyid"] == "example-publisher-2026");
        publisher.Remove("ids");
        if (ids is not null)
        {
            publisher["ids"] = ids;
        }

        return new ReadOptions { TrustRoot = TrustRoot.Load(doc), Clock = () => Now, RequireSignature = requireSignature };
    }

    [Theory]
    [InlineData("https://example.com/conformance/signed")]
    [InlineData("https://example.com/conformance/")]
    public async Task ReadPackage_WhenSignerIdsCoverTheId_IsSigned(string scope)
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "signed-reviewed"), WithPublisherIds([scope]), TestContext.Current.CancellationToken);

        Assert.Contains("signed", result.Capabilities);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.A010SignerNotForPackage);
    }

    [Fact]
    public async Task ReadPackage_WhenSignerHasNoIds_SignsAnyId()
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "signed-reviewed"), WithPublisherIds(null), TestContext.Current.CancellationToken);

        Assert.Contains("signed", result.Capabilities);
    }

    [Theory]
    [InlineData("https://example.com/other/")]
    [InlineData("https://example.com/conformance/sign")] // not a prefix: it does not end in /, : or #
    public async Task ReadPackage_WhenSignerIdsDoNotCoverTheId_ReportsA010AndIsNotSigned(string scope)
    {
        var options = WithPublisherIds([scope], requireSignature: true);

        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "signed-reviewed"), options, TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.A010SignerNotForPackage);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.A009SignatureRequired);
        Assert.Empty(result.Signers);
        Assert.Contains(result.Attestations, a => a.Role == "package" && a.Outcome == "invalid");
    }

    [Fact]
    public async Task ReadPackage_WhenVerifierReturnsAnOutcomeOtherThanValid_DoesNotCountTheSignature()
    {
        var target = Path.Combine(Fixtures, "sigstore", "sigstore-signed");
        var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "sigstore", "trust-root.json")))!;
        var identity = doc["sigstore"]!["identities"]!.AsArray().OfType<JsonObject>().First();
        var verifier = new FixedVerifier(new SigstoreVerification("Valid", null, (string?)identity["issuer"], (string?)identity["subject"]));

        var result = await MocaReader.ReadPackageAsync(target, new ReadOptions { TrustRoot = TrustRoot.Load(doc), Clock = () => Now, SigstoreVerifier = verifier }, TestContext.Current.CancellationToken);

        Assert.Empty(result.Signers);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.A002AttestationInvalid);
    }

    private sealed class FixedVerifier(SigstoreVerification outcome) : ISigstoreVerifier
    {
        public SigstoreVerification Verify(JsonObject bundle, JsonObject trustedRoot) => outcome;
    }
}
