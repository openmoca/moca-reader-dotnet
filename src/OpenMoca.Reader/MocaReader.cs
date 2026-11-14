using OpenMoca.Reader.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader;

/// <summary>
/// Reads MOCA packages (Reader contract §5): opens a package from any source, checks it, computes its
/// digest, verifies attestations against the host's trust root, resolves members through the host's
/// resolver, and derives capabilities. It never executes package content, never performs network I/O
/// of its own, and never acts on a location or setting a package supplies (contract §4).
/// </summary>
public static partial class MocaReader
{
    /// <summary>The specification version this Reader implements, <c>major.minor</c> (package spec §14).</summary>
    public const string MocaVersion = "0.6";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ASCII digits only, and \z so a trailing newline never matches (as in the reference Reader).
    [GeneratedRegex(@"^([0-9]+)\.([0-9]+)\z")]
    private static partial Regex MajorMinor();

    [GeneratedRegex(@"\.(dsse|sigstore)\.json\z")]
    private static partial Regex AttestationFile();

    /// <summary>
    /// Reads a package. Never throws for a malformed package: findings are diagnostics on the result.
    /// </summary>
    /// <param name="target">A directory, archive or host source.</param>
    /// <param name="options">Host options; null for defaults.</param>
    /// <param name="cancellationToken">Cancels member resolution.</param>
    public static Task<PackageResult> ReadPackageAsync(PackageTarget target, ReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ReadInternalAsync(target, options ?? new ReadOptions(), [], new MemberBudget(), cancellationToken);
    }

