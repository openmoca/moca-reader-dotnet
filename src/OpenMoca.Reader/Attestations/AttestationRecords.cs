using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Attestations;

/// <summary>What a Reader concluded about one attestation file.</summary>
public sealed class AttestationRecord
{
    /// <summary>The package-relative file.</summary>
    public required string File { get; init; }

    /// <summary>The in-toto predicate type, when the file parsed.</summary>
    public string? PredicateType { get; init; }

    /// <summary><c>package</c> or <c>review</c>.</summary>
    public string? Role { get; init; }

    /// <summary><c>dsse</c> or <c>sigstore</c>.</summary>
    public string? Format { get; init; }

    /// <summary>valid, invalid, unverifiable, indeterminate, malformed or ignored.</summary>
    public required string Outcome { get; set; }

    /// <summary>The signer's identity, when the signature verified.</summary>
    public string? Signer { get; init; }

    /// <summary>Why the outcome is not valid.</summary>
    public string? Reason { get; set; }

    /// <summary>Review files whose bytes still match.</summary>
    public IReadOnlyList<string> Current { get; set; } = [];

    /// <summary>Review files whose bytes have changed or been removed (A006).</summary>
    public IReadOnlyList<string> Outdated { get; set; } = [];
}

/// <summary>A review that verified and matches a current file (citation record <c>attestedReviews</c>).</summary>
public sealed record AttestedReview(string Reviewer, string ReviewedAt, string Outcome, string Signer, string? Scope = null);

/// <summary>
/// Detached attestations (attestations spec): DSSE envelopes (<c>*.dsse.json</c>) or Sigstore bundles
/// (<c>*.sigstore.json</c>) wrapping an in-toto v1 Statement with a MOCA predicate type.
/// </summary>
public static class AttestationFormats
{
    /// <summary>The in-toto Statement type.</summary>
    public const string StatementType = "https://in-toto.io/Statement/v1";

    /// <summary>The DSSE payload type for in-toto.</summary>
    public const string PayloadType = "application/vnd.in-toto+json";

    /// <summary>"This exact package was published by this signer."</summary>
    public const string PredicatePackage = "https://w3id.org/moca/attestation/package/v1";

    /// <summary>"This reviewer checked these exact node files."</summary>
    public const string PredicateReview = "https://w3id.org/moca/attestation/review/v1";

    /// <summary>A parsed attestation: its container, envelope and statement.</summary>
    public sealed record Parsed(string Format, JsonObject Container, JsonObject Envelope, JsonObject Statement);

