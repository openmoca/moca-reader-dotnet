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
    public void Corpus_WhenLoaded_IsVersion07()
    {
        Assert.Matches(@"^0\.7\.", Corpus.Value["corpusVersion"]!.GetValue<string>());
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
            "catalogue" => CheckCatalogue(expect, actual),
            "structure" or "map-time" => CheckStructure(expect, actual),
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

        // Package spec §4.6–4.13, Reader interface §4, attestations §7.1: result fields compared as JSON values; null
        // means absent.
        foreach (var field in new[] { "images", "translations", "tags", "apps", "readingOrder", "issuedCopy", "datePublished", "datePackaged", "publisher", "url", "contributors", "localeCoverage" })
        {
            if (e.TryGetPropertyValue(field, out var want) && Canonical(want) != Canonical(actual[field]))
            {
                problems.Add($"{field}: expected {Canonical(want)}, got {Canonical(actual[field])}");
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

        // Contract §7: where a refined quote is spoken; numbers compared as JSON text, so 10 and 10.0 agree.
        foreach (var (path, want) in e["evidenceLocated"] as JsonObject ?? [])
        {
            var got = actual["evidenceLocated"]?[path];
            var gotText = got is null ? "null" : JsonNode.Parse(got.ToJsonString())!.ToJsonString();
            if ((want?.ToJsonString() ?? "null") != gotText)
            {
                problems.Add($"evidenceLocated {path}: expected {want?.ToJsonString()}, got {gotText}");
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

        // Contract §9, localeMode first: these results come in this order (others may come between them).
        if (e["order"] is JsonArray order)
        {
            var at = order.Select(k => records.FindIndex(r => JsonValues.StringOf(r["node"]!["ref"]) == JsonValues.StringOf(k))).ToList();
            if (at.Where((i, n) => i < 0 || (n > 0 && i < at[n - 1])).Any())
            {
                problems.Add($"order: expected {string.Join(", ", order.Select(JsonValues.StringOf))}, got {string.Join(", ", records.Select(r => JsonValues.StringOf(r["node"]!["ref"])))}");
            }
        }

        foreach (var (k, locale) in e["locales"] as JsonObject ?? [])
        {
            var got = records.Where(r => JsonValues.StringOf(r["node"]!["ref"]) == k).Select(r => JsonValues.StringOf(r["node"]!["locale"])).ToList();
            if (got.Count == 0 || got.Any(l => l != JsonValues.StringOf(locale)))
            {
                problems.Add($"{k}: expected locale {locale}, got [{string.Join(",", got)}]");
            }
        }

        // Record fields by node ref: each listed field (a dotted path) compared as a JSON value; null means absent.
        foreach (var (k, fields) in e["fields"] as JsonObject ?? [])
        {
            var got = records.Where(r => JsonValues.StringOf(r["node"]!["ref"]) == k).ToList();
            if (got.Count == 0)
            {
                problems.Add($"{k}: expected a result to compare fields with");
            }

            foreach (var r in got)
            {
                foreach (var (path, want) in fields as JsonObject ?? [])
                {
                    JsonNode? value = r;
                    foreach (var part in path.Split('.'))
                    {
                        value = value is JsonObject o ? o[part] : null;
                    }

                    if (Canonical(value) != Canonical(want))
                    {
                        problems.Add($"{k}: expected {path} {Canonical(want)}, got {Canonical(value)}");
                    }
                }
            }
        }

        // Contract §15.4: the counts of the search's U004, each member the case gives compared exactly; null means no U004.
        if (e.TryGetPropertyValue("termsExcluded", out var termsExcluded))
        {
            var got = actual["termsExcluded"];
            if ((termsExcluded is null) != (got is null))
            {
                problems.Add($"termsExcluded: expected {Canonical(termsExcluded)}, got {Canonical(got)}");
            }
            else if (termsExcluded is JsonObject want)
            {
                foreach (var (k, v) in want)
                {
                    if (Canonical(got![k]) != Canonical(v))
                    {
                        problems.Add($"termsExcluded.{k}: expected {Canonical(v)}, got {Canonical(got[k])}");
                    }
                }
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
        // Structure answers are facts: compared exactly, list order included; members of an object (an edition, a
        // concept) in any order, as the runner compares them.
        var want = Canonical(e["result"]);
        var got = Canonical(actual["result"]);
        if (want != got)
        {
            problems.Add($"result: expected {want}, got {got}");
        }

        return problems;
    }

    /// <summary>Contract §15.4, "The catalogue rule": entries compared exactly, in order, each valid against its schema.</summary>
    private static List<string> CheckCatalogue(JsonObject e, JsonObject actual)
    {
        var problems = new List<string>();
        CompareSets("codes", e["codes"] ?? new JsonArray(), actual["codes"], problems);
        var result = actual["result"] as JsonArray ?? [];
        if (Canonical(e["result"]) != Canonical(result))
        {
            problems.Add($"result: expected {Canonical(e["result"])}, got {Canonical(result)}");
        }

        foreach (var entry in result)
        {
            var errors = MocaSchemas.Validate(MocaSchemas.CatalogueEntry, entry);
            if (errors.Count > 0)
            {
                problems.Add($"{entry?["id"]} is not a valid catalogue entry: {errors[0]}");
            }
        }

        return problems;
    }

    /// <summary>A JSON value as text with every object's keys sorted, as the runner's canonical form; null for absent.</summary>
    private static string Canonical(JsonNode? value)
    {
        static JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Sort)]),
            null => null,
            // Numbers as doubles, as JavaScript reads them, so 1e19 and 10000000000000000000 agree.
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => JsonValue.Create(double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)),
            _ => JsonNode.Parse(n.ToJsonString()),
        };

        return Sort(value)?.ToJsonString(JsonValues.CompactJson) ?? "null";
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
