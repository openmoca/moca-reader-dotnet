using OpenMoca.Reader.Json;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Content;

namespace OpenMoca.Reader.Citations;

/// <summary>Relation effects among loaded packages (package spec §7.2), keyed by <c>id@version</c>.</summary>
/// <param name="Superseded">Package key to the packages that supersede it.</param>
/// <param name="Contested">Package key to the packages it conflicts with.</param>
public sealed record RelationIndex(
    IReadOnlyDictionary<string, List<string>> Superseded,
    IReadOnlyDictionary<string, List<string>> Contested)
{
    /// <summary>No relations.</summary>
    public static RelationIndex Empty { get; } = new(new Dictionary<string, List<string>>(), new Dictionary<string, List<string>>());
}

/// <summary>Builds citation records and applies the default retrieval policy (Reader contract §7, §8).</summary>
public static class Citations
{
    private static readonly HashSet<string> Statuses = ["draft", "stable", "deprecated"];

    /// <summary>
    /// The default retrieval policy (contract §8): not in force, superseded or deprecated content is
    /// left out unless the host opts in.
    /// </summary>
    public static bool PassesDefaultPolicy(CitationRecord record) =>
        record.Trust.InForce && !record.Trust.Superseded && record.Trust.Status != "deprecated";

    /// <summary>The representation for a locale: exact tag, then a matching language prefix, then the default (package spec §5.4).</summary>
    public static Representation PickRepresentation(ContentNode node, string? locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return node.Representations[0];
        }

