using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Search;

namespace OpenMoca.Reader;

/// <summary>
/// The creator's terms (package spec §4.14, Reader contract §15): checking, authority, resolution and the gate. Terms
/// are labels, not locks: nothing here fetches anything, configures the Reader, or changes what a read or a search
/// without a terms request returns.
/// </summary>
internal static partial class TermsRules
{
    /// <summary>A permission of <c>ai</c>: where it is, and its values from loosest to strictest.</summary>
    private sealed record Field(string Name, string? AllowedKey, JsonNode[] Order);

    private static readonly Field[] Fields =
    [
        new("allowed.device", "device", [true, false]),
        new("allowed.host", "host", [true, false]),
        new("allowed.provider", "provider", [true, false]),
        new("allowed.training", "training", [true, false]),
        new("allowed.machineTranslation", "machineTranslation", [true, false]),
        new("allowed.mention", "mention", [true, false]),
        new("answerSources", null, ["model", "work"]),
    ];

    /// <summary>Contract §15.3: used only when no level sets a field (as an index into the field's order).</summary>
    private static readonly Dictionary<string, int> Platform = new(StringComparer.Ordinal)
    {
        ["allowed.device"] = 0,
        ["allowed.host"] = 0,
        ["allowed.provider"] = 0,
        ["allowed.training"] = 1,
        ["allowed.machineTranslation"] = 0,
        ["allowed.mention"] = 0,
        ["answerSources"] = 0,
    };

    /// <summary>
    /// Contract §15.3 (moca-spec ADR-0041): fields with no node level. In a <c>nodes</c> entry they are <c>U003</c> (the
    /// terms schema does not list them there) and have no effect, even under a malformed entry.
    /// </summary>
    private static readonly HashSet<string> PackageOnly = new(StringComparer.Ordinal) { "allowed.mention" };

    private static readonly HashSet<string> KnownConditions = new(StringComparer.Ordinal) { "noTraining", "noRetention" };

    private static readonly string[] Destinations = [TermsDestinations.Device, TermsDestinations.Host, TermsDestinations.Provider];

    /// <summary>
    /// The entry every node gets when <c>nodes</c>, or one of its entries, does not say which nodes it means (contract
    /// §15.3): <c>ai</c> malformed and <c>rating</c> malformed, so everything is at its strictest.
    /// </summary>
    private static readonly JsonObject StrictestEntry = new() { ["ai"] = null, ["rating"] = false };

    /// <summary>The terms of a package with no terms result (its manifest failed its schema): everything at its strictest.</summary>
    internal static readonly JsonObject Strictest = new()
    {
        ["ai"] = new JsonObject
        {
            ["allowed"] = new JsonObject { ["device"] = false, ["host"] = false, ["provider"] = false, ["training"] = false, ["machineTranslation"] = false, ["mention"] = false },
            ["answerSources"] = "work",
        },
        ["rights"] = new JsonObject { ["export"] = new JsonObject { ["allowed"] = false } },
    };

