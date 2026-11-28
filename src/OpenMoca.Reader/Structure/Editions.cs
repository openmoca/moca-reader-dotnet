using System.Text.Json.Serialization;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// A slot of a composite package (package spec §7.4): a concept in its own <c>structure.ttl</c> whose editions are
/// alternatives, chosen by language. Plain JSON in a package state (Reader contract §9.4).
/// </summary>
public sealed record EditionSlot
{
    /// <summary>The slot's concept IRI.</summary>
    [JsonPropertyName("iri")] public required string Iri { get; init; }

    /// <summary>The ids of its editions, each one of the composite's own members, in IRI order.</summary>
    [JsonPropertyName("editions")] public required IReadOnlyList<string> Editions { get; init; }

    /// <summary>The edition the slot names with <c>moca:defaultEdition</c>, when it names exactly one of its editions.</summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Default { get; init; }
}

/// <summary>
/// One edition of a slot that can be chosen, as the structure operation <c>editions</c> returns it (Reader contract
/// §10): its package id and version, its manifest's <c>language</c> and <c>title</c> when it has them, and whether it is
/// the slot's default edition.
/// </summary>
/// <param name="Id">The edition's package id.</param>
/// <param name="Version">Its package version.</param>
/// <param name="Language">Its manifest's <c>language</c>, or null when it has none.</param>
/// <param name="Title">Its manifest's <c>title</c>, or null when it has none.</param>
/// <param name="Default">True only for the slot's default edition.</param>
public sealed record EditionItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("language"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Language,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonPropertyName("default")] bool Default);

/// <summary>
/// Edition groups (package spec §7.4, Reader contract §9.2 step 5.4 and §10): reading a composite's slots, and choosing
/// one edition per slot for a locale.
/// </summary>
public static class Editions
{
    /// <summary><c>schema:workExample</c>: from a slot to one of its editions.</summary>
    public const string SchemaWorkExample = "https://schema.org/workExample";

    /// <summary><c>moca:defaultEdition</c>: a slot's default edition.</summary>
    public const string MocaDefaultEdition = "https://w3id.org/moca/ns#defaultEdition";

    /// <summary>
    /// The valid slots of a composite's own structure, ordered by IRI, with <c>O006_EDITION_GROUP_INVALID</c> for each
    /// statement that is ignored: a <c>schema:workExample</c> value that is not the id of one of its own members; a
    /// <c>moca:defaultEdition</c> on a concept with no editions, or naming none of them; two defaults on one slot.
    /// </summary>
    internal static IReadOnlyList<EditionSlot> ReadSlots(StructureGraph graph, IReadOnlyCollection<string> memberIds, DiagnosticBag diagnostics, string file)
    {
        var members = memberIds.ToHashSet(StringComparer.Ordinal);
        var slots = new SortedDictionary<string, (List<string> Editions, string? Default)>(StringComparer.Ordinal);
        foreach (var s in graph.Subjects("workExample").Order(StringComparer.Ordinal))
        {
            var editions = new List<string>();
            foreach (var e in graph.Out("workExample", s))
            {
                if (members.Contains(e.Iri))
                {
                    editions.Add(e.Iri);
                }
                else
                {
                    diagnostics.Add(DiagnosticCodes.O006EditionGroupInvalid,
                        $"{s} has schema:workExample {e.Iri}, which is not the id of one of this package's members; it is ignored", file);
                }
            }

            if (editions.Count > 0)
            {
                slots[s] = (editions, null);
            }
        }

        foreach (var s in graph.Subjects("defaultEdition").Order(StringComparer.Ordinal))
        {
            var named = graph.Out("defaultEdition", s).Select(e => e.Iri).ToList();
            if (named.Count > 1)
            {
                diagnostics.Add(DiagnosticCodes.O006EditionGroupInvalid, $"{s} names {named.Count} default editions; none is used", file);
                continue;
            }

            if (!slots.TryGetValue(s, out var slot) || !slot.Editions.Contains(named[0], StringComparer.Ordinal))
            {
                diagnostics.Add(DiagnosticCodes.O006EditionGroupInvalid,
                    $"{s} has moca:defaultEdition {named[0]}, which is not one of its editions; it is ignored", file);
                continue;
            }

            slots[s] = (slot.Editions, named[0]);
        }

        return [.. slots.Select(kv => new EditionSlot { Iri = kv.Key, Editions = kv.Value.Editions, Default = kv.Value.Default })];
    }

    /// <summary>
    /// Package spec §7.4, "Choosing": the edition a slot chooses for a locale, among the editions that can be chosen:
    /// the one the §5.4 lookup selects, each tagged with its language; else the default edition; else the lowest id in
    /// code-point order. Null when none can be chosen.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="available">Each edition that can be chosen (resolved, pin-matched, valid), by id, with its language.</param>
    /// <param name="locale">The priority list; null or empty for none.</param>
    public static string? Choose(EditionSlot slot, IReadOnlyDictionary<string, string?> available, IReadOnlyList<string>? locale)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(available);
        var ids = slot.Editions.Where(available.ContainsKey).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        if (LocaleLookup.Lookup(ids.Select(id => (available[id], id)), locale) is { } byLanguage)
        {
            return byLanguage;
        }

        return slot.Default is { } d && available.ContainsKey(d) ? d : ids[0];
    }
}
