using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader;

/// <summary>
/// A package's trust state worked out from what a store keeps, without the package's content: its manifest, its files'
/// SHA-256 digests and its attestation files, under a trust root at a time (attestations §6–§7, Reader contract §6).
/// It runs the same checks, in the same code, as <see cref="MocaReader.ReadPackageAsync"/>, so the signers, reviews,
/// issued copy, relation scopes and diagnostics are those opening the package would give.
/// </summary>
/// <remarks>
/// Use it when a host keeps packages broken apart in a store and wants to re-establish trust when its trust root
/// changes (a key removed before its expiry, a new publisher), or to store trust as dated fields it can filter on, then
/// feed the result into the package's <see cref="PackageState"/> with <see cref="PackageState.WithTrust"/>.
/// </remarks>
public static partial class PackageTrust
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hex64();

    /// <summary>
    /// The trust state under a trust root at a time. A convenience for <see cref="Evaluate(ReadOnlyMemory{byte}, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, byte[]}, ReadOptions?)"/>
    /// with default options.
    /// </summary>
    /// <param name="manifest">The bytes of <c>moca.json</c>, exactly as in the package.</param>
    /// <param name="fileDigests">Every file outside <c>attestations/</c>, by package path, with its lowercase hexadecimal SHA-256.</param>
    /// <param name="attestations">Every file under <c>attestations/</c>, by package path, with its bytes.</param>
    /// <param name="trustRoot">The host's trust root; null makes every attestation unverifiable (A003).</param>
    /// <param name="at">The time to evaluate at, for key expiry and review dates.</param>
    public static TrustState Evaluate(ReadOnlyMemory<byte> manifest, IReadOnlyDictionary<string, string> fileDigests,
        IReadOnlyDictionary<string, byte[]> attestations, TrustRoot? trustRoot, DateTimeOffset at) =>
        Evaluate(manifest, fileDigests, attestations, new ReadOptions { TrustRoot = trustRoot, Clock = () => at });

    /// <summary>
    /// The trust state under the options' trust root at the options' clock, with its dates (<see cref="TrustState.TrustedFrom"/>,
    /// <see cref="TrustState.TrustedUntil"/>). Never throws for what the package holds: a manifest that is not JSON gives a
    /// state with <c>M001</c> and no signers.
    /// </summary>
    /// <param name="manifest">The bytes of <c>moca.json</c>, exactly as in the package. Its SHA-256 must be the one <paramref name="fileDigests"/> gives it.</param>
    /// <param name="fileDigests">Every file outside <c>attestations/</c>, by package path, with its lowercase hexadecimal SHA-256: the package's <see cref="PackageResult.FileDigests"/>.</param>
    /// <param name="attestations">Every file under <c>attestations/</c>, by package path, with its bytes.</param>
    /// <param name="options">The trust root, Sigstore verifier, clock, limits, strict mode and <c>requireSignature</c>; null for defaults.</param>
    /// <exception cref="ArgumentException">The inputs do not describe one package: a digest that is not SHA-256 hex, a
    /// path in the wrong map, no <c>moca.json</c>, or a manifest whose bytes do not match its digest. These are a store's
    /// errors, not the package's.</exception>
    public static TrustState Evaluate(ReadOnlyMemory<byte> manifest, IReadOnlyDictionary<string, string> fileDigests,
        IReadOnlyDictionary<string, byte[]> attestations, ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileDigests);
        ArgumentNullException.ThrowIfNull(attestations);
        options ??= new ReadOptions();
        var digests = Normalised(fileDigests, nameof(fileDigests), attestation: false);
        var files = Normalised(attestations, nameof(attestations), attestation: true);
        foreach (var (path, hex) in digests)
        {
            if (!Hex64().IsMatch(hex))
            {
                throw new ArgumentException($"the digest of {path} is not lowercase hexadecimal SHA-256", nameof(fileDigests));
            }
        }

        if (!digests.TryGetValue(PackageDigest.ManifestFile, out var manifestDigest))
        {
            throw new ArgumentException("the file digests have no moca.json", nameof(fileDigests));
        }

        if (PackageDigest.Sha256Hex(manifest.Span) != manifestDigest)
        {
            throw new ArgumentException("the manifest's bytes do not match the digest given for moca.json", nameof(manifest));
        }

        var at = options.Clock();
        using var activity = ReaderTelemetry.Source.StartActivity(ReaderTelemetry.PackageTrustSpan);
        var result = new PackageResult
        {
            FileDigests = digests,
            Digest = $"sha256:{PackageDigest.Sha256Hex(Encoding.UTF8.GetBytes(PackageDigest.ManifestText(digests)))}",
        };
        var diagnostics = new DiagnosticBag(options.Strict);
        var manifestValid = false;
        try
        {
            // As the open path (MocaReader): strict UTF-8, a leading BOM ignored, and no repeated member names.
            var text = StrictUtf8.GetString(manifest.Span).TrimStart('﻿');
            if (JsonValues.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject parsed)
            {
                result.Manifest = parsed;
                var schema = new DiagnosticBag();
                ManifestChecks.Check(parsed, schema, options.KnownProfiles);
                manifestValid = !schema.Has(DiagnosticCodes.M002ManifestSchema);
            }
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            diagnostics.Add(DiagnosticCodes.M001ManifestInvalidJson, $"moca.json is not valid UTF-8 JSON: {ex.Message}", PackageDigest.ManifestFile);
        }

        if (result.Manifest is not null)
        {
            MocaReader.ReadAttestations(result, files, options, diagnostics, manifestValid);
        }

        // Attestations §8: a host can require a valid package attestation.
        if (options.RequireSignature && result.Signers.Count == 0)
        {
            diagnostics.Add(DiagnosticCodes.A009SignatureRequired, "the host requires a valid package attestation, and this package has none", PackageDigest.ManifestFile);
        }

        if (result.Manifest is not null)
        {
            MocaReader.CheckRelationAuthority(result, diagnostics);
        }

        var keys = KeysOf(result.Attestations, options.TrustRoot);
        var from = Dates.Parse(result.Manifest?["validFrom"]);
        var until = Earliest(keys.Select(k => k.Expires)
            .Append(Dates.Parse(result.Manifest?["validUntil"]))
            .Concat(result.Attestations.Select(r => r.CountsFrom)));
        var state = new TrustState
        {
            Digest = result.Digest,
            Id = result.Id,
            Version = result.Version,
            At = at,
            Signers = [.. result.Signers],
            RelationScopes = [.. result.RelationScopes],
            Keys = keys,
            Attestations = [.. result.Attestations],
            Reviews = result.ReviewsByFile.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AttestedReview>)[.. kv.Value], StringComparer.Ordinal),
            IssuedCopy = result.IssuedCopy,
            TermsAuthority = TermsRules.AuthorityOf(result.Signers),
            Diagnostics = diagnostics.Items,
            TrustedFrom = from,
            TrustedUntil = until,
        };

        if (activity is not null)
        {
            activity.SetTag("moca.package.digest", state.Digest);
            activity.SetTag("moca.attestations.count", state.Attestations.Count);
            activity.SetTag("moca.attestations.valid", state.Attestations.Count(a => a.Outcome == "valid"));
            activity.SetTag("moca.signers.count", state.Signers.Count);
            activity.SetTag("moca.diagnostics.count", state.Diagnostics.Count);
            activity.SetTag("moca.trust.signed", state.Signed);
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        return state;
    }

    // Package spec §6.2: paths are compared in NFC; each map holds only its own side of attestations/.
    private static Dictionary<string, T> Normalised<T>(IReadOnlyDictionary<string, T> map, string name, bool attestation)
    {
        var output = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (path, value) in map)
        {
            ArgumentNullException.ThrowIfNull(value, name);
            var nfc = UnicodeText.Normalize(path, NormalizationForm.FormC);
            if (PackageDigest.IsAttestationPath(nfc) != attestation)
            {
                throw new ArgumentException(attestation
                    ? $"{path} is not under attestations/"
                    : $"{path} is under attestations/, which the digest leaves out", name);
            }

            if (!output.TryAdd(nfc, value))
            {
                throw new ArgumentException($"{path} appears more than once after NFC normalisation", name);
            }
        }

        return output;
    }

    // Attestations §6: each valid attestation and the trust-root key behind it, with when that key stops being trusted.
    private static List<TrustKey> KeysOf(IEnumerable<AttestationRecord> records, TrustRoot? trustRoot) =>
        [.. records.Where(r => r.Outcome == "valid")
            .Select(r => new TrustKey(r.File, r.Role ?? "", r.Signer ?? "", r.KeyId, r.KeyId is { } id ? trustRoot?.Expires(id) : null))];

    private static DateTimeOffset? Earliest(IEnumerable<DateTimeOffset?> times)
    {
        DateTimeOffset? earliest = null;
        foreach (var t in times)
        {
            if (t is { } value && (earliest is null || value < earliest))
            {
                earliest = value;
            }
        }

        return earliest;
    }
}

