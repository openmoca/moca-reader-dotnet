using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Encryption;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Sources;

namespace OpenMoca.Reader;

/// <summary>What the inner archive of an envelope is checked against, right after its manifest is read (step 8).</summary>
internal sealed record EnvelopeCheck(JsonObject? Outline, byte[]? Structure, EnvelopeCover? Cover);

/// <summary>An envelope's plain cover copy (encrypted packages §3): its entry name and bytes.</summary>
internal sealed record EnvelopeCover(string Name, byte[] Bytes);

/// <summary>Encrypted packages (moca-spec spec/moca-encrypted-package.md): recognising and opening envelopes.</summary>
public static partial class MocaReader
{
    private const string Marker = "moca-enc.json";
    private const string PackageAge = "package.age";
    private const string IndexAge = "index.age";
    private const string StructureCopy = "outline/structure.ttl";
    private const int Stored = 0;
    private static readonly HashSet<string> EnvelopeEntries = new(StringComparer.Ordinal) { Marker, PackageAge, StructureCopy, IndexAge };

    /// <summary>Encrypted packages §3: the opt-in cover copy, by name, and the type its extension gives.</summary>
    private static readonly Dictionary<string, string> CoverCopies = new(StringComparer.Ordinal)
    {
        ["outline/cover.png"] = "image/png",
        ["outline/cover.jpg"] = "image/jpeg",
        ["outline/cover.webp"] = "image/webp",
    };

    /// <summary>An archive with moca-enc.json at its root is an envelope (encrypted packages §2).</summary>
    private static bool IsEnvelope(IPackageSource source, IReadOnlyDictionary<string, string> files) =>
        source is ArchiveSource && files.ContainsKey(Marker);