    /// <summary>Parses an attestation file into its envelope and statement, or returns why it is malformed.</summary>
    public static (Parsed? Value, string? Reason) Parse(string path, byte[] bytes)
    {
        var format = path.EndsWith(".sigstore.json", StringComparison.Ordinal) ? "sigstore"
            : path.EndsWith(".dsse.json", StringComparison.Ordinal) ? "dsse" : null;
        if (format is null)
        {
            return (null, "attestation files must end in .dsse.json or .sigstore.json");
        }

        JsonObject? container;
        try
        {
            container = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
        }
        catch (JsonException)
        {
            return (null, "not valid JSON");
        }

        var envelope = format == "dsse" ? container : container?["dsseEnvelope"] as JsonObject;
        if (envelope is null || JsonValues.StringOf(envelope["payload"]) is not { } payload
            || JsonValues.StringOf(envelope["payloadType"]) is not { } payloadType)
        {
            return (null, format == "dsse" ? "not a DSSE envelope" : "Sigstore bundle has no dsseEnvelope");
        }

        if (payloadType != PayloadType)
        {
            return (null, $"unexpected payloadType {payloadType}");
        }

        if (Dsse.DecodeBase64(payload) is not { } payloadBytes)
        {
            return (null, "payload is not base64 or base64url");
        }

        JsonObject? statement;
        try
        {
            statement = JsonNode.Parse(Encoding.UTF8.GetString(payloadBytes), documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return (null, "payload is not a JSON in-toto statement");
        }

        if (statement is null || JsonValues.StringOf(statement["_type"]) != StatementType
            || statement["subject"] is not JsonArray || JsonValues.StringOf(statement["predicateType"]) is null)
        {
            return (null, "payload is not an in-toto v1 Statement");
        }

        return (new Parsed(format, container!, envelope, statement), null);
    }

    /// <summary>
    /// Verifies the signature on an attestation against the trust root. It does not check what the
    /// statement says; see <see cref="CheckPackageStatement"/> and <see cref="CheckReviewStatement"/>.
    /// </summary>
    /// <returns>valid, invalid or unverifiable, with the signer or a reason.</returns>
    public static (string Outcome, string? Signer, string? Reason) VerifySignature(Parsed parsed, TrustRoot? trustRoot, string role, DateTimeOffset now) =>
        VerifySignatureWithScope(parsed, trustRoot, role, now) is var (outcome, signer, reason, _, _) ? (outcome, signer, reason) : default;

    /// <summary>As <see cref="VerifySignature"/>, with the signer's <c>ids</c> scope (package spec §7.2).</summary>
    /// <param name="parsed">The parsed attestation.</param>
    /// <param name="trustRoot">The host's trust root.</param>
    /// <param name="role"><c>package</c> or <c>review</c>.</param>
    /// <param name="now">The host clock, for key expiry.</param>
    /// <param name="sigstore">Verifies Sigstore bundles; null leaves them unverifiable.</param>
    public static (string Outcome, string? Signer, string? Reason, IReadOnlyList<string> Ids, IReadOnlyList<string>? Reviewers) VerifySignatureWithScope(Parsed parsed, TrustRoot? trustRoot, string role, DateTimeOffset now, ISigstoreVerifier? sigstore = null)
    {
        if (trustRoot is null)
        {
            return ("unverifiable", null, "no trust root was supplied", [], null);
        }

        if (parsed.Format == "dsse")
        {
            var result = Dsse.Verify(parsed.Envelope, keyId => trustRoot.Key(keyId, role, now)?.PublicKey);
            if (!result.Ok)
            {
                return ("invalid", null, result.Reason, [], null);
            }

            return ("valid", trustRoot.Key(result.KeyId!, role, now)?.Identity ?? result.KeyId, null, trustRoot.Ids(result.KeyId!), trustRoot.Reviewers(result.KeyId!));
        }

        return VerifySigstore(parsed, trustRoot, role, sigstore);
    }

    // Attestations §7, step 6: unverifiable when the trust root lists no Sigstore identities at all, when there is no
    // trust material, or when no verifier is plugged in (an optional feature); identities that exist but are not
    // trusted for this role make it invalid, as an untrusted DSSE key does.
    private static (string, string?, string?, IReadOnlyList<string>, IReadOnlyList<string>?) VerifySigstore(Parsed parsed, TrustRoot trustRoot, string role, ISigstoreVerifier? sigstore)
    {
        var sigstoreDoc = trustRoot.Document["sigstore"] as JsonObject;
        if ((sigstoreDoc?["identities"] as JsonArray ?? []).Count == 0)
        {
            return ("unverifiable", null, "the trust root lists no Sigstore identities", [], null);
        }

        if (sigstoreDoc!["trustedRoot"] is not JsonObject trustedRoot)
        {
            return ("unverifiable", null, "no sigstore.trustedRoot: this Reader verifies Sigstore offline, against the host's trusted root", [], null);
        }

        if (sigstore is null)
        {
            return ("unverifiable", null, "Sigstore verification needs the OpenMoca.Reader.Sigstore package (ReadOptions.SigstoreVerifier)", [], null);
        }

        var identities = trustRoot.SigstoreIdentities(role);
        if (identities.Count == 0)
        {
            return ("invalid", null, $"no Sigstore identity in the trust root is trusted for the {role} role", [], null);
        }

        var result = sigstore.Verify(parsed.Container, trustedRoot);
        if (result.Outcome != "valid")
        {
            return (result.Outcome, null, result.Reason, [], null);
        }

        // Attestations §6: issuer and subject alternative name match exactly; no patterns.
        foreach (var identity in identities)
        {
            if (JsonValues.StringOf(identity["issuer"]) == result.Issuer && JsonValues.StringOf(identity["subject"]) == result.SubjectAlternativeName)
            {
                var ids = (identity["ids"] as JsonArray ?? []).Select(JsonValues.StringOf).OfType<string>().ToList();
                IReadOnlyList<string>? reviewers = identity["reviewers"] is JsonArray listed ? [.. listed.Select(JsonValues.StringOf).OfType<string>()] : null;
                return ("valid", $"{result.Issuer} {result.SubjectAlternativeName}", null, ids, reviewers);
            }
        }

        return ("invalid", null, $"the certificate identity ({result.Issuer} {result.SubjectAlternativeName}) is not one the trust root lists for the {role} role", [], null);
    }

    /// <summary>Why a package statement does not attest this package, or null when it does.</summary>
    public static string? CheckPackageStatement(JsonObject statement, string id, string version, string digest)
    {
        if (JsonValues.StringOf(statement["predicateType"]) != PredicatePackage)
        {
            return "not a package attestation";
        }

        var subjects = (JsonArray)statement["subject"]!;
        var subject = subjects.FirstOrDefault() as JsonObject;
        var name = JsonValues.StringOf(subject?["name"]);
        if (subjects.Count != 1 || name != $"{id}@{version}")
        {
            return $"subject \"{name}\" does not name {id}@{version}";
        }

        var hex = JsonValues.StringOf((subject!["digest"] as JsonObject)?["sha256"]);
        return hex == digest.Replace("sha256:", "", StringComparison.Ordinal) ? null : "subject digest does not match the package digest";
    }

    /// <summary>Checks a review statement: its predicate, its package, and which files still match.</summary>
    public static (string? Error, IReadOnlyList<string> Current, IReadOnlyList<string> Outdated, JsonObject? Predicate) CheckReviewStatement(
        JsonObject statement, string packageId, IReadOnlyDictionary<string, string> fileDigests)
    {
        var predicate = statement["predicate"] as JsonObject;
        var errors = MocaSchemas.Validate(MocaSchemas.ReviewPredicate, statement["predicate"]);
        if (errors.Count > 0 || predicate is null)
        {
            return ($"review predicate is invalid: {string.Join("; ", errors)}", [], [], null);
        }

        if (JsonValues.StringOf(predicate["package"]) != packageId)
        {
            return ($"review names package {JsonValues.StringOf(predicate["package"])}, not {packageId}", [], [], null);
        }

        var current = new List<string>();
        var outdated = new List<string>();
        foreach (var node in (JsonArray)statement["subject"]!)
        {
            var name = JsonValues.StringOf((node as JsonObject)?["name"]);
            var sha = JsonValues.StringOf(((node as JsonObject)?["digest"] as JsonObject)?["sha256"]);
            if (name is null || sha is null)
            {
                return ("review subject entries need a name and a sha256 digest", [], [], null);
            }

            if (fileDigests.TryGetValue(UnicodeText.Normalize(name, NormalizationForm.FormC), out var hex) && hex == sha)
            {
                current.Add(name);
            }
            else
            {
                outdated.Add(name);
            }
        }

        return (null, current, outdated, predicate);
    }
}