/// <summary>A valid attestation and the trust-root entry that verified it (attestations §6).</summary>
/// <param name="File">The attestation file.</param>
/// <param name="Role"><c>package</c>, <c>review</c> or <c>issued-copy</c>.</param>
/// <param name="Signer">The signer's identity as the trust root names it.</param>
/// <param name="KeyId">The DSSE key's <c>keyid</c>; null for Sigstore.</param>
/// <param name="Expires">When the key stops being trusted; null for a key with no <c>expires</c>, and for Sigstore.</param>
public sealed record TrustKey(string File, string Role, string Signer, string? KeyId, DateTimeOffset? Expires);

/// <summary>
/// A package's trust state (<see cref="PackageTrust"/>): what opening the package at <see cref="At"/> under the same
/// trust root concludes about its attestations, and the window in which that conclusion holds.
/// </summary>
public sealed record TrustState
{
    /// <summary>The package digest, computed from the file digests.</summary>
    public required string Digest { get; init; }

    /// <summary>The manifest's <c>id</c>; empty when the manifest is not JSON.</summary>
    public required string Id { get; init; }

    /// <summary>The manifest's <c>version</c>; empty when the manifest is not JSON.</summary>
    public required string Version { get; init; }

    /// <summary>The time the state was worked out at.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether a valid package attestation signs the package (<c>signed</c>).</summary>
    public bool Signed => Signers.Count > 0;

