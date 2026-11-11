using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sigstore;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Offline Sigstore verification (attestations §6, §7), against the corpus's self-made test
/// Sigstore. Each test breaks one thing and checks that the matching step rejects it.
/// </summary>
public sealed class SigstoreTests
{
    private static readonly string Fixtures = Path.Combine(MocaSpec.Conformance, "fixtures", "sigstore");

    private static JsonObject Bundle() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "sigstore-signed", "attestations", "package.sigstore.json")))!.AsObject();

    private static JsonObject Root() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "trust-root.json")))!["sigstore"]!["trustedRoot"]!.AsObject();

    private static JsonObject Entry(JsonObject bundle) => bundle["verificationMaterial"]!["tlogEntries"]![0]!.AsObject();

    private static string Flip(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        bytes[^1] ^= 0x01;
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void ValidBundle_ReturnsTheCertificateIdentity()
    {
        var result = new SigstoreVerifier().Verify(Bundle(), Root());

        Assert.Equal("valid", result.Outcome);
        Assert.Equal("https://issuer.sigstore-test.openmoca.example", result.Issuer);
        Assert.Equal("https://ci.sigstore-test.openmoca.example/publish@refs/heads/main", result.SubjectAlternativeName);
    }

    [Fact]
    public void BrokenSignedEntryTimestamp_IsInvalid()
    {
        var bundle = Bundle();
        var promise = Entry(bundle)["inclusionPromise"]!.AsObject();
        promise["signedEntryTimestamp"] = Flip(promise["signedEntryTimestamp"]!.GetValue<string>());

        Assert.Contains("inclusion promise", new SigstoreVerifier().Verify(bundle, Root()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenCheckpointSignature_IsInvalid()
    {
        var bundle = Bundle();
        var checkpoint = Entry(bundle)["inclusionProof"]!["checkpoint"]!.AsObject();
        var envelope = checkpoint["envelope"]!.GetValue<string>();
        checkpoint["envelope"] = envelope.Replace("\n1\n", "\n2\n", StringComparison.Ordinal);

        Assert.Contains("checkpoint", new SigstoreVerifier().Verify(bundle, Root()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTimestamp_IsInvalid()
    {
        var bundle = Bundle();
        Entry(bundle).Remove("inclusionPromise");

        Assert.Contains("no verified timestamp", new SigstoreVerifier().Verify(bundle, Root()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedPayload_IsInvalid()
    {
        var bundle = Bundle();
        var envelope = bundle["dsseEnvelope"]!.AsObject();
        envelope["payload"] = Convert.ToBase64String("{\"_type\":\"x\"}"u8.ToArray());

        Assert.Contains("payload hash mismatch", new SigstoreVerifier().Verify(bundle, Root()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenSct_IsInvalid()
    {
        var root = Root();
        var ctlog = root["ctlogs"]![0]!.AsObject();
        // A CT log key the SCT was not signed with: take the transparency log's key under the CT log's id.
        ctlog["publicKey"]!["rawBytes"] = root["tlogs"]![0]!["publicKey"]!["rawBytes"]!.GetValue<string>();

        Assert.Contains("signed certificate timestamp", new SigstoreVerifier().Verify(Bundle(), root).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggedTimeOutsideTheCertificate_IsInvalid()
    {
        var root = Root();
        root["certificateAuthorities"]![0]!["validFor"] = new JsonObject { ["start"] = "2026-10-01T00:00:00Z" };

        Assert.Contains("certificate chain", new SigstoreVerifier().Verify(Bundle(), root).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusableTrustedRoot_IsUnverifiable()
    {
        var result = new SigstoreVerifier().Verify(Bundle(), new JsonObject { ["tlogs"] = "x" });

        Assert.Equal("unverifiable", result.Outcome);
    }

    [Fact]
    public async Task WithoutAVerifier_SigstoreIsUnverifiable_AndWithOneItVerifies()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = Path.Combine(Fixtures, "sigstore-signed");
        var trust = TrustRoot.LoadFile(Path.Combine(Fixtures, "trust-root.json"));

        var without = await MocaReader.ReadPackageAsync(target, new ReadOptions { TrustRoot = trust }, ct);
        var with = await MocaReader.ReadPackageAsync(target, new ReadOptions { TrustRoot = trust, SigstoreVerifier = new SigstoreVerifier() }, ct);

        Assert.Equal([DiagnosticCodes.A003AttestationUnverifiable], without.Diagnostics.Select(d => d.Code));
        Assert.Contains("OpenMoca.Reader.Sigstore", without.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Empty(with.Diagnostics);
        Assert.Contains("signed", with.Capabilities);
    }
}
