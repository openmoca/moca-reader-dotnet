using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// <c>ai.allowed.mention</c>, and when a package is closed or hidden for a request (Reader contract §15.3–15.4,
/// moca-spec ADR-0041).
/// </summary>
public sealed class TermsMentionTests
{
    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    private static readonly TermsRequest Provider = new() { Ai = new AiRequest(TermsDestinations.Provider) };

    private static HashSet<string> Problems(string terms) =>
        [.. TermsRules.Check(Json(terms)).Select(p => $"{p.Problem.Code} {p.Problem.Path}")];

    [Fact]
    public void Check_MentionAtPackageLevel_IsValid_AndInANodesEntryIsUnknown()
    {
        Assert.Empty(Problems("""{ "ai": { "allowed": { "mention": false } } }"""));
        Assert.Equal(["U001_TERMS_INVALID ai.allowed.mention"], Problems("""{ "ai": { "allowed": { "mention": "no" } } }"""));
        Assert.Equal(["U003_TERMS_UNKNOWN_VALUE nodes.0.ai.allowed.mention"],
            Problems("""{ "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": { "mention": false } } } ] }"""));
    }

    [Fact]
    public void ResolveAi_Mention_DefaultsToTrue_AndTheStricterValueAppliesFromAnyLevel()
    {
        Assert.Equal(new ResolvedTerm<bool>(true, "platform"), TermsRules.ResolveAi(Json("{}"), "a.md", null, []).Allowed.Mention);
        Assert.Equal(new ResolvedTerm<bool>(false, "package"),
            TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "mention": false } } }"""), "a.md", null, []).Allowed.Mention);
        Assert.Equal(new ResolvedTerm<bool>(false, "composite"),
            TermsRules.ResolveAi(Json("{}"), "a.md", null, [Json("""{ "ai": { "allowed": { "mention": false } } }""")]).Allowed.Mention);
        Assert.Equal(new ResolvedTerm<bool>(false, "host"),
            TermsRules.ResolveAi(Json("{}"), "a.md", new TermsLimits { Mention = false }.ToAi(), []).Allowed.Mention);
        // A host limit of true never loosens the package's false.
        Assert.Equal(new ResolvedTerm<bool>(false, "package"),
            TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "mention": false } } }"""), "a.md", new TermsLimits { Mention = true }.ToAi(), []).Allowed.Mention);
    }

    [Theory]
    [InlineData("""{ "ai": { "allowed": { "mention": "no" } } }""")]
    [InlineData("""{ "ai": { "allowed": false } }""")]
    [InlineData("""{ "ai": "none" }""")]
    public void ResolveAi_AMalformedMentionOrContainer_FailsClosed(string terms)
    {
        Assert.False(TermsRules.ResolveAi(Json(terms), null, null, []).Allowed.Mention.Value);
    }

    [Theory]
    [InlineData("""{ "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": { "mention": false } } } ] }""")]
    [InlineData("""{ "nodes": {} }""")]
    [InlineData("""{ "nodes": [ { "paths": ["content/a.md"], "ai": false } ] }""")]
    public void ResolveAi_MentionHasNoNodeLevel_EvenUnderAMalformedEntry(string terms)
    {
        Assert.Equal(new ResolvedTerm<bool>(true, "platform"), TermsRules.ResolveAi(Json(terms), "a.md", null, []).Allowed.Mention);
    }

    [Fact]
    public void Loosened_NeverReportsMention()
    {
        Assert.Empty(TermsRules.Loosened(Json("""
            { "ai": { "allowed": { "mention": false } }, "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": { "mention": true } } } ] }
            """)));
    }

    [Fact]
    public void ClosureOf_ClosedByTheDestination_HiddenOnlyWhenMentionIsFalse()
    {
        var named = TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "provider": false } } }"""), null, null, []);
        var hidden = TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "provider": false, "mention": false } } }"""), null, null, []);

        var closure = TermsRules.ClosureOf(Provider, named);
        Assert.True(closure.Closed);
        Assert.False(closure.Hidden);
        Assert.Equal(["ai.allowed.provider"], closure.ClosedBy);
        Assert.True(TermsRules.ClosureOf(Provider, hidden).Hidden);
        // Mention changes nothing while the destination is allowed.
        Assert.Same(PackageClosure.Open, TermsRules.ClosureOf(new TermsRequest { Ai = new AiRequest(TermsDestinations.Device) }, hidden));
        // A request with no ai part closes and hides nothing.
        Assert.Same(PackageClosure.Open, TermsRules.ClosureOf(new TermsRequest(), hidden));
        Assert.Same(PackageClosure.Open, TermsRules.ClosureOf(null, hidden));
    }

    [Fact]
    public void ClosureOf_ProviderConditionsNotMet_CloseIt_AndAUseNeverDoes()
    {
        var ai = TermsRules.ResolveAi(Json("""{ "ai": { "allowed": { "training": false, "machineTranslation": false }, "providerRequires": ["noRetention"] } }"""), null, null, []);

        Assert.Equal(["ai.providerRequires"], TermsRules.ClosureOf(Provider, ai).ClosedBy);
        Assert.False(TermsRules.ClosureOf(new TermsRequest { Ai = new AiRequest(TermsDestinations.Provider, ["noRetention"]) }, ai).Closed);
        Assert.False(TermsRules.ClosureOf(new TermsRequest { Ai = new AiRequest(TermsDestinations.Host, Use: "translate") }, ai).Closed);
    }

    [Fact]
    public void ClosureOf_NodeTermsNeverCloseThePackage()
    {
        var terms = Json("""{ "nodes": [ { "paths": ["content/a.md"], "ai": { "allowed": { "provider": false } } } ] }""");

        Assert.False(TermsRules.ClosureOf(Provider, TermsRules.ResolveAi(terms, null, null, [])).Closed);
        Assert.Equal(["ai.allowed.provider"], TermsRules.GateReasons(Provider, TermsRules.ResolveAi(terms, "a.md", null, []), null));
    }

    [Fact]
    public void Diagnostic_U004Counts_AreOmittedOnOtherFindings()
    {
        var plain = System.Text.Json.JsonSerializer.Serialize(new Diagnostic("X", DiagnosticSeverity.Info, "m"));

        Assert.DoesNotContain("excluded", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", plain, StringComparison.Ordinal);
    }
}
