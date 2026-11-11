using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Attestations;

/// <summary>
/// A host trust root (attestations spec §6): which keys and Sigstore identities the host trusts, and
/// for which roles. A package never supplies one (Reader interface §3).
/// </summary>
public sealed class TrustRoot
{
    private readonly Dictionary<string, JsonObject> _keys = new(StringComparer.Ordinal);

    private TrustRoot(JsonObject document)
    {
        Document = document;
        foreach (var key in (document["keys"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (JsonValues.StringOf(key["keyid"]) is { } id && !_keys.TryAdd(id, key))
            {
                // Attestations §6: keyids are unique.
                throw new ArgumentException($"invalid trust root: keyid \"{id}\" appears more than once", nameof(document));
            }
        }
    }

    /// <summary>The trust-root document.</summary>
    public JsonObject Document { get; }

    /// <summary>Loads and validates a trust-root document.</summary>
    /// <exception cref="ArgumentException">The document does not validate against the trust-root schema.</exception>
    public static TrustRoot Load(JsonNode? document)
    {
        var errors = MocaSchemas.Validate(MocaSchemas.TrustRoot, document);
        if (errors.Count > 0 || document is not JsonObject obj)
        {
            throw new ArgumentException($"invalid trust root: {string.Join("; ", errors)}", nameof(document));
        }

        return new TrustRoot(obj);
    }

    /// <summary>Loads a trust root from a JSON file.</summary>
    public static TrustRoot LoadFile(string path) => Load(JsonNode.Parse(File.ReadAllText(path)));

    /// <summary>The reviewers a key may sign reviews as (attestations §4), or null for any.</summary>
    public IReadOnlyList<string>? Reviewers(string keyId) =>
        _keys.TryGetValue(keyId, out var entry) && entry["reviewers"] is JsonArray reviewers
            ? reviewers.Select(JsonValues.StringOf).OfType<string>().ToList()
            : null;

    /// <summary>
    /// The package ids a key may speak for in <c>supersedes</c> and <c>conflictsWith</c> relations (package spec
    /// §7.2), or an empty list.
    /// </summary>
    public IReadOnlyList<string> Ids(string keyId) =>
        _keys.TryGetValue(keyId, out var entry) && entry["ids"] is JsonArray ids
            ? ids.Select(JsonValues.StringOf).OfType<string>().ToList()
            : [];

    /// <summary>
    /// Package spec §7.2: whether a signer's <c>ids</c> cover a package id: an entry equal to it, or one ending in
    /// <c>/</c>, <c>:</c> or <c>#</c> that it starts with.
    /// </summary>
    public static bool ScopeCovers(IEnumerable<string> scopes, string? packageId) =>
        packageId is not null && scopes.Any(s => s == packageId
            || (s.Length > 0 && (s[^1] is '/' or ':' or '#') && packageId.StartsWith(s, StringComparison.Ordinal)));

    /// <summary>A trusted key for a role, or null when the key is unknown, not trusted for the role, or expired.</summary>
    public (string PublicKey, string Identity)? Key(string keyId, string role, DateTimeOffset now)
    {
        if (!_keys.TryGetValue(keyId, out var entry) || !Allows(entry, role))
        {
            return null;
        }

        // Attestations §6: a key is not trusted from the instant it expires; one whose expiry does not parse is not
        // trusted at all.
        if (entry["expires"] is not null && (Dates.Parse(entry["expires"]) is not { } expires || now >= expires))
        {
            return null;
        }

        return (JsonValues.StringOf(entry["publicKey"]) ?? "", JsonValues.StringOf(entry["identity"]) ?? keyId);
    }

    /// <summary>The Sigstore identities trusted for a role.</summary>
    public IReadOnlyList<JsonObject> SigstoreIdentities(string role) =>
        ((Document["sigstore"] as JsonObject)?["identities"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => Allows(i, role))
            .ToList();

    // Attestations §6: `roles` is required; an empty list trusts the entry for nothing.
    private static bool Allows(JsonObject entry, string role) =>
        entry["roles"] is JsonArray roles && roles.Any(r => JsonValues.StringOf(r) == role);
}
