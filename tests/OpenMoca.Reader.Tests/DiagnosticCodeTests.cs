using System.Text.RegularExpressions;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>The Reader's codes and severities are exactly the Reader contract §11 table.</summary>
public sealed partial class DiagnosticCodeTests
{
    [GeneratedRegex(@"^\| `([A-Z]\d{3}_[A-Z0-9_]+)` \| (error|warning|info) \|", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    [Fact]
    public void Codes_MatchTheContractTable()
    {
        var contract = File.ReadAllText(Path.Combine(MocaSpec.Root, "spec", "moca-reader-contract.md"));
        var table = TableRow().Matches(contract).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        var ours = DiagnosticCodes.All.ToDictionary(c => c, c => DiagnosticCodes.DefaultSeverity(c).ToString().ToLowerInvariant(), StringComparer.Ordinal);

        Assert.Equal(table.OrderBy(kv => kv.Key, StringComparer.Ordinal), ours.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void RegisteredUrnNamespaces_EqualTheMocaSpecSnapshot()
    {
        using var snapshot = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(MocaSpec.Root, "data", "urn-namespaces.json")));
        var formal = snapshot.RootElement.GetProperty("formal").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(formal, OpenMoca.Reader.Content.ManifestChecks.RegisteredUrnNamespaces.Order(StringComparer.Ordinal));
    }
}
