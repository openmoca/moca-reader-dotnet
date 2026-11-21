using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Sources;

namespace OpenMoca.Reader.Sidecars;

/// <summary>One chunk of a node from a sidecar's payload (sidecar spec).</summary>
public sealed record SidecarChunk
{
    /// <summary>The node path relative to <c>content/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The representation's locale, or null.</summary>
    public string? Locale { get; init; }

    /// <summary>Position of this chunk.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>How many chunks the node has.</summary>
    public int ChunkCount { get; init; }

    /// <summary>UTF-8 byte offset where the chunk starts in the node file.</summary>
    public long Start { get; init; }

    /// <summary>UTF-8 byte offset where it ends.</summary>
    public long End { get; init; }

    /// <summary>The chunk text, when the sidecar carries it.</summary>
    public string? Text { get; init; }

    /// <summary>Its vector, when the sidecar has vectors.</summary>
    public float[]? Vector { get; init; }
}

/// <summary>The result of binding a sidecar to a package (Reader interface §4 <c>bindSidecar</c>).</summary>
/// <param name="Usable">Every check passed; the chunks may be searched.</param>
/// <param name="Index">The sidecar's index.json, when it parsed.</param>
/// <param name="Chunks">The chunks, empty unless usable.</param>
/// <param name="Diagnostics">Sidecar diagnostics (S codes).</param>
public sealed record SidecarBinding(bool Usable, JsonObject? Index, IReadOnlyList<SidecarChunk> Chunks, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Binds sidecar indexes (sidecar spec). A sidecar that fails any check is ignored: the package stays
/// fully usable without it.
/// </summary>
public static class SidecarBinder
{
    /// <summary>The sidecar manifest file.</summary>
    public const string ManifestFile = "index.json";

    /// <summary>The portable payload format.</summary>
    public const string PortableFormat = "moca-jsonl-v1";

    /// <summary>
    /// A chunk's text: the bytes <c>start</c>..<c>end</c> of the node file, clipped to the body. A sidecar's
    /// <c>text</c>, when present, has already been checked to be exactly these bytes (S004); search never reads it
    /// (Reader contract §9.1).
    /// </summary>
    public static string ChunkText(SidecarChunk chunk, Content.Representation rep)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(rep);
        return SliceBody(rep, chunk.Start, chunk.End);
    }

    private static string SliceBody(Content.Representation rep, long start, long end)
    {
        var body = Encoding.UTF8.GetBytes(rep.Body);
        var from = Math.Max(start - rep.BodyOffset, 0);
        var to = Math.Min(end - rep.BodyOffset, body.Length);
        return to > from ? Encoding.UTF8.GetString(body, (int)from, (int)(to - from)) : "";
    }

    /// <summary>Checks a sidecar (directory or <c>.zip</c>) against a package and returns chunks a library can search.</summary>
    public static SidecarBinding Bind(string target, PackageResult package, bool strict = false)
    {
        var diagnostics = new DiagnosticBag(strict);
        IPackageSource source;
        try
        {
            source = PackageSources.Open(target);
            source.List();
        }
        catch (TargetException ex)
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, ex.Message);
            return new SidecarBinding(false, null, [], diagnostics.Items);
        }

        return Bind(source, package, diagnostics);
    }

    /// <summary>
    /// Binds an envelope's <c>index.age</c> to the result of reading that envelope (encrypted packages §6): opened through
    /// the same key provider (<c>file: "index"</c>) and steps 3–6, then bound as any sidecar. A failure makes the
    /// sidecar unusable and is reported here with its code; it never changes the package's validity.
    /// </summary>
    public static async Task<SidecarBinding> BindEnvelopeAsync(PackageTarget envelope, PackageResult package, ReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new DiagnosticBag(options.Strict);
        SidecarBinding Fail(string code, string message, string? file = null)
        {
            diagnostics.Add(code, message, file);
            return new SidecarBinding(false, null, [], diagnostics.Items);
        }

        if (package.Envelope is not { Opened: true })
        {
            return Fail(DiagnosticCodes.S001SidecarInvalid, "index.age binds only to an opened envelope");
        }

        IPackageSource source;
        try
        {
            source = envelope.Open(options.Limits);
            source.List();
        }
        catch (TargetException ex)
        {
            return Fail(DiagnosticCodes.S001SidecarInvalid, ex.Message);
        }

        if (source is not ArchiveSource archive || !archive.List().Any(e => e.Path == "index.age"))
        {
            return Fail(DiagnosticCodes.S001SidecarInvalid, "the envelope has no index.age");
        }

        if (!archive.Methods.TryGetValue("index.age", out var method) || method != 0)
        {
            return Fail(DiagnosticCodes.E005EnvelopeInvalid, "index.age must be stored, not compressed", "index.age");
        }

        var outline = package.Envelope.Outline;
        byte[] indexBytes;
        try
        {
            indexBytes = archive.Read("index.age");
        }
        catch (IOException ex)
        {
            return Fail(DiagnosticCodes.S001SidecarInvalid, $"index.age could not be read: {ex.Message}", "index.age");
        }

        var (plain, code, message) = await MocaReader.OpenAgeEntryAsync(indexBytes, "index", outline, options, cancellationToken).ConfigureAwait(false);
        if (plain is null)
        {
            return Fail(code!, message!, "index.age");
        }

        IPackageSource index;
        try
        {
            index = PackageSources.OpenArchive(plain, options.Limits);
            index.List();
        }
        catch (TargetException ex)
        {
            return Fail(DiagnosticCodes.S001SidecarInvalid, ex.Message, "index.age");
        }

        return Bind(index, package, diagnostics);
    }

    // A sidecar entry that cannot be read makes the sidecar unusable (S001); it never escapes as an exception.
    private static SidecarBinding Bind(IPackageSource source, PackageResult package, DiagnosticBag diagnostics)
    {
        try
        {
            return BindChecked(source, package, diagnostics);
        }
        catch (IOException ex)
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, $"a sidecar entry could not be read: {ex.Message}");
            return new SidecarBinding(false, null, [], diagnostics.Items);
        }
    }

    private static SidecarBinding BindChecked(IPackageSource source, PackageResult package, DiagnosticBag diagnostics)
    {
        var listed = source.List().Where(e => e.Kind == PackageEntryKind.File).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        SidecarBinding Fail(JsonObject? index = null) => new(false, index, [], diagnostics.Items);

        if (!listed.Contains(ManifestFile))
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, "the sidecar has no index.json");
            return Fail();
        }

        JsonObject? index;
        try
        {
            index = JsonValues.Parse(Encoding.UTF8.GetString(source.Read(ManifestFile))) as JsonObject;
        }
        catch (JsonException ex)
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, $"index.json is not valid JSON: {ex.Message}", ManifestFile);
            return Fail();
        }

        var errors = MocaSchemas.Validate(MocaSchemas.SidecarIndex, index);
        foreach (var e in errors)
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, e, ManifestFile);
        }

        if (errors.Count > 0 || index is null)
        {
            return Fail(index);
        }

        var target = (JsonObject)index["target"]!;
        var storage = (JsonObject)index["storage"]!;
        if (JsonValues.StringOf(target["id"]) != package.Id || JsonValues.StringOf(target["version"]) != package.Version)
        {
            diagnostics.Add(DiagnosticCodes.S002SidecarTargetMismatch,
                $"sidecar is bound to {target["id"]}@{target["version"]}, not {package.Id}@{package.Version}", ManifestFile);
            return Fail(index);
        }

        // Sidecar spec §5: an issued copy binds through the original digest it verified with.
        var targetDigest = JsonValues.StringOf(target["digest"]);
        var copy = package.IssuedCopy is { } issued && targetDigest == issued.OriginalDigest && targetDigest != package.Digest ? issued : null;
        if (targetDigest != package.Digest && copy is null)
        {
            diagnostics.Add(DiagnosticCodes.S003SidecarStale, $"sidecar was built for {target["digest"]}; the package is {package.Digest}", ManifestFile);
            return Fail(index);
        }

        if (JsonValues.StringOf(storage["format"]) != PortableFormat)
        {
            diagnostics.Add(DiagnosticCodes.S005SidecarFormatUnknown,
                $"payload format \"{storage["format"]}\" is not {PortableFormat}; the sidecar is ignored", ManifestFile);
            return Fail(index);
        }

        var payloadPath = PosixPath.Normalize(JsonValues.StringOf(storage["file"]) ?? "");
        if (payloadPath.StartsWith("..", StringComparison.Ordinal) || PosixPath.IsAbsolute(payloadPath) || !listed.Contains(payloadPath))
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, $"storage.file \"{storage["file"]}\" is missing or outside the sidecar", ManifestFile);
            return Fail(index);
        }

        var representations = new Dictionary<string, Representation>(StringComparer.Ordinal);
        foreach (var node in package.Nodes)
        {
            foreach (var rep in node.Representations)
            {
                representations[$"{node.Path}\u0000{rep.Locale ?? ""}"] = rep;
            }
        }

        // Sidecar spec §5: on an issued copy, the items of changed node files are replaced before they are checked.
        var changed = new HashSet<string>(copy?.ChangedFiles ?? [], StringComparer.Ordinal);
        var replaced = new HashSet<int>();
        var lines = Encoding.UTF8.GetString(source.Read(payloadPath)).Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var counts = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var chunks = new List<SidecarChunk>();
        var bad = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            JsonObject? item;
            try
            {
                item = JsonValues.Parse(lines[i]) as JsonObject;
            }
            catch (JsonException)
            {
                diagnostics.Add(DiagnosticCodes.S004SidecarItemInvalid, "payload line is not JSON", payloadPath, i + 1);
                bad++;
                continue;
            }

            if (changed.Count > 0 && item is not null && JsonValues.StringOf(item["path"]) is { } itemPath
                && representations.TryGetValue($"{itemPath}\u0000{LocaleKey(item["locale"])}", out var target2) && changed.Contains(target2.File))
            {
                // A placeholder keeps the vector rows in step with the items.
                replaced.Add(chunks.Count);
                chunks.Add(new SidecarChunk { Path = itemPath });
                continue;
            }

            var (chunk, problem) = CheckItem(item, representations, index);
            if (problem is not null)
            {
                diagnostics.Add(DiagnosticCodes.S004SidecarItemInvalid, problem, payloadPath, i + 1);
                bad++;
                continue;
            }

            var key = $"{chunk!.Path}\u0000{chunk.Locale ?? ""}";
            if (!counts.TryGetValue(key, out var seen))
            {
                seen = [];
                counts[key] = seen;
            }

            seen.Add(chunk.ChunkIndex);
            chunks.Add(chunk);
        }

        foreach (var chunk in chunks.Where((_, i) => !replaced.Contains(i)))
        {
            var seen = counts[$"{chunk.Path}\u0000{chunk.Locale ?? ""}"];
            if (seen.Count != chunk.ChunkCount)
            {
                diagnostics.Add(DiagnosticCodes.S004SidecarItemInvalid, $"{chunk.Path}: chunkCount is {chunk.ChunkCount} but {seen.Count} chunk(s) are present", payloadPath);
                bad++;
                break;
            }
        }

        if (bad == 0 && storage["vectors"] is JsonObject vectors)
        {
            bad += AttachVectors(source, listed, index, vectors, chunks, lines.Count, diagnostics);
        }

        chunks = [.. chunks.Where((_, i) => !replaced.Contains(i))];
        if (copy is not null && bad == 0)
        {
            var files = 0;
            foreach (var node in package.Nodes)
            {
                foreach (var rep in node.Representations)
                {
                    if (!changed.Contains(rep.File))
                    {
                        continue;
                    }

                    files++;
                    chunks.Add(new SidecarChunk
                    {
                        Path = node.Path,
                        Locale = rep.Locale,
                        ChunkIndex = 0,
                        ChunkCount = 1,
                        Start = rep.BodyOffset,
                        End = rep.BodyOffset + Encoding.UTF8.GetByteCount(rep.Body),
                    });
                }
            }

            if (files > 0)
            {
                diagnostics.Add(DiagnosticCodes.S009SidecarItemsReplaced,
                    $"bound to an issued copy of {copy.OriginalDigest}; the items of {files} file(s) the issuer changed were replaced by each file's whole body, without a vector", ManifestFile);
            }
        }

        var usable = bad == 0;
        return new SidecarBinding(usable, index, usable ? chunks : [], diagnostics.Items);
    }

    /// <summary>An item's locale as the representation key spells it: JavaScript's <c>item.locale ?? ''</c> in a template string.</summary>
    private static string LocaleKey(JsonNode? locale) => locale switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => locale.ToJsonString(),
    };

    private static (SidecarChunk? Chunk, string? Problem) CheckItem(JsonObject? item, Dictionary<string, Representation> representations, JsonObject index)
    {
        if (item is null)
        {
            return (null, "item is not an object");
        }

        var path = JsonValues.StringOf(item["path"]);
        if (path is null || path.StartsWith('/') || path.Split('/').Contains(".."))
        {
            return (null, "path must be relative to content/ without \"..\" segments");
        }

        var locale = JsonValues.StringOf(item["locale"]);
        if (!representations.TryGetValue($"{path}\u0000{locale ?? ""}", out var rep))
        {
            return (null, $"path \"{path}\"{(locale is not null ? $" ({locale})" : "")} is not a content node of the package");
        }

        var chunkCount = JsonValues.IntegerOf(item["chunkCount"]);
        if (chunkCount is null || chunkCount < 1)
        {
            return (null, "chunkCount must be a positive integer");
        }

        var chunkIndex = JsonValues.IntegerOf(item["chunkIndex"]);
        if (chunkIndex is null || chunkIndex < 0 || chunkIndex >= chunkCount)
        {
            return (null, "0 <= chunkIndex < chunkCount must hold");
        }

        var start = JsonValues.IntegerOf(item["start"]);
        var end = JsonValues.IntegerOf(item["end"]);
        if (start is null || end is null || start < 0 || end < start)
        {
            return (null, "start and end must be byte offsets with 0 <= start <= end");
        }

        var fileEnd = rep.BodyOffset + Encoding.UTF8.GetByteCount(rep.Body);
        if (end > fileEnd)
        {
            return (null, $"end {end} is beyond the end of the file ({fileEnd})");
        }

        // Sidecar §5: a chunk lies inside the node's body, and any text it carries is exactly the package's bytes there.
        if (start < rep.BodyOffset)
        {
            return (null, $"start {start} is inside the frontmatter (the body starts at {rep.BodyOffset})");
        }

        if (item.ContainsKey("text") && JsonValues.StringOf(item["text"]) != SliceBody(rep, start.Value, end.Value))
        {
            return (null, "text is not the package's text at start..end");
        }

        float[]? vector = null;
        if (item["vector"] is { } vectorNode)
        {
            if (vectorNode is not JsonArray array || array.Any(v => v is not JsonValue jv || !jv.TryGetValue<double>(out _)))
            {
                return (null, "vector must be an array of numbers");
            }

            vector = array.Select(v => (float)v!.GetValue<double>()).ToArray();
            var declared = JsonValues.IntegerOf((index["model"] as JsonObject)?["dimensions"]);
            if (declared is not null && vector.Length != declared)
            {
                return (null, $"vector has {vector.Length} dimensions, model declares {declared}");
            }
        }

        return (new SidecarChunk
        {
            Path = path,
            Locale = locale,
            ChunkIndex = (int)chunkIndex,
            ChunkCount = (int)chunkCount,
            Start = start.Value,
            End = end.Value,
            Text = JsonValues.StringOf(item["text"]),
            Vector = vector,
        }, null);
    }

    /// <summary>Reads <c>storage.vectors</c> (little-endian float32, one row per item) onto the chunks. Returns 1 on a problem.</summary>
    private static int AttachVectors(IPackageSource source, HashSet<string> listed, JsonObject index, JsonObject vectors, List<SidecarChunk> chunks, int count, DiagnosticBag diagnostics)
    {
        var file = JsonValues.StringOf(vectors["file"]) ?? "";
        var dimensions = (int)(JsonValues.IntegerOf(vectors["dimensions"]) ?? 0);
        var path = PosixPath.Normalize(file);
        if (path.StartsWith("..", StringComparison.Ordinal) || PosixPath.IsAbsolute(path) || !listed.Contains(path))
        {
            diagnostics.Add(DiagnosticCodes.S001SidecarInvalid, $"storage.vectors.file \"{file}\" is missing or outside the sidecar", ManifestFile);
            return 1;
        }

        var declared = JsonValues.IntegerOf((index["model"] as JsonObject)?["dimensions"]);
        if (declared is not null and not 0 && declared != dimensions)
        {
            diagnostics.Add(DiagnosticCodes.S004SidecarItemInvalid, $"storage.vectors has {dimensions} dimensions, model declares {declared}", ManifestFile);
            return 1;
        }

        var bytes = source.Read(path);
        if (bytes.Length != (long)count * dimensions * 4)
        {
            diagnostics.Add(DiagnosticCodes.S004SidecarItemInvalid,
                $"{path} has {bytes.Length} bytes; {count} items x {dimensions} float32 needs {count * dimensions * 4}", path);
            return 1;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var row = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
            {
                row[d] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(((i * dimensions) + d) * 4, 4));
            }

            chunks[i] = chunks[i] with { Vector = row };
        }

        return 0;
    }
}
