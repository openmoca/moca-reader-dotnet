using System.Text.Json.Nodes;
using OpenMoca.Reader.Json;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Schema errors name the deepest failing places, once each, and one branch of a failing oneOf or anyOf.</summary>
public sealed class SchemaErrorTests
{
    [Fact]
    public void Errors_AreTheDeepestFailingPlaces_WithOneBranchOfEachFailingCombinator()
    {
        // verified[1].at passes its anyOf (a date), so its failing date-time branch is not reported; verified[0].at
        // fails both branches, so only the first is; the levels above each failure are not reported.
        var node = JsonNode.Parse("""{"type":"Note","verified":[{"by":"a","at":"nope"},{"by":"b","at":"2026-01-01"},{"by":"c","at":5}],"tags":[1]}""");
        Assert.Equal(
            [
                "/tags/0 Value is \"integer\" but should be \"string\"",
                "/verified/0/at Value does not match format \"date-time\"",
                "/verified/2/at Value is \"integer\" but should be \"string\"",
            ],
            MocaSchemas.Validate(MocaSchemas.Node, node));
    }

    [Fact]
    public void A_MissingRequiredProperty_IsReportedBesideDeeperFailures()
    {
        var manifest = JsonNode.Parse("""{"mocaVersion":"0.7","version":"1.0.0","title":"T","tags":[3]}""");
        Assert.Equal(
            ["/tags/0 Value is \"integer\" but should be \"string\"", "(root) Required properties [\"id\"] are not present"],
            MocaSchemas.Validate(MocaSchemas.Manifest, manifest));
    }
}