        var want = locale.ToLowerInvariant();
        // Package spec §5.4: exact; then the longest tag the request starts with (RFC 4647 Lookup); then the lowest
        // tag, in code-point order, that starts with the request.
        var tagged = node.Representations.Where(r => r.Locale is not null).ToList();
        return tagged.FirstOrDefault(r => r.Locale!.ToLowerInvariant() == want)
               ?? tagged.Where(r => want.StartsWith(r.Locale!.ToLowerInvariant() + "-", StringComparison.Ordinal)).OrderByDescending(r => r.Locale!.Length).FirstOrDefault()
               ?? tagged.Where(r => r.Locale!.ToLowerInvariant().StartsWith(want + "-", StringComparison.Ordinal)).OrderBy(r => r.Locale, StringComparer.Ordinal).FirstOrDefault()
               ?? node.Representations[0];
    }

    /// <summary>Builds one citation record for a representation of a node.</summary>
    /// <param name="result">The package.</param>
    /// <param name="node">The node.</param>
    /// <param name="rep">Which representation.</param>
    /// <param name="now">The host clock.</param>
    /// <param name="index">Relation effects among loaded packages.</param>
    /// <param name="text">The text to include, or null for none.</param>
    public static CitationRecord For(PackageResult result, ContentNode node, Representation rep, DateTimeOffset now, RelationIndex? index = null, string? text = null)
    {
        var manifest = result.Manifest!;
        var fm = rep.Frontmatter;
        var moca = rep.Moca;
        var key = $"{result.Id}@{result.Version}";
        // Package spec §4.4: the node's window is the intersection of the package's and its own. A bound that does
        // not parse, or a window whose end is not after its start, is ignored: it never takes a node out of force.
        var (validFrom, validUntil, from, until) = Dates.EffectiveWindow(
            Str(manifest["validFrom"]), Str(manifest["validUntil"]), Str(moca["valid_from"]), Str(moca["valid_until"]));
        var supersededBy = index?.Superseded.GetValueOrDefault(key) ?? [];
        var contestedBy = (moca["contested_by"] as JsonArray ?? []).Select(Str).OfType<string>()
            .Concat(index?.Contested.GetValueOrDefault(key) ?? [])
            .ToList();
        var sources = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var s in (fm["sources"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(s["id"]) is { Length: > 0 } id)
            {
                sources.TryAdd(id, s);
            }
        }

        var audience = Search.Audience.Of(node, rep);
        var staleAfter = DateOrNull(Str(fm["stale_after"]) ?? fm["stale_after"]?.ToJsonString());
        var staleAt = Dates.Parse(staleAfter);

        var evidence = new List<CitationEvidence>();
        if (moca["evidence"] is JsonArray entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JsonObject e)
                {
                    continue;
                }

                var sourceId = Str(e["source"]);
                var source = sourceId is not null && sources.TryGetValue(sourceId, out var declared)
                    ? (JsonObject)declared.DeepClone()
                    : new JsonObject { ["id"] = e["source"]?.DeepClone() };
                var matched = i < rep.EvidenceChecks.Count ? rep.EvidenceChecks[i].Matched : null;
                evidence.Add(new CitationEvidence(
                    source,
                    e["selector"] is JsonObject selector ? (JsonObject)selector.DeepClone() : null,
                    Str(e["note"]) is { Length: > 0 } note ? note : null,
                    matched));
            }
        }

        var body = rep.Body;
        return new CitationRecord
        {
            Package = new CitationPackage(result.Id, result.Version, result.Digest ?? "", result.Signers.Count > 0,
                result.Signers.Count > 0 ? [.. result.Signers] : null),
            Node = new CitationNode
            {
                Id = $"{result.Id}#{node.Path}",
                Ref = $"{result.Id}@{result.Version}#{node.Path}",
                Path = node.Path,
                Locale = rep.Locale ?? Str(manifest["language"]),
                Type = Str(fm["type"]) ?? "",
                Title = Str(fm["title"]),
                Description = Str(fm["description"]),
                Tags = fm["tags"] is JsonArray tags ? tags.Select(Str).OfType<string>().ToList() : null,
                Digest = rep.Sha256 is not null ? $"sha256:{rep.Sha256}" : null,
            },
            Span = new CitationSpan(rep.BodyOffset, rep.BodyOffset + Encoding.UTF8.GetByteCount(body)),
            Text = text,
            Trust = new CitationTrust
            {
                Status = Str(fm["status"]) is { } status && Statuses.Contains(status) ? status : "stable",
                // An entry with no actor (`by`) is dropped rather than reported as "undefined".
                Generated = fm["generated"] is JsonObject generated && HasActor(generated)
                    ? new DeclaredBy(generated["by"]!.ToString(), generated["at"]?.ToString())
                    : null,
                DeclaredVerified = VerifiedEntries(fm["verified"]),
                AttestedReviews = result.ReviewsByFile.TryGetValue(rep.File, out var reviews) ? reviews.Select(r => CitationReview.From(r, result.Signers.Count > 0)).ToList() : [],
                ReviewStatus = Reviews.StatusOf(result.ReviewsByFile.GetValueOrDefault(rep.File), result.Signers.Count > 0),
                StaleAfter = staleAfter,
                ValidFrom = validFrom,
                ValidUntil = validUntil,
                Stale = staleAfter is not null && staleAt is not null && now >= staleAt,
                InForce = (validFrom is null || (from is not null && now >= from)) && (validUntil is null || (until is not null && now < until)),
                Superseded = supersededBy.Count > 0,
                SupersededBy = supersededBy.Count > 0 ? [.. supersededBy] : null,
                Contested = contestedBy.Count > 0,
                ContestedBy = contestedBy.Count > 0 ? contestedBy : null,
            },
            Evidence = evidence,
            Concepts = rep.Concepts is { } concepts ? [.. concepts] : null,
            Audience = audience.Label,
            AudienceInvalid = audience.Invalid ? true : null,
            Profiles = result.Profiles.Count > 0 ? [.. result.Profiles] : null,
        };
    }

    private static List<DeclaredBy> VerifiedEntries(JsonNode? verified)
    {
        IEnumerable<JsonNode?> items = verified switch
        {
            JsonArray array => array,
            null => [],
            _ => [verified],
        };
        return items.OfType<JsonObject>()
            .Where(HasActor)
            .Select(v => new DeclaredBy(v["by"]!.ToString(), v["at"]?.ToString()))
            .ToList();
    }

    /// <summary>An OKF <c>generated</c> or <c>verified</c> entry that names its actor.</summary>
    private static bool HasActor(JsonObject entry) => entry["by"] is { } by && by.ToString().Length > 0;

    /// <summary>A frontmatter or manifest date that parses, or null.</summary>
    private static string? DateOrNull(string? value) =>
        string.IsNullOrEmpty(value) || Dates.Parse(value) is null ? null : value;

    private static string? Str(JsonNode? node) => JsonValues.StringOf(node);
}