    private static async Task<PackageResult> ReadEnvelopeAsync(
        ArchiveSource source, IReadOnlyDictionary<string, string> files, bool entriesOk, DiagnosticBag diagnostics, PackageResult result,
        ReadOptions options, Func<byte[], EnvelopeCheck, Task<PackageResult>> readInner, CancellationToken cancellationToken)
    {
        // Step 1: the outer archive's rules (T003, P001–P003) were applied when it was opened and listed.
        if (!entriesOk)
        {
            return Finish(result, diagnostics);
        }

        result.Envelope = new EnvelopeInfo(null, false);
        PackageResult Fail(string code, string message, string? file = null)
        {
            diagnostics.Add(code, message, file);
            return Finish(result, diagnostics);
        }

        // Step 2: the entry set and the marker.
        if (files.ContainsKey(PackageDigest.ManifestFile))
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, "a Zip with both moca.json and moca-enc.json at its root is neither a package nor an envelope", Marker);
        }

        if (source.HiddenEntries.Count > 0)
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"an envelope may not have a hidden entry: \"{source.HiddenEntries[0]}\"");
        }

        foreach (var path in files.Keys)
        {
            if (!EnvelopeEntries.Contains(path) && !CoverCopies.ContainsKey(path))
            {
                return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"an envelope may not have the entry \"{path}\"", path);
            }
        }

        if (!files.ContainsKey(PackageAge))
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, "the envelope has no package.age");
        }

        var covers = CoverCopies.Keys.Where(files.ContainsKey).ToList();
        if (covers.Count > 1)
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"an envelope has at most one cover copy, and this one has {string.Join(", ", covers)}", covers[1]);
        }

        foreach (var name in new[] { PackageAge, IndexAge })
        {
            if (files.TryGetValue(name, out var stored) && source.Methods.TryGetValue(stored, out var method) && method != Stored)
            {
                return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"{name} must be stored, not compressed", name);
            }
        }

        var (marker, error) = ParseMarker(source.Read(files[Marker]));
        if (marker is null)
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"moca-enc.json is invalid: {error}", Marker);
        }

        var format = JsonValues.StringOf(marker["format"]);
        var outline = marker["outline"] as JsonObject;
        result.Envelope = new EnvelopeInfo(format, false, outline?.DeepClone().AsObject());

        // Steps 3–6.
        var structure = files.TryGetValue(StructureCopy, out var copy) ? source.Read(copy) : null;
        var cover = covers.Count == 1 ? new EnvelopeCover(covers[0], source.Read(files[covers[0]])) : null;
        if (cover is not null && cover.Bytes.Length > options.Limits.MaxImageBytes)
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, $"{cover.Name} is {cover.Bytes.Length} bytes, over the limit of {options.Limits.MaxImageBytes}", cover.Name);
        }

        var (plain, code, message) = await OpenAgeEntryAsync(source.Read(files[PackageAge]), "package", outline, options, cancellationToken).ConfigureAwait(false);
        if (plain is null)
        {
            return Fail(code!, message!, PackageAge);
        }

        // Steps 7–10, with the outline check (step 8) right after the inner manifest is read.
        var inner = await readInner(plain, new EnvelopeCheck(outline, structure, cover)).ConfigureAwait(false);
        var failed = inner.Manifest is null || inner.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error && (d.Code[0] is 'T' or 'P' || d.Code == DiagnosticCodes.E004OutlineMismatch));
        if (failed)
        {
            // Encrypted packages §5.3: nothing from the inner archive is exposed; its diagnostics are reported.
            foreach (var d in inner.Diagnostics)
            {
                diagnostics.AddExisting(d);
            }

            return Finish(result, diagnostics);
        }

        inner.Envelope = new EnvelopeInfo(format, true);
        return inner;
    }

    /// <summary>Parses moca-enc.json as moca.json is parsed: strict UTF-8, no repeated member names; then its schema.</summary>
    private static (JsonObject? Marker, string? Error) ParseMarker(byte[] bytes)
    {
        try
        {
            var text = StrictUtf8.GetString(bytes).TrimStart('﻿');
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
            var errors = MocaSchemas.Validate(MocaSchemas.MocaEnc, node);
            return errors.Count > 0 ? (null, string.Join("; ", errors)) : (node as JsonObject, null);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            return (null, $"not valid UTF-8 JSON: {ex.Message}");
        }
    }

    /// <summary>The outline check (encrypted packages §3); null when it passes.</summary>
    private static string? OutlineMismatch(EnvelopeCheck check, JsonObject manifest, IReadOnlyDictionary<string, byte[]> bytes, ReaderLimits limits)
    {
        foreach (var (key, value) in check.Outline ?? [])
        {
            if (!manifest.TryGetPropertyValue(key, out var actual))
            {
                return $"the outline has \"{key}\", and the manifest does not";
            }

            if (!JsonNode.DeepEquals(value, actual))
            {
                return $"the outline's \"{key}\" differs from the manifest's";
            }
        }

        if (check.Structure is not null)
        {
            if (!bytes.TryGetValue("structure.ttl", out var inner))
            {
                return "the envelope has a structure copy, and the package has no structure.ttl";
            }

            if (!inner.AsSpan().SequenceEqual(check.Structure))
            {
                return "the structure copy differs from structure.ttl";
            }
        }

        if (check.Cover is { } copyOfCover)
        {
            // The first images entry with role cover, which must pass its checks (package spec §4.6).
            var first = (manifest["images"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(i => JsonValues.StringOf(i["role"]) == "cover");
            if (first is null || JsonValues.StringOf(first["src"]) is not { } src || JsonValues.StringOf(first["type"]) is not { } type)
            {
                return "the envelope has a cover copy, and the manifest has no cover image";
            }

            if (CatalogueChecks.ImageProblem(src, type, bytes, limits.MaxImageBytes) is { } problem)
            {
                return $"the envelope has a cover copy, and the manifest's cover fails its checks: {problem}";
            }

            if (CoverCopies[copyOfCover.Name] != type)
            {
                return $"the cover copy {copyOfCover.Name} does not match the cover's type {type}";
            }

            if (!bytes[src.Normalize(NormalizationForm.FormC)].AsSpan().SequenceEqual(copyOfCover.Bytes))
            {
                return $"the cover copy differs from {src}";
            }
        }

        return null;
    }

    /// <summary>Steps 3–6 of encrypted packages §5.2 for one encrypted entry. Never throws for a bad envelope.</summary>
    internal static async Task<(byte[]? Plain, string? Code, string? Message)> OpenAgeEntryAsync(
        byte[] bytes, string file, JsonObject? outline, ReadOptions options, CancellationToken cancellationToken)
    {
        var decryptor = options.EnvelopeDecryptor;
        if (decryptor is null)
        {
            return (null, DiagnosticCodes.E001EncryptedNoKey, "this Reader was not given an envelope decryptor, so it does not open envelopes");
        }

        IEnvelopeHeader header;
        try
        {
            header = decryptor.ParseHeader(bytes, options.Limits.MaxScryptWorkFactor);
        }
        catch (EnvelopeDecryptionException ex)
        {
            return (null, CodeOf(ex.Failure), $"{file}.age: {ex.Message}");
        }

        if (options.KeyProvider is null)
        {
            return (null, DiagnosticCodes.E001EncryptedNoKey, "the host supplied no key provider");
        }

        EnvelopeKey? key;
        try
        {
            // Encrypted packages §5.1: stanzas are data; the outline is unverified.
            var request = new EnvelopeKeyRequest(
                header.Stanzas.Select(s => new EnvelopeStanza(s.Type, [.. s.Args], [.. s.Body])).ToList(),
                outline?.DeepClone().AsObject(),
                file);
            key = await options.KeyProvider(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, DiagnosticCodes.E001EncryptedNoKey, $"the key provider failed: {ex.Message}");
        }

        if (key is null)
        {
            return (null, DiagnosticCodes.E001EncryptedNoKey, "the key provider returned no key");
        }

        try
        {
            return (decryptor.Decrypt(bytes, header, key, options.Limits.MaxBytes), null, null);
        }
        catch (EnvelopeDecryptionException ex)
        {
            return (null, CodeOf(ex.Failure), $"{file}.age: {ex.Message}");
        }
    }

    private static string CodeOf(EnvelopeFailure failure) => failure switch
    {
        EnvelopeFailure.Header => DiagnosticCodes.E006AgeHeaderInvalid,
        EnvelopeFailure.NoKey => DiagnosticCodes.E001EncryptedNoKey,
        EnvelopeFailure.NoMatch or EnvelopeFailure.Mac => DiagnosticCodes.E002DecryptionFailed,
        EnvelopeFailure.Payload => DiagnosticCodes.E003PayloadCorrupt,
        _ => DiagnosticCodes.T003ArchiveRejected,
    };
}