    /// <summary>Signers of the package's valid package attestations, as <see cref="PackageResult.Signers"/>.</summary>
    public IReadOnlyList<string> Signers { get; init; } = [];

    /// <summary>Package ids the signers may speak for (package spec §7.2), as <see cref="PackageResult.RelationScopes"/>.</summary>
    public IReadOnlyList<string> RelationScopes { get; init; } = [];

    /// <summary>Each valid attestation's key, with its expiry.</summary>
    public IReadOnlyList<TrustKey> Keys { get; init; } = [];

    /// <summary>Every attestation file's outcome, with the reason for any that is not valid, as <see cref="PackageResult.Attestations"/>.</summary>
    public IReadOnlyList<AttestationRecord> Attestations { get; init; } = [];

    /// <summary>Current reviews per file, the latest from each signer, as <see cref="PackageResult.ReviewsByFile"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AttestedReview>> Reviews { get; init; } = new Dictionary<string, IReadOnlyList<AttestedReview>>();

    /// <summary>The issued copy, when one verified (attestations §7.1).</summary>
    public IssuedCopy? IssuedCopy { get; init; }

    /// <summary>Who states the package's terms (Reader contract §15.2): <c>packager</c> when signed, else <c>unverified</c>.</summary>
    public string TermsAuthority { get; init; } = global::OpenMoca.Reader.TermsAuthority.Unverified;

    /// <summary>The trust findings opening the package would report: A001 to A010, R004, and M001 for a manifest that is not JSON.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// The start of the window: the package's own <c>validFrom</c> (package spec §4.4), or null for no start. The trust
    /// root sets no start for a key, so a state that holds at <see cref="At"/> holds from then back to this.
    /// </summary>
    public DateTimeOffset? TrustedFrom { get; init; }

    /// <summary>
    /// The end of the window, exclusive: the earliest of the package's own <c>validUntil</c>, the expiry of each key behind
    /// a valid attestation, and the date of a review ignored as later than <see cref="At"/> (A007). Until then, only a
    /// change to the trust root changes the state. Null when none of these is set.
    /// </summary>
    public DateTimeOffset? TrustedUntil { get; init; }

    /// <summary>Whether <paramref name="now"/> is in the window, from inclusive and until exclusive (package spec §4.4).</summary>
    public bool Holds(DateTimeOffset now) => (TrustedFrom is null || now >= TrustedFrom) && (TrustedUntil is null || now < TrustedUntil);
}