    [GeneratedRegex(@"^([0-9]{1,2})-([0-9]{1,2})?\z")]
    private static partial Regex AgeRange();

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{4})\z")]
    private static partial Regex YearRange();

    /// <summary>A member of an object as written: whether it is present (a JSON null is present), and its value.</summary>
    private readonly record struct Slot(bool Present, JsonNode? Value)
    {
        public static Slot Of(JsonNode? container, string key) =>
            container is JsonObject o && o.TryGetPropertyValue(key, out var v) ? new Slot(true, v) : default;

        public static Slot Given(JsonNode? value) => new(true, value);
    }

    // ---- Checking (package spec §4.14) ----

    /// <summary>
    /// Checks <c>terms</c> against the terms schema. Each problem is <c>U003</c> (a string where only listed strings are
    /// allowed, or a member the schema does not list) or <c>U001</c> (anything else, and the checks the schema cannot
    /// express). Each comes with a message.
    /// </summary>
    public static List<(TermsProblem Problem, string Message)> Check(JsonNode? terms)
    {
        var problems = new List<(TermsProblem, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string code, string path, string message)
        {
            if (seen.Add($"{code} {path}"))
            {
                problems.Add((new TermsProblem(code, path), message));
            }
        }

        foreach (var (where, evaluation, keyword, message) in MocaSchemas.Failures(MocaSchemas.Terms, terms))
        {
            // A failed combinator, or a keyword that only says something below it failed, is reported where it happened.
            if (keyword is "oneOf" or "anyOf" or "if" or "properties" or "items" or "additionalProperties" or "$ref" or "allOf"
                or "prefixItems" or "contains" or "patternProperties" or "propertyNames" or "dependentSchemas")
            {
                continue;
            }

            var path = FieldPath(where);
            if (keyword.Length == 0 && evaluation.EndsWith("/additionalProperties", StringComparison.Ordinal))
            {
                Add(DiagnosticCodes.U003TermsUnknownValue, path, $"{path} is a member this version does not know");
            }
            else if (keyword == "enum" && ValueAt(terms, where) is JsonValue v && v.TryGetValue<string>(out var s))
            {
                Add(DiagnosticCodes.U003TermsUnknownValue, path, $"{path} has a value this version does not know: \"{s}\"");
            }
            else
            {
                var at = path.Length > 0 ? path : "(terms)";
                Add(DiagnosticCodes.U001TermsInvalid, at, $"{(path.Length > 0 ? path : "terms")}: {message}");
            }
        }

        var nodes = terms?["nodes"] as JsonArray;
        var ratings = new List<(string At, JsonNode? Rating)> { ("rating", terms?["rating"]) };
        var copyrights = new List<(string At, JsonNode? Copyright)> { ("rights.copyright", terms?["rights"]?["copyright"]) };
        for (var i = 0; nodes is not null && i < nodes.Count; i++)
        {
            ratings.Add(($"nodes.{i}.rating", (nodes[i] as JsonObject)?["rating"]));
            copyrights.Add(($"nodes.{i}.rights.copyright", ((nodes[i] as JsonObject)?["rights"] as JsonObject)?["copyright"]));
        }

        foreach (var (at, r) in ratings)
        {
            if (r is not JsonObject rating)
            {
                continue;
            }

            if (JsonValues.StringOf(rating["typicalAgeRange"]) is { } range && AgeRange().Match(range) is { Success: true } m
                && m.Groups[2].Success && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            {
                Add(DiagnosticCodes.U001TermsInvalid, $"{at}.typicalAgeRange", $"{at}.typicalAgeRange has its lower bound above its upper");
            }

            if (rating["contentRating"] is JsonArray entries)
            {
                var schemes = entries.Select(x => JsonValues.StringOf((x as JsonObject)?["scheme"])).OfType<string>().ToList();
                if (schemes.Distinct(StringComparer.Ordinal).Count() != schemes.Count)
                {
                    Add(DiagnosticCodes.U001TermsInvalid, $"{at}.contentRating", $"{at}.contentRating has two entries for one scheme");
                }
            }
        }

        // A copyright year range must not end before it starts.
        foreach (var (at, c) in copyrights)
        {
            if (JsonValues.StringOf((c as JsonObject)?["year"]) is { } year && YearRange().Match(year) is { Success: true } y
                && string.CompareOrdinal(y.Groups[1].Value, y.Groups[2].Value) > 0)
            {
                Add(DiagnosticCodes.U001TermsInvalid, $"{at}.year", $"{at}.year has its first year after its last");
            }
        }

        return problems;
    }

    /// <summary>A JSON Pointer as a dotted path, as written (no unescaping).</summary>
    private static string FieldPath(string pointer) => string.Join('.', pointer.Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static JsonNode? ValueAt(JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject o => o[key],
                JsonArray a when int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
        }

        return node;
    }

    /// <summary>
    /// Contract §15.3 step 2, reported when the package is read: a <c>nodes</c> entry whose <c>ai</c> value is looser than
    /// the package's own. One message per entry and field.
    /// </summary>
    public static List<string> Loosened(JsonNode? terms)
    {
        var output = new List<string>();
        if (terms?["nodes"] is not JsonArray nodes)
        {
            return output;
        }

        var packageAi = Slot.Of(terms, "ai");
        for (var i = 0; i < nodes.Count; i++)
        {
            var entryAi = Slot.Of(nodes[i], "ai");
            foreach (var field in Fields.Where(f => !PackageOnly.Contains(f.Name)))
            {
                var pkg = Rank(field, AiValue(packageAi, field));
                var node = Rank(field, AiValue(entryAi, field));
                if (pkg is { } p && node is { } n && p > n)
                {
                    output.Add($"nodes[{i}] sets {field.Name} {field.Order[n].ToJsonString()}, looser than the package's {field.Order[p].ToJsonString()}; the package's value stands");
                }
            }

            var pc = Conditions(packageAi);
            var nc = Conditions(entryAi);
            if (pc is not null && nc is not null && pc.Any(c => !nc.Contains(c)))
            {
                output.Add($"nodes[{i}] drops provider conditions the package sets; the package's stand");
            }
        }

        return output;
    }

    /// <summary>Contract §15.2: from the package's valid package attestations only, never from fields the signer writes.</summary>
    public static string AuthorityOf(IReadOnlyCollection<string> signers) =>
        signers.Count > 0 ? TermsAuthority.Packager : TermsAuthority.Unverified;

    // ---- Resolution (contract §15.3) ----

    /// <summary>Stands for "present and not a known value", so the strictest.</summary>
    private static readonly JsonNode Malformed = new JsonObject();

    /// <summary>The value a level sets for a field: null when nothing sets it; <see cref="Malformed"/> under a container of the wrong type.</summary>
    private static (bool Set, JsonNode? Value) AiValue(Slot ai, Field field)
    {
        if (!ai.Present)
        {
            return (false, null);
        }

        // Contract §15.3: a container (ai, ai.allowed) that is present and not an object makes every permission under it the strictest.
        if (ai.Value is not JsonObject)
        {
            return (true, Malformed);
        }

        if (field.AllowedKey is { } key)
        {
            var allowed = Slot.Of(ai.Value, "allowed");
            if (!allowed.Present)
            {
                return (false, null);
            }

            if (allowed.Value is not JsonObject)
            {
                return (true, Malformed);
            }

            var value = Slot.Of(allowed.Value, key);
            return value.Present ? (true, value.Value) : (false, null);
        }

        var own = Slot.Of(ai.Value, field.Name);
        return own.Present ? (true, own.Value) : (false, null);
    }

    /// <summary>A level's value as an index into the field's order; a malformed or unknown value is the strictest.</summary>
    private static int? Rank(Field field, (bool Set, JsonNode? Value) v)
    {
        if (!v.Set)
        {
            return null;
        }

        for (var i = 0; i < field.Order.Length; i++)
        {
            if (Same(field.Order[i], v.Value))
            {
                return i;
            }
        }

        return field.Order.Length - 1;
    }

    /// <summary>Strict equality of a known value (a boolean or a string) with a value as written.</summary>
    private static bool Same(JsonNode known, JsonNode? value)
    {
        if (value is not JsonValue v || ReferenceEquals(value, Malformed))
        {
            return false;
        }

        return known.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => v.GetValueKind() == System.Text.Json.JsonValueKind.True,
            System.Text.Json.JsonValueKind.False => v.GetValueKind() == System.Text.Json.JsonValueKind.False,
            _ => v.TryGetValue<string>(out var s) && s == known.GetValue<string>(),
        };
    }

    /// <summary>The conditions a level sets; an unknown or malformed one is kept, so no host can meet it.</summary>
    private static List<string>? Conditions(Slot ai)
    {
        if (!ai.Present)
        {
            return null;
        }

        if (ai.Value is not JsonObject)
        {
            return ["(malformed)"];
        }

        var requires = Slot.Of(ai.Value, "providerRequires");
        if (!requires.Present)
        {
            return null;
        }

        if (requires.Value is not JsonArray list)
        {
            return ["(malformed)"];
        }

        return [.. list.Select(c => JsonValues.StringOf(c) is { } s && KnownConditions.Contains(s) ? s : $"(unknown:{Display(c)})")];
    }

    private static string Display(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    private static bool WellFormedEntry(JsonNode? entry) =>
        entry is JsonObject o && o["paths"] is JsonArray paths && paths.All(p => JsonValues.StringOf(p) is not null);

    /// <summary>
    /// The <c>nodes</c> entry that applies to a node path: the longest matching path or <c>/</c>-ended prefix (package spec
    /// §4.14), with its index. Contract §15.3: a <c>nodes</c> that is present and not a list, an entry that is not an
    /// object, and an entry whose <c>paths</c> is not a list of strings count for every node, at the strictest (index -1).
    /// </summary>
    private static (JsonObject? Entry, int Index) NodeEntry(JsonNode? terms, string path)
    {
        var nodes = Slot.Of(terms, "nodes");
        if (!nodes.Present)
        {
            return (null, -1);
        }

        if (nodes.Value is not JsonArray list || !list.All(WellFormedEntry))
        {
            return (StrictestEntry, -1);
        }

        var full = $"content/{path}";
        JsonObject? best = null;
        var bestIndex = -1;
        var bestLength = -1;
        for (var i = 0; i < list.Count; i++)
        {
            var entry = (JsonObject)list[i]!;
            foreach (var p in ((JsonArray)entry["paths"]!).Select(JsonValues.StringOf).OfType<string>())
            {
                var hit = p.EndsWith('/') ? full.StartsWith(p, StringComparison.Ordinal) : full == p;
                if (hit && p.Length > bestLength)
                {
                    best = entry;
                    bestIndex = i;
                    bestLength = p.Length;
                }
            }
        }

        return (best, bestIndex);
    }

    /// <summary>
    /// Contract §15.3: the resolved <c>ai</c> terms for one node. Each level applies only if it is stricter: no authority
    /// in this version lets a package loosen the platform defaults.
    /// </summary>
    /// <param name="terms">The package's declared terms.</param>
    /// <param name="path">
    /// The node path under content/; null for the package-level terms, with no node level (contract §15.4, "Closed and
    /// hidden").
    /// </param>
    /// <param name="limits">The host's limits, shaped as <c>terms.ai</c>: floors, never values.</param>
    /// <param name="composites">The terms of composing packages whose signers speak for this package (step 3).</param>
    public static CitationAiTerms ResolveAi(JsonNode? terms, string? path, JsonObject? limits, IReadOnlyList<JsonNode?> composites)
    {
        var node = path is null ? null : NodeEntry(terms, path).Entry;
        var levels = new List<(Slot Ai, string Level)> { (Slot.Of(terms, "ai"), "package"), (Slot.Of(node, "ai"), "node") };
        levels.AddRange(composites.Select(c => (Slot.Of(c, "ai"), "composite")));
        if (limits is not null)
        {
            levels.Add((Slot.Given(limits), "host"));
        }

        var resolved = new Dictionary<string, (int Rank, string DecidedBy)>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var value = Platform[field.Name];
            var decidedBy = "platform";
            foreach (var (ai, level) in levels)
            {
                // A package-level field has no node level (contract §15.3).
                if (level == "node" && PackageOnly.Contains(field.Name))
                {
                    continue;
                }

                if (Rank(field, AiValue(ai, field)) is { } n && n >= value)
                {
                    value = n;
                    decidedBy = level;
                }
            }

            resolved[field.Name] = (value, decidedBy);
        }

        var conditions = new List<string>();
        var conditionsBy = "platform";
        foreach (var (ai, level) in levels)
        {
            if (Conditions(ai) is not { } c)
            {
                continue;
            }

            var before = conditions.Count;
            foreach (var x in c.Where(x => !conditions.Contains(x)))
            {
                conditions.Add(x);
            }

            if (conditions.Count > before)
            {
                conditionsBy = level;
            }
        }

        conditions.Sort(StringComparer.Ordinal);
        ResolvedTerm<bool> Bool(string name) => new(resolved[name].Rank == 0, resolved[name].DecidedBy);
        return new CitationAiTerms(
            new CitationAllowedTerms(Bool("allowed.device"), Bool("allowed.host"), Bool("allowed.provider"), Bool("allowed.training"), Bool("allowed.machineTranslation"),
                Bool("allowed.mention")),
            new ResolvedTerm<string>(resolved["answerSources"].Rank == 0 ? "model" : "work", resolved["answerSources"].DecidedBy),
            new ResolvedTerm<IReadOnlyList<string>>(conditions, conditionsBy));
    }

    /// <summary>
    /// Contract §15.3: the resolved export permission (<c>rights.export.allowed</c>). Only the package and a composing
    /// package whose signers speak for the member set it; the platform default is true, and a malformed value counts as false.
    /// </summary>
    public static ResolvedTerm<bool> ResolveExport(JsonNode? terms, IReadOnlyList<JsonNode?> composites)
    {
        var value = true;
        var decidedBy = "platform";
        void Apply(JsonNode? t, string level)
        {
            var rights = Slot.Of(t, "rights");
            if (!rights.Present)
            {
                return;
            }

            bool allowed;
            if (rights.Value is not JsonObject)
            {
                // A rights or rights.export that is present and not an object: not allowed.
                allowed = false;
            }
            else
            {
                var export = Slot.Of(rights.Value, "export");
                if (!export.Present)
                {
                    return;
                }

                if (export.Value is JsonObject e)
                {
                    var a = Slot.Of(e, "allowed");
                    if (!a.Present)
                    {
                        return;
                    }

                    allowed = a.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                }
                else
                {
                    allowed = false;
                }
            }

            if (!allowed || value)
            {
                value = allowed;
                decidedBy = level;
            }
        }

        Apply(terms, "package");
        foreach (var c in composites)
        {
            Apply(c, "composite");
        }

        return new ResolvedTerm<bool>(value, decidedBy);
    }

    /// <summary>
    /// Contract §15.3: the node's effective rating as written: its own <c>nodes</c> entry's, if any, else the package's.
    /// Absent: null. A value that is present and not an object is returned as it is (JSON null as false): the gate counts
    /// it as the strictest rating.
    /// </summary>
    public static JsonNode? RatingOf(JsonNode? terms, string path)
    {
        var node = NodeEntry(terms, path).Entry;
        var own = node is not null && node.ContainsKey("rating") ? Slot.Of(node, "rating") : Slot.Of(terms, "rating");
        return !own.Present ? null : own.Value ?? JsonValue.Create(false);
    }

    // ---- The gate (contract §15.4) ----

    /// <summary>
    /// Contract §15.4, "Closed and hidden" (moca-spec ADR-0041): whether a package is closed for a request's <c>ai</c>
    /// part, from its package-level resolved terms (no node level), and whether it is hidden. A use never closes a
    /// package; a request with no <c>ai</c> part closes and hides nothing.
    /// </summary>
    /// <param name="request">The terms request, or null for none.</param>
    /// <param name="packageAi">The package-level resolved terms (<see cref="ResolveAi"/> with a null path).</param>
    public static PackageClosure ClosureOf(TermsRequest? request, CitationAiTerms packageAi)
    {
        if (request?.Ai is not { } a)
        {
            return PackageClosure.Open;
        }

        var destination = Destinations.Contains(a.Destination) ? a.Destination : TermsDestinations.Provider;
        var conditions = new HashSet<string>(a.Conditions ?? [], StringComparer.Ordinal);
        var allowed = destination switch
        {
            TermsDestinations.Device => packageAi.Allowed.Device,
            TermsDestinations.Host => packageAi.Allowed.Host,
            _ => packageAi.Allowed.Provider,
        };
        string? closedBy = null;
        if (!allowed.Value)
        {
            closedBy = $"ai.allowed.{destination}";
        }
        else if (destination == TermsDestinations.Provider && packageAi.ProviderRequires.Value.Any(c => !conditions.Contains(c)))
        {
            closedBy = "ai.providerRequires";
        }

        return closedBy is null ? PackageClosure.Open : new PackageClosure(true, [closedBy], !packageAi.Allowed.Mention.Value);
    }

    /// <summary>The reasons the terms gate removes a record, or none.</summary>
    public static List<string> GateReasons(TermsRequest request, CitationAiTerms ai, JsonNode? rating)
    {
        var reasons = new List<string>();
        if (request.Ai is { } a)
        {
            // An unknown or missing destination is treated as the strictest, provider.
            var destination = Destinations.Contains(a.Destination) ? a.Destination : TermsDestinations.Provider;
            var allowed = destination switch
            {
                TermsDestinations.Device => ai.Allowed.Device,
                TermsDestinations.Host => ai.Allowed.Host,
                _ => ai.Allowed.Provider,
            };
            var conditions = new HashSet<string>(a.Conditions ?? [], StringComparer.Ordinal);
            if (!allowed.Value)
            {
                reasons.Add($"ai.allowed.{destination}");
            }
            else if (destination == TermsDestinations.Provider && ai.ProviderRequires.Value.Any(c => !conditions.Contains(c)))
            {
                reasons.Add("ai.providerRequires");
            }

            if (a.Use == "train" && !ai.Allowed.Training.Value)
            {
                reasons.Add("ai.allowed.training");
            }

            if (a.Use == "translate" && !ai.Allowed.MachineTranslation.Value)
            {
                reasons.Add("ai.allowed.machineTranslation");
            }
        }

        if (request.Rating is { } r)
        {
            // A request this version cannot honour in full is never run as a weaker one: it removes every record.
            if (r.Unhonourable)
            {
                reasons.Add("rating.request");
                return reasons;
            }

            // A rating that is present and malformed counts as the strictest: explicit, and outside every listed scheme.
            var malformed = rating is not null && rating is not JsonObject;
            var obj = rating as JsonObject;
            var fromFeed = JsonValues.StringOf(obj?["declaredBy"]) == "feed";
            bool? isExplicit = null;
            if (malformed)
            {
                isExplicit = true;
            }
            else if (obj is not null && obj.TryGetPropertyValue("explicit", out var e))
            {
                isExplicit = e is JsonValue ev && ev.TryGetValue<bool>(out var eb) ? eb : true;
                // A feed's explicit: false counts only when the host accepts feed ratings; otherwise it is unrated.
                if (fromFeed && isExplicit == false && !r.AcceptFeedRatings)
                {
                    isExplicit = null;
                }
            }

            var entries = obj is not null && obj.TryGetPropertyValue("contentRating", out var cr) ? cr : new JsonArray();
            var unreadable = malformed || entries is not JsonArray list
                || list.Any(x => x is not JsonObject o || JsonValues.StringOf(o["scheme"]) is null);
            var items = unreadable ? [] : ((JsonArray)entries!).OfType<JsonObject>().ToList();
            // Only the schemes the host listed count: a rating under any other scheme says nothing to this filter.
            var listed = (r.Schemes ?? new Dictionary<string, IReadOnlyList<string>?>())
                .Where(kv => unreadable || items.Any(x => JsonValues.StringOf(x["scheme"]) == kv.Key))
                .ToList();
            if (isExplicit is null && listed.Count == 0)
            {
                if (!r.AllowUnrated)
                {
                    reasons.Add("rating.unrated");
                }
            }
            else
            {
                if (r.Explicit == false && isExplicit == true)
                {
                    reasons.Add("rating.explicit");
                }

                foreach (var (scheme, values) in listed)
                {
                    var entry = unreadable ? null : items.FirstOrDefault(x => JsonValues.StringOf(x["scheme"]) == scheme);
                    var value = JsonValues.StringOf(entry?["value"]);
                    if (entry is null || values is null || value is null || !values.Contains(value))
                    {
                        reasons.Add($"rating.{scheme}");
                    }
                }
            }
        }

        return reasons;
    }

    /// <summary>The <c>terms</c> of a citation record from a search with a terms request.</summary>
    public static CitationTerms RecordTerms(string authority, CitationAiTerms ai, JsonNode? rating, ResolvedTerm<bool> export)
    {
        // A rating that is present and not an object is shown as what it counts as: explicit.
        var shown = rating switch
        {
            null => null,
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => new JsonObject { ["explicit"] = true },
        };
        return new CitationTerms(authority, ai, shown, new CitationRightsTerms(export));
    }

    // ---- Rights in citation records (contract §7) ----

    /// <summary>
    /// A node's licence and copyright: the node's <c>terms.nodes</c> entry first, else the package's (<c>license</c> from
    /// the manifest, <c>terms.rights.copyright</c>). A value with a terms problem at or under its path is skipped.
    /// </summary>
    /// <param name="terms">The declared terms.</param>
    /// <param name="problems">The terms problems.</param>
    /// <param name="license">The manifest's license, when the manifest passes its schema.</param>
    /// <param name="path">The node path under content/.</param>
    public static (string? License, JsonObject? Copyright) RightsOf(JsonNode? terms, IReadOnlyList<TermsProblem> problems, string? license, string path)
    {
        var (entry, index) = NodeEntry(terms, path);
        var at = entry is not null && index >= 0 ? $"nodes.{index}.rights" : null;
        bool Clean(string p) => !problems.Any(x => x.Path == p || x.Path.StartsWith(p + ".", StringComparison.Ordinal));
        var rights = entry?["rights"] as JsonObject;

        var outLicense = at is not null && JsonValues.StringOf(rights?["license"]) is { } own && Clean($"{at}.license") ? own : license;
        JsonObject? copyright = null;
        if (at is not null && rights?["copyright"] is JsonObject nodeCopyright && Clean($"{at}.copyright"))
        {
            copyright = (JsonObject)nodeCopyright.DeepClone();
        }
        else if ((terms?["rights"] as JsonObject)?["copyright"] is JsonObject packageCopyright && Clean("rights.copyright"))
        {
            copyright = (JsonObject)packageCopyright.DeepClone();
        }

        return (outLicense, copyright);
    }
}
