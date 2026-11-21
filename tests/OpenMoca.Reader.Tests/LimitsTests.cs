using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Contract §3 and attestations §7: attestation files, signature entries and collection members are bounded.</summary>
public sealed class LimitsTests
{
    private static readonly string Fixtures = Path.Combine(MocaSpec.Conformance, "fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static ReadOptions Signed(ReaderLimits limits) => new()
    {
        TrustRoot = TrustRoot.LoadFile(Path.Combine(Fixtures, "trust-root.json")),
        Clock = () => Now,
        Limits = limits,
    };

    [Fact]
    public async Task ReadPackage_WhenAttestationFilesAreOverTheLimit_ReadsNoneAndReportsA001Once()
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "signed-reviewed"), Signed(ReaderLimits.Default with { MaxAttestations = 1 }), TestContext.Current.CancellationToken);

        Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.A001AttestationMalformed);
        Assert.Empty(result.Attestations);
        Assert.Empty(result.Signers);
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task ReadPackage_WhenAnAttestationFileIsOverTheByteLimit_DoesNotParseIt()
    {
        var result = await MocaReader.ReadPackageAsync(Path.Combine(Fixtures, "signed-reviewed"), Signed(ReaderLimits.Default with { MaxAttestationBytes = 64 }), TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.A001AttestationMalformed);
        Assert.All(result.Attestations, a => Assert.Equal("malformed", a.Outcome));
        Assert.Empty(result.Signers);
    }

    [Fact]
    public void DefaultLimits_AreTheContractValues()
    {
        Assert.Equal(256, ReaderLimits.Default.MaxAttestations);
        Assert.Equal(1024 * 1024, ReaderLimits.Default.MaxAttestationBytes);
        Assert.Equal(16, Dsse.MaxSignatures);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Parse_WithManySignatureEntries_AcceptsUpToSixteen(int count, bool parses)
    {
        var statement = """{"_type":"https://in-toto.io/Statement/v1","subject":[],"predicateType":"https://w3id.org/moca/attestation/package/v1"}""";
        var signatures = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            signatures.Add(new JsonObject { ["keyid"] = $"k{i}", ["sig"] = "AA==" });
        }

        var envelope = new JsonObject
        {
            ["payloadType"] = AttestationFormats.PayloadType,
            ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement)),
            ["signatures"] = signatures,
        };

        var (parsed, reason) = AttestationFormats.Parse("attestations/a.dsse.json", Encoding.UTF8.GetBytes(envelope.ToJsonString()));

        Assert.Equal(parses, parsed is not null);
        Assert.Equal(parses, reason is null);
        Assert.Equal(parses, Dsse.Verify(envelope, _ => null).Reason != $"more than {Dsse.MaxSignatures} signature entries");
    }

    [Fact]
    public async Task ReadPackage_WhenSharedMemberListsAddUpPastTheTripleLimit_ReportsO001()
    {
        // Six collections name one list of six: 36 members from about 30 triples.
        var target = Path.Combine(Fixtures, "structure-shared-list");
        var within = await MocaReader.ReadPackageAsync(target, cancellationToken: TestContext.Current.CancellationToken);
        var over = await MocaReader.ReadPackageAsync(target, new ReadOptions { Limits = ReaderLimits.Default with { MaxTriples = 30 } }, TestContext.Current.CancellationToken);

        Assert.Contains("structured", within.Capabilities);
        Assert.DoesNotContain(within.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable);
        Assert.Contains(over.Diagnostics, d => d.Code == DiagnosticCodes.O001OntologyUnparseable);
        Assert.DoesNotContain("structured", over.Capabilities);
    }
}
