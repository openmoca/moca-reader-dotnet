using System.Text.Json.Nodes;
using OpenMoca.Reader.ConformanceAdapter;
using OpenMoca.Reader.Json;
using Xunit;

namespace OpenMoca.Reader.Tests.Conformance;

/// <summary>
/// Runs every case in moca-spec's <c>conformance/cases.json</c> and checks it the way
/// <c>scripts/run-conformance.mjs</c> does: sets of codes and capabilities, digests, evidence matches,
/// search inclusions and exclusions, and exact structure answers. Message text is never compared.
/// </summary>
public sealed class ConformanceCorpusTests
{
    private static readonly Lazy<JsonObject> Corpus = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(MocaSpec.Conformance, "cases.json")))!.AsObject());

    private static readonly Lazy<Dictionary<string, string>> ExpectedFailures = new(() =>
    {
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Conformance", "expected-failures.json")))!;
        return file["cases"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
    });

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.Value["cases"]!.AsArray())
        {
            data.Add(c!["id"]!.GetValue<string>());
        }

        return data;
    }

    [Fact]
    public void Corpus_WhenLoaded_IsVersion06()
    {
        Assert.Matches(@"^0\.6\.", Corpus.Value["corpusVersion"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Case_WhenRun_MatchesExpectedConclusions(string id)
    {
        var c = FindCase(id);
        var actual = await ConformanceCases.ActualForAsync(MocaSpec.Conformance, c, TestContext.Current.CancellationToken);
        var expect = c["expect"]!.AsObject();
        var problems = JsonValues.StringOf(c["kind"]) switch
        {
            "search" => CheckSearch(expect, actual),
            "structure" => CheckStructure(expect, actual),
            _ => CheckPackage(expect, actual),
        };

        if (ExpectedFailures.Value.TryGetValue(id, out var reason))
        {
            Assert.True(problems.Count > 0, $"{id} now passes: remove it from expected-failures.json ({reason})");
            return;
        }

        Assert.True(problems.Count == 0, $"{id}:\n  {string.Join("\n  ", problems)}");
    }

    private static JsonObject FindCase(string id) =>
        Corpus.Value["cases"]!.AsArray().OfType<JsonObject>().Single(c => JsonValues.StringOf(c["id"]) == id);

    private static List<string> CheckPackage(JsonObject e, JsonObject actual)
    {
        var problems = new List<string>();
        if (e["valid"]!.GetValue<bool>() != actual["valid"]!.GetValue<bool>())
        {
            problems.Add($"valid: expected {e["valid"]}, got {actual["valid"]}");
        }

        CompareSets("codes", e["codes"], actual["codes"], problems);
        CompareSets("capabilities", e["capabilities"], actual["capabilities"], problems);
        if (JsonValues.StringOf(e["digest"]) is { } digest)
        {
            var want = digest.StartsWith("SAME:", StringComparison.Ordinal)
                ? JsonValues.StringOf(FindCase(digest[5..])["expect"]!["digest"])
                : digest;
            if (want != JsonValues.StringOf(actual["digest"]))
            {
                problems.Add($"digest: expected {want}, got {actual["digest"]}");
            }
        }

        if (e["sidecarUsable"] is JsonValue usable && usable.GetValue<bool>() != (actual["sidecarUsable"]?.GetValue<bool>()))
        {
            problems.Add($"sidecarUsable: expected {usable}, got {actual["sidecarUsable"]}");
        }

        foreach (var (path, want) in e["evidenceMatched"] as JsonObject ?? [])
        {
            var got = actual["evidenceMatched"]?[path];
            if (!JsonNode.DeepEquals(want, got))
            {
                problems.Add($"evidenceMatched {path}: expected {want?.ToJsonString()}, got {got?.ToJsonString()}");
            }
        }

        return problems;
    }

    private static List<string> CheckSearch(JsonObject e, JsonObject actual)
    {
        var problems = new List<string>();
        var records = actual["records"]!.AsArray().OfType<JsonObject>().ToList();
        var keys = records.Select(r => JsonValues.StringOf(r["node"]!["ref"])).ToHashSet();
        CompareSets("codes", e["codes"] ?? new JsonArray(), actual["codes"], problems);
        foreach (var k in (e["include"] as JsonArray ?? []).Select(JsonValues.StringOf))
        {
            if (!keys.Contains(k))
            {
                problems.Add($"missing {k}");
            }
        }

        foreach (var k in (e["exclude"] as JsonArray ?? []).Select(JsonValues.StringOf))
        {
            if (keys.Contains(k))
            {
                problems.Add($"returned {k}, which must never be returned");
            }
        }

        if (JsonValues.IntegerOf(e["count"]) is { } count && records.Count != count)
        {
            problems.Add($"expected {count} result(s), got {records.Count}");
        }

        foreach (var (k, locale) in e["locales"] as JsonObject ?? [])
        {
            var got = records.Where(r => JsonValues.StringOf(r["node"]!["ref"]) == k).Select(r => JsonValues.StringOf(r["node"]!["locale"])).ToList();
            if (got.Count == 0 || got.Any(l => l != JsonValues.StringOf(locale)))
            {
                problems.Add($"{k}: expected locale {locale}, got [{string.Join(",", got)}]");
            }
        }

        foreach (var r in records)
        {
            var errors = MocaSchemas.Validate(MocaSchemas.CitationRecord, r);
            if (errors.Count > 0)
            {
                problems.Add($"{r["node"]!["ref"]} is not a valid citation record: {errors[0]}");
            }

            if (r["score"] is not JsonValue)
            {
                problems.Add($"{r["node"]!["ref"]} has no score");
            }
        }

        return problems;
    }

    private static List<string> CheckStructure(JsonObject e, JsonObject actual)
    {
        var problems = new List<string>();
        CompareSets("codes", e["codes"] ?? new JsonArray(), actual["codes"], problems);
        // Structure answers are facts: compared exactly, key order and list order included.
        var want = e["result"]?.ToJsonString(JsonValues.CompactJson) ?? "null";
        var got = actual["result"]?.ToJsonString(JsonValues.CompactJson) ?? "null";
        if (want != got)
        {
            problems.Add($"result: expected {want}, got {got}");
        }

        return problems;
    }

    private static void CompareSets(string name, JsonNode? expected, JsonNode? actual, List<string> problems)
    {
        static SortedSet<string> Set(JsonNode? n) =>
            new((n as JsonArray ?? []).Select(JsonValues.StringOf).OfType<string>(), StringComparer.Ordinal);

        var want = Set(expected);
        var got = Set(actual);
        if (!want.SetEquals(got))
        {
            problems.Add($"{name}: expected [{string.Join(",", want)}], got [{string.Join(",", got)}]");
        }
    }
}
