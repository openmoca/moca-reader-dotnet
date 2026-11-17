using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Attestations;

/// <summary>The outcome of pass 1 for one issued-copy attestation (attestations §7.1).</summary>
/// <param name="Error">Why it is invalid, or null when it passed.</param>
/// <param name="Original">The publisher's digest, <c>sha256:&lt;hex&gt;</c>, when it passed.</param>
/// <param name="Changed">Each changed NFC path with the publisher's hash, when it passed.</param>
public sealed record IssuedCopyCheck(string? Error, string? Original = null, IReadOnlyDictionary<string, string>? Changed = null);

/// <summary>Attestations §3a and §7.1: issued copies, pass 1.</summary>
public static partial class IssuedCopies
{
    /// <summary>
    /// Attestations §7.1, pass 1, after the signature verified with role <c>issued-copy</c>.
    /// </summary>
    /// <param name="statement">The in-toto statement.</param>
    /// <param name="signature">The signature check, with the verifying key or identity.</param>
    /// <param name="manifest">The copy's manifest, the publisher's.</param>
    /// <param name="digest">The copy's computed digest.</param>
    /// <param name="fileDigests">The copy's file hashes by NFC path.</param>
    /// <param name="manifestValid">Whether the manifest passed its schema; a manifest that did not allows nothing.</param>
    public static IssuedCopyCheck Check(JsonObject statement, SignatureCheck signature, JsonObject manifest, string digest, IReadOnlyDictionary<string, string> fileDigests, bool manifestValid)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(fileDigests);
        if (JsonValues.StringOf(statement["predicateType"]) != AttestationFormats.PredicateIssuedCopy)
        {
            return new("not an issued-copy attestation");
        }

        var id = JsonValues.StringOf(manifest["id"]);
        var version = JsonValues.StringOf(manifest["version"]);
        var subjects = statement["subject"] as JsonArray ?? [];
        var first = subjects.Count > 0 ? subjects[0] as JsonObject : null;
        var name = JsonValues.StringOf(first?["name"]);
        if (subjects.Count != 1 || name != $"{id}@{version}")
        {
            return new($"subject \"{name}\" does not name {id}@{version}");
        }

        if (JsonValues.StringOf((first?["digest"] as JsonObject)?["sha256"]) != digest["sha256:".Length..])
        {
            return new("subject digest does not match the copy");
        }

        var predicate = statement["predicate"] as JsonObject;
        var original = JsonValues.StringOf((predicate?["original"] as JsonObject)?["sha256"]);
        if (original is null || !Hex64().IsMatch(original))
        {
            return new("original.sha256 is not a SHA-256 digest");
        }

        if ($"sha256:{original}" == digest)
        {
            return new("original is the digest of the copy itself");
        }

        if (predicate?["changedFiles"] is not JsonObject listed || listed.Count == 0)
        {
            return new("changedFiles is missing or empty");
        }

        // Step 2: the role needs ids that cover the package.
        if (!TrustRoot.ScopeCovers(signature.Ids, id))
        {
            return new($"the issuer's trust-root entry has no ids covering {id}");
        }

        // Step 3: the publisher's consent.
        if (!manifestValid || manifest["issuedCopies"] is not JsonObject consent)
        {
            return new("the publisher allows no issued copies (no issuedCopies in moca.json)");
        }

        if (!IssuerAllowed(consent["issuers"] as JsonArray ?? [], signature))
        {
            return new("the issuer is not in the publisher's issuedCopies.issuers");
        }