    private static async Task<PackageResult> ReadInternalAsync(
        PackageTarget target, ReadOptions options, IReadOnlyList<string> stack, MemberBudget budget, CancellationToken cancellationToken,
        EnvelopeCheck? envelopeCheck = null)
    {
        var diagnostics = new DiagnosticBag(options.Strict);
        var result = new PackageResult();

        IPackageSource source;
        try
        {
            source = target.Open(options.Limits);
            source.List();
        }
        catch (TargetException ex)
        {
            diagnostics.Add(ex.Code, ex.Message);
            return Finish(result, diagnostics);
        }

        result.Source = source;
        var (entriesOk, files) = PackageDigest.PackageFiles(source, diagnostics);
        // Encrypted packages §2: an archive with moca-enc.json at its root is an envelope (never the inner archive).
        if (envelopeCheck is null && IsEnvelope(source, files))
        {
            return await ReadEnvelopeAsync((ArchiveSource)source, files, entriesOk, diagnostics, result, options,
                (plain, check) => ReadInternalAsync(PackageTarget.FromArchive(plain), options, stack, budget, cancellationToken, check),
                cancellationToken).ConfigureAwait(false);
        }

        if (!files.ContainsKey(PackageDigest.ManifestFile))
        {
            diagnostics.Add(DiagnosticCodes.T002ManifestNotFound, "the target has no moca.json at its root");
            return Finish(result, diagnostics);
        }

        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var readable = true;
        foreach (var (nfc, stored) in files)
        {
            try
            {
                bytes[nfc] = source.Read(stored);
            }
            catch (TargetException ex)
            {
                // A limit broken while reading rejects the whole source (T003), once.
                diagnostics.Add(ex.Code, ex.Message);
                return Finish(result, diagnostics);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                diagnostics.Add(DiagnosticCodes.P002UnreadableEntry, $"\"{stored}\" could not be read: {ex.Message}", stored);
                readable = false;
            }
        }

        // An entry that cannot be read leaves nothing to check the rest against: stop with P002, as the reference does.
        if (!readable)
        {
            return Finish(result, diagnostics);
        }

        result.FileBytes = bytes;
        JsonObject? manifest;
        try
        {
            // Contract §11, M001: moca.json is UTF-8; invalid bytes are an error, not replaced.
            var text = StrictUtf8.GetString(bytes[PackageDigest.ManifestFile]).TrimStart('\ufeff');
            // I-JSON (RFC 7493): a repeated member name would let Readers disagree on `id`.
            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
            manifest = parsed as JsonObject;
            if (manifest is null)
            {
                // Valid JSON that isn't an object is a schema problem, not a JSON one.
                ManifestChecks.Check(parsed, diagnostics, options.KnownProfiles);
                return Finish(result, diagnostics);
            }
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            diagnostics.Add(DiagnosticCodes.M001ManifestInvalidJson, $"moca.json is not valid UTF-8 JSON: {ex.Message}", PackageDigest.ManifestFile);
            return Finish(result, diagnostics);
        }

        result.Manifest = manifest;
        if (envelopeCheck is not null && OutlineMismatch(envelopeCheck, manifest, bytes, options.Limits) is { } why)
        {
            diagnostics.Add(DiagnosticCodes.E004OutlineMismatch, why, PackageDigest.ManifestFile);
            return Finish(result, diagnostics);
        }

        ManifestChecks.Check(manifest, diagnostics, options.KnownProfiles);
        // Package spec §4.1: the publisher's intent, exposed and never acted on. Null when the manifest fails its schema.
        if (!diagnostics.Items.Any(d => d.Code == DiagnosticCodes.M002ManifestSchema))
        {
            result.Distribution = JsonValues.StringOf(manifest["distribution"]) ?? "open";
            // Package spec §4.9: advice, exposed as declared and never fetched, resolved or opened.
            if (manifest["apps"] is JsonArray apps)
            {
                result.Apps = [.. apps.Select(JsonValues.StringOf).OfType<string>()];
            }

            // Package spec §4.6–4.8: held aside, and exposed by Finish only when the package is valid.
            result.PendingCatalogue = CatalogueChecks.Check(manifest, bytes, diagnostics, options.Limits);
        }
        result.Profiles = (manifest["profiles"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
        // Package spec §14: M008 only for a declared mocaVersion below 0.4; M009 for one newer than this Reader's.
        var declared = MajorMinor().Match(JsonValues.StringOf(manifest["mocaVersion"]) ?? "");
        if (declared.Success && CompareVersion(declared, "0.4") < 0)
        {
            diagnostics.Add(DiagnosticCodes.M008DigestV1Package,
                $"written for mocaVersion {manifest["mocaVersion"]}; its digest is now computed with moca-digest-v2, so pins and attestations made under 0.3 or earlier will not match",
                PackageDigest.ManifestFile);
        }

        if (declared.Success && CompareVersion(declared, MocaVersion) > 0)
        {
            diagnostics.Add(DiagnosticCodes.M009MocaVersionNewer,
                $"written for mocaVersion {manifest["mocaVersion"]}, newer than {MocaVersion}; read on a best-effort basis",
                PackageDigest.ManifestFile);
        }

        // Package spec §6.3: an ambiguous or unreadable package gets no digest.
        if (entriesOk && readable)
        {
            var (digest, fileDigests, payloadManifest) = PackageDigest.Compute(files, nfc => bytes[nfc]);
            result.Digest = digest;
            result.FileDigests = fileDigests;
            result.PayloadManifest = payloadManifest;
        }

        result.Nodes = ContentReader.Read(manifest, files, bytes, result.FileDigests, diagnostics, options.Limits);
        result.Structure = PackageStructure.Read(bytes, result.Nodes, diagnostics, options.Limits, result.Digest);
        // Package spec §4.10: readingOrder resolves against the package's own structure.ttl only.
        if (JsonValues.StringOf(manifest["readingOrder"]) is { } readingOrder && !diagnostics.Items.Any(d => d.Code == DiagnosticCodes.M002ManifestSchema))
        {
            if (ReadingOrder.Resolve(result.Structure?.Graph, readingOrder) is { } order)
            {
                result.ReadingOrder = order.Iri;
                result.ResolvedReadingOrder = order;
            }
            else
            {
                diagnostics.Add(DiagnosticCodes.M014ReadingOrderUnresolved,
                    $"readingOrder {readingOrder} is not an ordered collection with a member list in structure.ttl; it is ignored",
                    PackageDigest.ManifestFile);
            }
        }
        var members = (manifest["members"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (result.Nodes.Count == 0 && members.Count == 0)
        {
            diagnostics.Add(DiagnosticCodes.M003NoContent, "a package needs at least one content node under content/ or at least one member");
        }

        if (result.Digest is not null)
        {
            ReadAttestations(result, bytes, options, diagnostics);
        }

        // Attestations §8: a host can require a valid package attestation.
        if (options.RequireSignature && result.Signers.Count == 0)
        {
            diagnostics.Add(DiagnosticCodes.A009SignatureRequired, "the host requires a valid package attestation, and this package has none", PackageDigest.ManifestFile);
        }

        CheckRelationAuthority(result, diagnostics);
        await ResolveMembersAsync(result, members, options, stack, budget, diagnostics, cancellationToken).ConfigureAwait(false);
        return Finish(result, diagnostics);
    }

    private static void ReadAttestations(PackageResult result, Dictionary<string, byte[]> bytes, ReadOptions options, DiagnosticBag diagnostics)
    {
        var now = options.Clock();
        var attestationFiles = bytes.Keys
            .Where(p => p.StartsWith(PackageDigest.AttestationsDirectory + "/", StringComparison.Ordinal) && AttestationFile().IsMatch(p))
            .Order(StringComparer.Ordinal);
        var pending = new List<(string File, AttestationFormats.Parsed Parsed, string Role, AttestationRecord Record)>();
        foreach (var file in attestationFiles)
        {
            var (parsed, malformed) = AttestationFormats.Parse(file, bytes[file]);
            if (parsed is null)
            {
                diagnostics.Add(DiagnosticCodes.A001AttestationMalformed, malformed!, file);
                result.Attestations.Add(new AttestationRecord { File = file, Outcome = "malformed", Reason = malformed });
                continue;
            }

            var type = JsonValues.StringOf(parsed.Statement["predicateType"]);
            var role = type switch
            {
                AttestationFormats.PredicatePackage => "package",
                AttestationFormats.PredicateReview => "review",
                AttestationFormats.PredicateIssuedCopy => "issued-copy",
                _ => null,
            };
            if (role is null)
            {
                result.Attestations.Add(new AttestationRecord { File = file, PredicateType = type, Outcome = "ignored", Reason = "unknown predicate type" });
                continue;
            }

            var record = new AttestationRecord { File = file, PredicateType = type, Role = role, Format = parsed.Format, Outcome = "pending" };
            result.Attestations.Add(record);
            pending.Add((file, parsed, role, record));
        }

        // Attestations §7.1, pass 1: issued-copy attestations, before the others.
        var passed = new List<(AttestationRecord Record, string File, string Signer, IssuedCopyCheck Check)>();
        foreach (var (file, parsed, role, record) in pending.Where(p => p.Role == "issued-copy"))
        {
            if (Signature(file, parsed, role, record, options, now, diagnostics) is not { } sig)
            {
                continue;
            }

            var check = IssuedCopies.Check(parsed.Statement, sig, result.Manifest!, result.Digest!, result.FileDigests, !diagnostics.Has(DiagnosticCodes.M002ManifestSchema));
            if (check.Error is not null)
            {
                Invalid(record, file, $"issued-copy attestation: {check.Error}", diagnostics);
                continue;
            }

            passed.Add((record, file, sig.Signer!, check));
        }

        IssuedCopyCheck? issued = null;
        if (passed.Count > 0 && passed.All(p => IssuedCopies.Same(p.Check, passed[0].Check)))
        {
            issued = passed[0].Check;
            result.IssuedCopy = new IssuedCopy(issued.Original!, [.. passed.Select(p => p.Signer)], [.. issued.Changed!.Keys.Order(StringComparer.Ordinal)]);
        }
        else
        {
            // Several that disagree are each A002, and none is used.
            foreach (var p in passed)
            {
                Invalid(p.Record, p.File, "issued-copy attestations disagree on original or changedFiles; none is used", diagnostics);
            }
        }

        // Pass 2: package attestations against the rebuilt original digest when there is an issued copy.
        var digest = issued?.Original ?? result.Digest!;
        foreach (var (file, parsed, role, record) in pending.Where(p => p.Role != "issued-copy"))
        {
            if (Signature(file, parsed, role, record, options, now, diagnostics) is not { } sig)
            {
                continue;
            }

            var signer = sig.Signer;
            var reviewers = sig.Reviewers;
            if (role == "package")
            {
                var mismatch = AttestationFormats.CheckPackageStatement(parsed.Statement, result.Id, result.Version, digest);
                if (mismatch is not null)
                {
                    record.Outcome = "invalid";
                    record.Reason = mismatch;
                    // Attestations §3: a statement naming this id@version with another digest is positive evidence that
                    // the content changed after signing.
                    var subjects = parsed.Statement["subject"] as JsonArray;
                    var tampered = subjects is { Count: 1 } && JsonValues.StringOf((subjects[0] as JsonObject)?["name"]) == $"{result.Id}@{result.Version}";
                    diagnostics.Add(tampered ? DiagnosticCodes.A008PackageDigestMismatch : DiagnosticCodes.A002AttestationInvalid,
                        $"package attestation does not match this package: {mismatch}", file);
                }
                else
                {
                    result.Signers.Add(signer!);
                    result.RelationScopes.AddRange(sig.Ids);
                }

                continue;
            }

            var review = AttestationFormats.CheckReviewStatement(parsed.Statement, result.Id, result.FileDigests);
            if (review.Error is not null)
            {
                record.Outcome = "invalid";
                record.Reason = review.Error;
                diagnostics.Add(DiagnosticCodes.A002AttestationInvalid, review.Error, file);
                continue;
            }

            // Attestations §4: a key with a `reviewers` list may only sign as those reviewers.
            var reviewerName = JsonValues.StringOf(review.Predicate!["reviewer"]) ?? "";
            if (reviewers is not null && !reviewers.Contains(reviewerName))
            {
                record.Outcome = "invalid";
                record.Reason = $"the signing key may not sign as reviewer {reviewerName}";
                diagnostics.Add(DiagnosticCodes.A002AttestationInvalid, record.Reason, file);
                continue;
            }

            // A review dated after the host clock does not count.
            if (Dates.Parse(review.Predicate!["reviewedAt"]) is { } reviewedAt && reviewedAt > now)
            {
                record.Outcome = "ignored";
                record.Reason = $"review is dated {JsonValues.StringOf(review.Predicate!["reviewedAt"])}, later than now";
                diagnostics.Add(DiagnosticCodes.A007ReviewDateFuture, record.Reason, file);
                continue;
            }

            // Attestations §7.1: in an issued copy, a review of the publisher's bytes of a changed file is listed with
            // fileChanged and never counts; it is not outdated.
            var subjectsByName = (parsed.Statement["subject"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(x => (Name: JsonValues.StringOf(x["name"]), Sha: JsonValues.StringOf((x["digest"] as JsonObject)?["sha256"])))
                .ToList();
            bool Changed(string path) =>
                issued is not null
                && issued.Changed!.TryGetValue(UnicodeText.Normalize(path, NormalizationForm.FormC), out var original)
                && subjectsByName.Any(x => x.Name == path && x.Sha == original);
            var fileChanged = review.Outdated.Where(Changed).ToList();
            record.Current = review.Current;
            record.Outdated = [.. review.Outdated.Where(p => !Changed(p))];
            foreach (var path in record.Outdated)
            {
                diagnostics.Add(DiagnosticCodes.A006ReviewOutdated, $"review of {path} no longer matches the file", file);
            }

            var predicate = review.Predicate!;
            foreach (var path in review.Current.Concat(fileChanged))
            {
                var entry = new AttestedReview(
                    JsonValues.StringOf(predicate["reviewer"]) ?? "",
                    JsonValues.StringOf(predicate["reviewedAt"]) ?? "",
                    JsonValues.StringOf(predicate["outcome"]) ?? "",
                    signer!,
                    JsonValues.StringOf(predicate["scope"]),
                    fileChanged.Contains(path) ? true : null);
                var key = UnicodeText.Normalize(path, NormalizationForm.FormC);
                if (!result.ReviewsByFile.TryGetValue(key, out var list))
                {
                    list = [];
                    result.ReviewsByFile[key] = list;
                }

                list.Add(entry);
            }
        }

        // Attestations §4: for each file, the latest review from each signer counts.
        foreach (var (path, entries) in result.ReviewsByFile.ToList())
        {
            var latest = new Dictionary<string, AttestedReview>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!latest.TryGetValue(e.Signer, out var prev)
                    || (Dates.Parse(e.ReviewedAt) ?? DateTimeOffset.MinValue) >= (Dates.Parse(prev.ReviewedAt) ?? DateTimeOffset.MinValue))
                {
                    latest[e.Signer] = e;
                }
            }

            result.ReviewsByFile[path] = [.. latest.Values];
        }
    }

    // Attestations §7 steps 6–7: the signature outcome, reported; the check when it is valid, else null.
    private static SignatureCheck? Signature(string file, AttestationFormats.Parsed parsed, string role, AttestationRecord record, ReadOptions options, DateTimeOffset now, DiagnosticBag diagnostics)
    {
        var sig = AttestationFormats.CheckSignature(parsed, options.TrustRoot, role, now, options.SigstoreVerifier);
        record.Outcome = sig.Outcome;
        record.Signer = sig.Signer;
        record.Reason = sig.Reason;
        switch (sig.Outcome)
        {
            case "unverifiable":
                diagnostics.Add(DiagnosticCodes.A003AttestationUnverifiable, $"{role} attestation not checked: {sig.Reason}", file);
                return null;
            case "indeterminate":
                diagnostics.Add(DiagnosticCodes.A004AttestationIndeterminate, sig.Reason ?? "online verification could not complete", file);
                return null;
            case "invalid":
                diagnostics.Add(DiagnosticCodes.A002AttestationInvalid, $"{role} attestation signature did not verify: {sig.Reason}", file);
                return null;
            default:
                return sig;
        }
    }

    private static void Invalid(AttestationRecord record, string file, string reason, DiagnosticBag diagnostics)
    {
        record.Outcome = "invalid";
        record.Reason = reason;
        diagnostics.Add(DiagnosticCodes.A002AttestationInvalid, reason, file);
    }

    // Package spec §7.2: supersedes and conflictsWith take effect only when a signer may speak for the target.
    private static void CheckRelationAuthority(PackageResult result, DiagnosticBag diagnostics)
    {
        foreach (var relation in (result.Manifest?["relations"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = JsonValues.StringOf(relation["type"]);
            var target = JsonValues.StringOf(relation["target"]);
            if (type is not ("supersedes" or "conflictsWith") || target is null)
            {
                continue;
            }

            if (!TrustRoot.ScopeCovers(result.RelationScopes, target))
            {
                diagnostics.Add(DiagnosticCodes.R004RelationUnauthorised,
                    $"{type} {target} is not authorised by a signer of this package, so it has no effect", PackageDigest.ManifestFile);
            }
        }
    }

    private static async Task ResolveMembersAsync(
        PackageResult result, List<JsonObject> members, ReadOptions options, IReadOnlyList<string> stack, MemberBudget budget, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        // Cycles are detected by id@version: composing another version of the same package is not a cycle.
        var self = $"{result.Id}@{result.Version}";
        foreach (var member in members)
        {
            var memberId = JsonValues.StringOf(member["id"]);
            var digest = JsonValues.StringOf(member["digest"]);
            if (string.IsNullOrEmpty(memberId) || string.IsNullOrEmpty(digest))
            {
                continue;
            }

            var reference = new MemberReference(memberId, JsonValues.StringOf(member["version"]), digest);
            var entry = new PackageMember { Reference = reference };
            result.Members.Add(entry);
            if (options.ResolveMember is null)
            {
                continue;
            }

            var memberKey = $"{memberId}@{reference.Version}";
            if (memberKey == self || stack.Contains(memberKey))
            {
                diagnostics.Add(DiagnosticCodes.R003MemberCycle, $"members form a cycle: {string.Join(" -> ", [.. stack, self, memberKey])}", PackageDigest.ManifestFile);
                entry.Status = "cycle";
                continue;
            }

            // Package spec §7.1: depth and the members resolved per read are bounded; a member past either limit is not used.
            if (stack.Count + 1 > options.Limits.MaxMemberDepth)
            {
                diagnostics.Add(DiagnosticCodes.R001MemberUnresolved, $"member {memberKey} is not resolved: composition is deeper than {options.Limits.MaxMemberDepth} levels", PackageDigest.ManifestFile);
                continue;
            }

            if (budget.Resolved >= options.Limits.MaxMembers)
            {
                diagnostics.Add(DiagnosticCodes.R001MemberUnresolved, $"member {memberKey} is not resolved: more than {options.Limits.MaxMembers} members in one read", PackageDigest.ManifestFile);
                continue;
            }

            budget.Resolved++;
            var target = await options.ResolveMember(reference, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                diagnostics.Add(DiagnosticCodes.R001MemberUnresolved, $"member {memberId}@{reference.Version} could not be resolved", PackageDigest.ManifestFile);
                continue;
            }

            var child = await ReadInternalAsync(target, options, [.. stack, self], budget, cancellationToken).ConfigureAwait(false);
            entry.Result = child;
            if (child.Diagnostics.Any(d => d.Code == DiagnosticCodes.R003MemberCycle))
            {
                diagnostics.Add(DiagnosticCodes.R003MemberCycle, $"member {memberId} is part of a cycle", PackageDigest.ManifestFile);
                entry.Status = "cycle";
            }
            else if (child.Manifest is not null && (child.Id != memberId || (reference.Version is not null && child.Version != reference.Version)))
            {
                // Package spec §7.1: the resolved member must be the package the pin names.
                diagnostics.Add(DiagnosticCodes.R002MemberDigestMismatch,
                    $"member pinned as {memberId}@{reference.Version} resolved to {child.Id}@{child.Version}", PackageDigest.ManifestFile);
                entry.Status = "mismatch";
            }
            else if (child.Digest != digest)
            {
                diagnostics.Add(DiagnosticCodes.R002MemberDigestMismatch,
                    $"member {memberId}@{reference.Version} has digest {child.Digest ?? "(none)"}, but {digest} is pinned", PackageDigest.ManifestFile);
                entry.Status = "mismatch";
            }
            else
            {
                entry.Status = "resolved";
            }
        }
    }

    private static PackageResult Finish(PackageResult result, DiagnosticBag diagnostics)
    {
        result.Diagnostics = diagnostics.Items;
        result.Valid = !diagnostics.Items.Any(d => d.Severity == DiagnosticSeverity.Error && DiagnosticCodes.IsInvalidating(d.Code));
        if (!result.Valid)
        {
            return result;
        }

        if (result.PendingCatalogue is { } catalogue)
        {
            result.Images = catalogue.Images;
            result.Translations = catalogue.Translations;
            result.Tags = catalogue.Tags;
        }

        var capabilities = new SortedSet<string>(StringComparer.Ordinal) { "core" };
        var allEvidenceLocal = true;
        var reviewedFiles = 0;
        var allApproved = true;
        foreach (var node in result.Nodes)
        {
            foreach (var rep in node.Representations)
            {
                if (rep.Moca["evidence"] is JsonArray { Count: > 0 })
                {
                    capabilities.Add("located-evidence");
                }

                if (rep.EvidenceChecks.Any(c => !c.Local))
                {
                    allEvidenceLocal = false;
                }

                if (rep.Locale is not null)
                {
                    capabilities.Add("localized");
                }

                // Attestations §7.1: an issued copy whose issuer changed a reviewed file is not reviewed.
                if (result.ReviewsByFile.TryGetValue(rep.File, out var allReviews) && allReviews.Any(r => r.FileChanged == true))
                {
                    allApproved = false;
                }

                if (result.ReviewsByFile.TryGetValue(rep.File, out var reviewsOfFile) && reviewsOfFile.Any(r => r.FileChanged != true))
                {
                    reviewedFiles++;
                    if (Reviews.StatusOf(reviewsOfFile, result.Signers.Count > 0) != "approved")
                    {
                        allApproved = false;
                    }
                }
            }
        }

        if (capabilities.Contains("located-evidence") && allEvidenceLocal)
        {
            capabilities.Add("self-contained-evidence");
        }

        if (result.Structure?.Ok == true)
        {
            capabilities.Add("structured");
        }

        if (result.Members.Count > 0)
        {
            capabilities.Add("composed");
        }

        if (result.Signers.Count > 0)
        {
            capabilities.Add("signed");
        }

        // Package spec §11 (moca-spec ADR-0017): signed, and every reviewed file approved.
        if (capabilities.Contains("signed") && reviewedFiles > 0 && allApproved)
        {
            capabilities.Add("reviewed");
        }

        result.Capabilities = [.. capabilities];
        return result;
    }

    /// <summary>Members resolved so far in one read, shared by every level (package spec §7.1).</summary>
    private sealed class MemberBudget
    {
        public int Resolved { get; set; }
    }

    /// <summary>Compares a matched <c>major.minor</c> with a version string, numerically and without overflow.</summary>
    private static int CompareVersion(Match declared, string version)
    {
        var parts = version.Split('.');
        var major = Number(declared.Groups[1].Value, parts[0]);
        return major != 0 ? major : Number(declared.Groups[2].Value, parts[1]);

        static int Number(string a, string b)
        {
            var x = a.TrimStart('0');
            var y = b.TrimStart('0');
            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : Math.Sign(string.CompareOrdinal(x, y));
        }
    }
}