        // Step 4: each changed file.
        var paths = (consent["paths"] as JsonArray ?? []).Select(JsonValues.StringOf).OfType<string>().Select(p => UnicodeText.Normalize(p, NormalizationForm.FormC)).ToList();
        var defined = ProfileFiles(manifest);
        var changed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, entry) in listed)
        {
            var hash = entry is JsonObject e ? JsonValues.StringOf(e["original"]) : null;
            if (path != UnicodeText.Normalize(path, NormalizationForm.FormC))
            {
                return new($"changedFiles key {path} is not in NFC");
            }

            if (!path.StartsWith("content/", StringComparison.Ordinal) && !path.StartsWith("media/", StringComparison.Ordinal))
            {
                return new($"{path} is not under content/ or media/");
            }

            if (path.Split('/').Any(s => s.StartsWith('.')))
            {
                return new($"{path} is a hidden entry");
            }

            if (defined.Contains(path))
            {
                return new($"{path} is a file a profile defines");
            }

            if (!fileDigests.TryGetValue(path, out var current))
            {
                return new($"{path} is not a file in the copy");
            }

            if (hash is null || !Hex64().IsMatch(hash))
            {
                return new($"{path}: original is not a SHA-256 hash");
            }

            if (current == hash)
            {
                return new($"{path} is listed but unchanged");
            }

            if (!paths.Any(p => p.EndsWith('/') ? path.StartsWith(p, StringComparison.Ordinal) : path == p))
            {
                return new($"{path} is outside the paths the publisher allows");
            }

            changed[path] = hash;
        }

        // Step 5: rebuild the publisher's digest.
        return RebuiltDigest(fileDigests, changed) == $"sha256:{original}"
            ? new(null, $"sha256:{original}", changed)
            : new("the rebuilt digest does not equal original");
    }

    /// <summary>
    /// Attestations §7.1 step 5: the digest of the copy's manifest with each changed file's original hash.
    /// </summary>
    /// <param name="fileDigests">The copy's file hashes by NFC path.</param>
    /// <param name="changed">Each changed NFC path with the publisher's hash.</param>
    public static string RebuiltDigest(IReadOnlyDictionary<string, string> fileDigests, IReadOnlyDictionary<string, string> changed)
    {
        ArgumentNullException.ThrowIfNull(fileDigests);
        ArgumentNullException.ThrowIfNull(changed);
        var rebuilt = new Dictionary<string, string>(fileDigests, StringComparer.Ordinal);
        foreach (var (path, hash) in changed)
        {
            rebuilt[path] = hash;
        }

        return $"sha256:{PackageDigest.Sha256Hex(Encoding.UTF8.GetBytes(PackageDigest.ManifestText(rebuilt)))}";
    }

    /// <summary>Whether two passing checks agree on original and changedFiles.</summary>
    public static bool Same(IssuedCopyCheck a, IssuedCopyCheck b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Original == b.Original && a.Changed!.Count == b.Changed!.Count
            && a.Changed.All(kv => b.Changed.TryGetValue(kv.Key, out var h) && h == kv.Value);
    }

    // Package spec §4.11: a DSSE key matches a publicKey entry by its Ed25519 bytes; a Sigstore identity matches a
    // sigstore entry by issuer and subject, exactly.
    private static bool IssuerAllowed(JsonArray issuers, SignatureCheck signature)
    {
        var signed = Dsse.Ed25519KeyBytes(signature.PublicKey);
        foreach (var entry in issuers.OfType<JsonObject>())
        {
            if (JsonValues.StringOf(entry["publicKey"]) is { } pem && signed is not null)
            {
                if (Dsse.Ed25519KeyBytes(pem) is { } listed && listed.AsSpan().SequenceEqual(signed))
                {
                    return true;
                }
            }
            else if (entry["sigstore"] is JsonObject sigstore && signature.SigstoreIssuer is not null
                && JsonValues.StringOf(sigstore["issuer"]) == signature.SigstoreIssuer && JsonValues.StringOf(sigstore["subject"]) == signature.SigstoreSubject)
            {
                return true;
            }
        }

        return false;
    }

    // Files the profiles this Reader recognises define (package spec §9): the ontology profile's files.
    private static HashSet<string> ProfileFiles(JsonObject manifest) =>
        [.. (((manifest["profiles"] as JsonObject)?[Profiles.Ontology] as JsonObject)?["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(f => JsonValues.StringOf(f["path"])).OfType<string>().Select(p => UnicodeText.Normalize(p, NormalizationForm.FormC))];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hex64();
}
