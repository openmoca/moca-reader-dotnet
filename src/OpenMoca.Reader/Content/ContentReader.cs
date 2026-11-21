using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Content;

/// <summary>
/// Reads <c>content/</c> as an Open Knowledge Format v0.2 bundle (package spec §5, Reader contract §5):
/// groups locale representations into nodes and checks frontmatter, reserved files, paths, links and evidence.
/// </summary>
internal static partial class ContentReader
{
    public const string ContentDirectory = "content";

    private static readonly HashSet<string> Reserved = ["index.md", "log.md"];
    private static readonly string[] EvidenceDirectories = ["sources/", "media/"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex Scheme();

    // A Windows drive prefix (C:\x, C:/x) is a path, not a URL scheme.
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex Drive();

    // ECMAScript white space, as \s in the reference Reader.
    private static bool IsSpace(char c) => Array.IndexOf(UnicodeText.JsWhitespace, c) >= 0 || c is '\u2028' or '\u2029';

    /// <summary>
    /// The target of every Markdown link and image in a line, as written (a <c>&lt;...&gt;</c> target keeps its
    /// brackets): what the first group of
    /// <c>!?\[[^\]]*\]\(\s*(&lt;[^&gt;]+&gt;|[^)\s]+)(?:\s+(?:"[^"]*"|'[^']*'))?\s*\)</c> captures, in one pass over the
    /// line. Contract \u00a73 bounds a Reader's work by the size of what it reads, and that expression rescans the line from
    /// every <c>[</c>.
    /// </summary>
    internal static List<string> LinkTargets(string line)
    {
        var targets = new List<string>();
        var open = new EvidenceText.ForwardFinder(line, '[');
        var close = new EvidenceText.ForwardFinder(line, ']');
        var gt = new EvidenceText.ForwardFinder(line, '>');
        char At(int p) => p < line.Length ? line[p] : '\0';

        // The end of a plain target: the next ")" or white space, or the end of the line.
        var stopAt = -2;
        int TargetEnd(int from)
        {
            if (stopAt != -1 && stopAt < from)
            {
                stopAt = -1;
                for (var k = from; k < line.Length; k++)
                {
                    if (line[k] == ')' || IsSpace(line[k]))
                    {
                        stopAt = k;
                        break;
                    }
                }
            }

            return stopAt == -1 ? line.Length : stopAt;
        }

        var spaces = new Dictionary<int, int>();
        int SkipSpace(int p)
        {
            if (!spaces.TryGetValue(p, out var e))
            {
                e = p;
                while (e < line.Length && IsSpace(line[e]))
                {
                    e++;
                }

                spaces[p] = e;
            }

            return e;
        }

        // What follows a target: an optional title in quotes, then ")". The index after the ")", or -1.
        Func<int, int> Tail()
        {
            var quotes = new Dictionary<char, EvidenceText.ForwardFinder> { ['"'] = new(line, '"'), ['\''] = new(line, '\'') };
            var known = new Dictionary<int, int>();
            return p =>
            {
                if (known.TryGetValue(p, out var cached))
                {
                    return cached;
                }

                var w = SkipSpace(p);
                var end = -1;
                if (w > p && At(w) is '"' or '\'')
                {
                    var q = quotes[At(w)].From(w + 1);
                    if (q >= 0 && At(SkipSpace(q + 1)) == ')')
                    {
                        end = SkipSpace(q + 1) + 1;
                    }
                }

                if (end < 0 && At(w) == ')')
                {
                    end = w + 1;
                }

                known[p] = end;
                return end;
            };
        }

        // Each kind of target asks from rising positions of its own, so each has its own searches.
        var afterAngle = Tail();
        var afterPlain = Tail();
        var pos = 0;
        while (true)
        {
            var i = open.From(pos);
            if (i < 0)
            {
                break;
            }

            var j = close.From(i + 1);
            if (j < 0)
            {
                break;
            }

            var end = -1;
            string? target = null;
            if (At(j + 1) == '(')
            {
                var p = SkipSpace(j + 2);
                if (At(p) == '<')
                {
                    var q = gt.From(p + 1);
                    if (q > p + 1)
                    {
                        end = afterAngle(q + 1);
                        target = line[p..(q + 1)];
                    }
                }

                if (end < 0)
                {
                    var e = TargetEnd(p);
                    if (e > p)
                    {
                        end = afterPlain(e);
                        target = line[p..e];
                    }
                }
            }

            // Every "[" up to j closes at j, so none of them starts a link either.
            if (end < 0)
            {
                pos = j + 1;
                continue;
            }

            targets.Add(target!);
            pos = end;
        }

        return targets;
    }

    [GeneratedRegex(@"^(\.{1,2}/|/)")]
    private static partial Regex PathReference();

    [GeneratedRegex(@"^(.*)\.([A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})*)\.[Mm][Dd]\z")]
    private static partial Regex LocaleSuffix();

    [GeneratedRegex(@"^##[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+(.*)\z")]
    private static partial Regex LogHeading();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*(```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    public static List<ContentNode> Read(
        JsonObject manifest,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, byte[]> bytes,
        IReadOnlyDictionary<string, string> fileDigests,
        DiagnosticBag diagnostics,
        ReaderLimits limits,
        EvidenceSpend? spend = null)
    {
        var sourceText = new Dictionary<string, string>(StringComparer.Ordinal);
        var budget = new EvidenceBudget(spend ?? new EvidenceSpend());
        var locales = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifest["locales"] is JsonArray declared)
        {
            foreach (var locale in declared.Select(JsonValues.StringOf).OfType<string>())
            {
                locales.TryAdd(locale.ToLowerInvariant(), locale);
            }
        }

        var byKey = new Dictionary<string, ContentNode>(StringComparer.Ordinal);
        var markdownFiles = files.Keys
            .Where(p => p.StartsWith(ContentDirectory + "/", StringComparison.Ordinal) && UnicodeText.AsciiLower(p).EndsWith(".md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        foreach (var file in markdownFiles)
        {
            var relative = file[(ContentDirectory.Length + 1)..];
            var baseName = PosixPath.BaseName(relative);
            var text = EvidenceText.Utf8(bytes[file]);

            if (Reserved.Contains(UnicodeText.AsciiLower(baseName)))
            {
                CheckReserved(file, relative, UnicodeText.AsciiLower(baseName), text, diagnostics, limits.MaxFrontmatterBytes);
                continue;
            }

            var (key, nodeLocale) = LocaleOf(relative, locales);
            var split = Frontmatter.Split(text, limits.MaxFrontmatterBytes);
            if (!split.Present)
            {
                diagnostics.Add(DiagnosticCodes.C001FrontmatterMissing, "concept documents must open with a YAML frontmatter block", file);
                continue;
            }

            if (split.Error is not null || split.Data is null)
            {
                diagnostics.Add(DiagnosticCodes.C002FrontmatterInvalid, split.Error ?? "frontmatter is invalid", file, split.Line);
                continue;
            }

            var frontmatter = split.Data;

            // Package spec §5.1: evidence and media name a source by its id, so two sources with one id leave none chosen.
            if (RepeatedSourceId(frontmatter) is { } repeated)
            {
                diagnostics.Add(DiagnosticCodes.C002FrontmatterInvalid, $"sources has two entries with the id \"{repeated}\"; none is chosen", file);
                continue;
            }

            var type = JsonValues.StringOf(frontmatter["type"]);
            if (type is null || UnicodeText.JsTrim(type).Length == 0)
            {
                diagnostics.Add(DiagnosticCodes.C003TypeMissing, "frontmatter needs a non-empty \"type\"", file);
                continue;
            }

            var fieldErrors = MocaSchemas.Validate(MocaSchemas.Node, frontmatter);
            foreach (var error in fieldErrors)
            {
                diagnostics.Add(DiagnosticCodes.C005FieldInvalid, error, file);
            }

            // Package spec §4.13: the node's own credits, when they match the node schema; images checked as in §4.6.
            var contributors = (frontmatter["moca"] as JsonObject)?["contributors"] is JsonArray credits
                && !fieldErrors.Any(e => e.StartsWith("/moca/contributors", StringComparison.Ordinal) || e.StartsWith("/moca ", StringComparison.Ordinal))
                ? CatalogueChecks.Contributors(credits, "moca.contributors", bytes, limits.MaxImageBytes, diagnostics, file)
                : null;

            CheckEvidenceSources(file, frontmatter, diagnostics);
            CheckWindow(file, frontmatter, diagnostics);
            CheckPaths(file, frontmatter, split.Body, files, diagnostics);
            var media = MediaDescriptions(file, frontmatter, files, diagnostics);
            var evidenceChecks = VerifyEvidence(file, frontmatter, files, bytes, diagnostics, limits, sourceText, budget, media);

            var representation = new Representation
            {
                Locale = nodeLocale,
                File = file,
                Frontmatter = frontmatter,
                Body = split.Body,
                BodyOffset = split.BodyOffset,
                Sha256 = fileDigests.GetValueOrDefault(file),
                EvidenceChecks = evidenceChecks,
                Media = media,
                Contributors = contributors,
            };
            if (!byKey.TryGetValue(key, out var node))
            {
                node = new ContentNode { Path = key };
                byKey[key] = node;
            }

            node.Representations.Add(representation);
        }

        var nodes = new List<ContentNode>();
        foreach (var node in byKey.Values.OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            node.Representations.Sort((a, b) =>
                a.Locale is null ? -1 : b.Locale is null ? 1 : string.CompareOrdinal(a.Locale, b.Locale));
            if (node.Representations[0].Locale is not null)
            {
                diagnostics.Add(DiagnosticCodes.C009LocaleOrphan,
                    $"locale variant has no default-language file {ContentDirectory}/{node.Path}", node.Representations[0].File);
            }

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>Package spec §5.4: <c>name.tag.md</c> is a representation only when <c>tag</c> is a declared locale.</summary>
    private static (string Key, string? Locale) LocaleOf(string relative, Dictionary<string, string> locales)
    {
        var m = LocaleSuffix().Match(relative);
        if (m.Success && locales.TryGetValue(m.Groups[2].Value.ToLowerInvariant(), out var locale))
        {
            return ($"{m.Groups[1].Value}.md", locale);
        }

        return (relative, null);
    }

    private static void CheckReserved(string file, string relative, string baseName, string text, DiagnosticBag diagnostics, int maxFrontmatterBytes)
    {
        // Contract §3: the frontmatter size limit applies to reserved files too.
        var split = Frontmatter.Split(text, maxFrontmatterBytes);
        if (baseName == "index.md")
        {
            if (!split.Present)
            {
                return;
            }

            var keys = split.Data?.Select(kv => kv.Key).ToList() ?? [];
            var rootIndex = UnicodeText.AsciiLower(relative) == "index.md";
            if (split.Error is not null || !rootIndex || keys.Any(k => k != "okf_version"))
            {
                diagnostics.Add(DiagnosticCodes.C004ReservedFileInvalid, rootIndex
                    ? "the bundle-root index.md may carry only an okf_version frontmatter key"
                    : "index.md files carry no frontmatter", file);
            }

            return;
        }

        if (split.Present)
        {
            diagnostics.Add(DiagnosticCodes.C004ReservedFileInvalid, "log.md carries no frontmatter", file);
        }

        // Headings inside fenced code blocks are not headings.
        var lines = LineBreak().Split(text);
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence().IsMatch(lines[i]))
            {
                fence = !fence;
            }

            if (fence)
            {
                continue;
            }

            var heading = LogHeading().Match(lines[i]);
            if (heading.Success && !IsoDate().IsMatch(heading.Groups[1].Value))
            {
                diagnostics.Add(DiagnosticCodes.C004ReservedFileInvalid,
                    $"log.md date headings must start with an ISO 8601 date: \"{heading.Groups[1].Value}\"", file, i + 1);
            }
        }
    }

    private static Dictionary<string, JsonObject> SourcesById(JsonObject frontmatter)
    {
        var sources = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (frontmatter["sources"] is JsonArray array)
        {
            foreach (var source in array.OfType<JsonObject>())
            {
                if (JsonValues.StringOf(source["id"]) is { Length: > 0 } id)
                {
                    sources.TryAdd(id, source);
                }
            }
        }

        return sources;
    }

    /// <summary>The first source id that two entries of the frontmatter's <c>sources</c> share, or null.</summary>
    private static string? RepeatedSourceId(JsonObject frontmatter)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (frontmatter["sources"] is not JsonArray sources)
        {
            return null;
        }

        foreach (var entry in sources)
        {
            if (entry is not JsonObject source || JsonValues.StringOf(source["id"]) is not { } id)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                return id;
            }
        }

        return null;
    }

    private static void CheckEvidenceSources(string file, JsonObject frontmatter, DiagnosticBag diagnostics)
    {
        if ((frontmatter["moca"] as JsonObject)?["evidence"] is not JsonArray evidence)
        {
            return;
        }

        var ids = SourcesById(frontmatter);
        foreach (var entry in evidence.OfType<JsonObject>())
        {
            if (JsonValues.StringOf(entry["source"]) is { } source && !ids.ContainsKey(source))
            {
                diagnostics.Add(DiagnosticCodes.C006EvidenceSourceUnknown,
                    $"evidence names source \"{source}\", which sources[] does not declare with that id", file);
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}(?:[ \t]*;[^\n\r]*)?\z")]
    private static partial Regex MediaType();

    [GeneratedRegex(@"^sha256:[a-f0-9]{64}\z")]
    private static partial Regex Sha256Digest();

    private static readonly HashSet<string> MediaKeys = new(["source", "type", "duration", "digest", "transcript", "alternateOf", "sync"], StringComparer.Ordinal);

    /// <summary>A finite JSON number, or null.</summary>
    private static double? NumberOf(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            return d;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (v.TryGetValue<decimal>(out var m))
        {
            return (double)m;
        }

        return null;
    }

    private static bool WellFormedMedia(JsonNode? node)
    {
        if (node is not JsonObject m || m.Any(p => !MediaKeys.Contains(p.Key)))
        {
            return false;
        }

        if (JsonValues.StringOf(m["source"]) is not { Length: > 0 } || JsonValues.StringOf(m["type"]) is not { } type || !MediaType().IsMatch(type))
        {
            return false;
        }

        if (m.ContainsKey("duration") && NumberOf(m["duration"]) is not >= 0)
        {
            return false;
        }

        if (m.ContainsKey("digest") && (JsonValues.StringOf(m["digest"]) is not { } digest || !Sha256Digest().IsMatch(digest)))
        {
            return false;
        }

        if (m.ContainsKey("transcript") && JsonValues.StringOf(m["transcript"]) is not { Length: > 0 })
        {
            return false;
        }

        if (m.ContainsKey("alternateOf") && JsonValues.StringOf(m["alternateOf"]) is not { Length: > 0 })
        {
            return false;
        }

        return !m.ContainsKey("sync") || (m["sync"] is JsonArray { Count: > 0 } sync && sync.All(WellFormedSyncEntry));
    }

    private static bool WellFormedSyncEntry(JsonNode? node)
    {
        if (node is not JsonObject e || e.Any(p => p.Key is not ("at" or "offset")) || !e.ContainsKey("offset"))
        {
            return false;
        }

        if (NumberOf(e["at"]) is not >= 0)
        {
            return false;
        }

        return e["offset"] is null || NumberOf(e["offset"]) is not null;
    }

    private static List<SyncEntry> SyncOf(JsonNode? node) =>
        node is JsonArray list ? [.. list.OfType<JsonObject>().Select(e => new SyncEntry(NumberOf(e["at"])!.Value, NumberOf(e["offset"])))] : [.. Alternates.DefaultSync];

    /// <summary>The package path of a resource when it is a file under sources/ or media/, or null.</summary>
    private static string? LocalEvidencePath(string file, string? resource, IReadOnlyDictionary<string, string> files)
    {
        if (resource is null || !PathReference().IsMatch(resource))
        {
            return null;
        }

        var (path, escapes) = ResolveReference(file, resource);
        if (escapes || path is null || !files.ContainsKey(path) || !EvidenceDirectories.Any(d => path.StartsWith(d, StringComparison.Ordinal)))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Package spec §5.7: the node's valid media descriptions, in order. A malformed description (already reported by
    /// the node schema as C005) is ignored; so is one naming an unknown source (C006), or a source already described
    /// (C005). A transcript that is not a WebVTT file under sources/ or media/ is dropped (C005).
    /// </summary>
    private static List<MediaDescription> MediaDescriptions(string file, JsonObject frontmatter, IReadOnlyDictionary<string, string> files, DiagnosticBag diagnostics)
    {
        var output = new List<MediaDescription>();
        if ((frontmatter["moca"] as JsonObject)?["media"] is not JsonArray list)
        {
            return output;
        }

        var sources = SourcesById(frontmatter);
        var described = new HashSet<string>(StringComparer.Ordinal);
        var raw = new List<JsonObject>();
        foreach (var node in list)
        {
            if (!WellFormedMedia(node))
            {
                continue;
            }

            var m = (JsonObject)node!;
            var sourceId = JsonValues.StringOf(m["source"])!;
            if (!sources.TryGetValue(sourceId, out var source))
            {
                diagnostics.Add(DiagnosticCodes.C006EvidenceSourceUnknown,
                    $"media names source \"{sourceId}\", which sources[] does not declare with that id", file);
                continue;
            }

            if (JsonValues.StringOf(source["resource"]) is not { } resource)
            {
                diagnostics.Add(DiagnosticCodes.C005FieldInvalid, $"media describes source \"{sourceId}\", which has no resource", file);
                continue;
            }

            if (!described.Add(sourceId))
            {
                diagnostics.Add(DiagnosticCodes.C005FieldInvalid, $"media describes source \"{sourceId}\" twice; the later description is ignored", file);
                continue;
            }

            string? transcript = null;
            string? transcriptPath = null;
            if (JsonValues.StringOf(m["transcript"]) is { } transcriptId)
            {
                var path = sources.TryGetValue(transcriptId, out var t) ? LocalEvidencePath(file, JsonValues.StringOf(t["resource"]), files) : null;
                if (path is not null && EvidenceText.KindOf(path) == SourceKind.WebVtt)
                {
                    transcript = transcriptId;
                    transcriptPath = path;
                }
                else
                {
                    diagnostics.Add(DiagnosticCodes.C005FieldInvalid,
                        $"media transcript \"{transcriptId}\" is not a source with a WebVTT file under sources/ or media/; it is dropped", file);
                }
            }

            output.Add(new MediaDescription(sourceId, resource, JsonValues.StringOf(m["type"])!, NumberOf(m["duration"]), JsonValues.StringOf(m["digest"]), transcript)
            {
                TranscriptPath = transcriptPath,
            });
            raw.Add(m);
        }

        // §5.7, Alternates: checked once every description is known, so the order of descriptions does not matter.
        for (var i = 0; i < output.Count; i++)
        {
            var d = output[i];
            var m = raw[i];
            if (JsonValues.StringOf(m["alternateOf"]) is not { } alternateOf)
            {
                if (m.ContainsKey("sync"))
                {
                    diagnostics.Add(DiagnosticCodes.C005FieldInvalid, $"media \"{d.Source}\" has sync but no alternateOf; sync is dropped", file);
                }

                continue;
            }

            var refIndex = output.FindIndex(x => x.Source == alternateOf);
            var sync = SyncOf(m["sync"]);
            var problem = refIndex < 0 ? $"names \"{alternateOf}\", which is no valid description in this node"
                : refIndex == i ? "names itself"
                : raw[refIndex].ContainsKey("alternateOf") ? $"names \"{output[refIndex].Source}\", which is an alternate itself"
                : Alternates.SyncProblem(sync);
            if (problem is not null)
            {
                diagnostics.Add(DiagnosticCodes.C005FieldInvalid, $"media \"{d.Source}\" alternateOf {problem}; alternateOf and sync are dropped", file);
                continue;
            }

            output[i] = d with { AlternateOf = alternateOf, Sync = sync };
        }

        return output;
    }

    /// <summary>
    /// Checks each evidence selector against the cited file when that file is inside the package and
    /// checkable (text, Markdown, HTML or WebVTT), after normalisation (Reader contract §7).
    /// </summary>
    private static List<EvidenceCheck> VerifyEvidence(
        string file,
        JsonObject frontmatter,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, byte[]> bytes,
        DiagnosticBag diagnostics,
        ReaderLimits limits,
        Dictionary<string, string> sourceText,
        EvidenceBudget budget,
        IReadOnlyList<MediaDescription> media)
    {
        if ((frontmatter["moca"] as JsonObject)?["evidence"] is not JsonArray evidence)
        {
            return [];
        }

        var sources = SourcesById(frontmatter);
        var checks = new List<EvidenceCheck>();
        for (var i = 0; i < evidence.Count; i++)
        {
            var entry = evidence[i] as JsonObject;
            var sourceId = JsonValues.StringOf(entry?["source"]);
            var resource = sourceId is not null && sources.TryGetValue(sourceId, out var source)
                ? JsonValues.StringOf(source["resource"])
                : null;
            var path = LocalEvidencePath(file, resource, files);

            // Contract §7: a time range is checked against the cited WebVTT file, or the transcript of the cited media,
            // and bounded by the media's duration. A time range checked this way is evidence checkable in the package.
            double? duration = null;
            Func<(double Start, double End), (double Start, double End)?>? toReference = null;
            if (EvidenceText.IsMediaTimeRange(entry?["selector"]) && (path is null || EvidenceText.KindOf(path) != SourceKind.WebVtt))
            {
                var cited = media.FirstOrDefault(d => d.Source == sourceId);
                if (cited is { TranscriptPath: { } transcriptPath })
                {
                    path = transcriptPath;
                    duration = cited.Duration;
                }
                else if (cited is { AlternateOf: { } refId, Sync: { } sync }
                    && media.FirstOrDefault(d => d.Source == refId) is { TranscriptPath: { } refTranscript } reference)
                {
                    // §5.7: an alternate with no transcript of its own is checked through its reference.
                    path = refTranscript;
                    duration = reference.Duration;
                    // An alternate's own duration bounds the range too, when it declares one.
                    var own = cited.Duration;
                    toReference = range => own is { } limit && !double.IsPositiveInfinity(range.End) && range.End > limit
                        ? null
                        : Alternates.RangeToReference(sync, range);
                }
            }
            else if (path is not null && EvidenceText.KindOf(path) == SourceKind.WebVtt)
            {
                duration = media.FirstOrDefault(d => d.Transcript is not null && d.Transcript == sourceId)?.Duration;
            }

            if (path is null)
            {
                checks.Add(new EvidenceCheck(false));
                continue;
            }

            var kind = EvidenceText.KindOf(path);
            if (kind is null || entry?["selector"] is not JsonObject selector)
            {
                checks.Add(new EvidenceCheck(true));
                continue;
            }

            // Contract §3 limits: past them, the entry is not checked and matched stays absent.
            if (i >= limits.MaxEvidencePerNode || bytes[path].Length > limits.MaxSourceBytesChecked)
            {
                checks.Add(new EvidenceCheck(true));
                continue;
            }

            // Contract §3: one budget of checks and of source bytes scanned, shared by the package and its members.
            var spent = budget.Spend;
            if (spent.Checks >= limits.MaxEvidenceChecksPerPackage || spent.Bytes + bytes[path].Length > limits.MaxEvidenceBytesPerPackage)
            {
                checks.Add(new EvidenceCheck(true));
                continue;
            }

            spent.Checks++;
            spent.Bytes += bytes[path].Length;
            if (!sourceText.TryGetValue(path, out var text))
            {
                // Contract §7: a leading byte order mark is not part of an evidence source.
                text = EvidenceText.Utf8(bytes[path]);
                text = text.StartsWith('\uFEFF') ? text[1..] : text;
                sourceText[path] = text;
            }

            // Each source is normalised, and a WebVTT file's cues parsed, once per package, not once per check.
            string? normalised = null;
            IReadOnlyList<VttCue>? cues = null;
            if (JsonValues.StringOf(selector["type"]) == "FragmentSelector")
            {
                if (kind.Value == SourceKind.WebVtt && !budget.Cues.TryGetValue(path, out cues))
                {
                    cues = EvidenceText.WebVtt(text).Cues;
                    budget.Cues[path] = cues;
                }
            }
            else
            {
                var key = $"{path}\u0000{kind.Value}";
                if (!budget.Normalised.TryGetValue(key, out normalised))
                {
                    normalised = EvidenceText.NormalisedText(text, kind.Value);
                    budget.Normalised[key] = normalised;
                }
            }

            var (matched, located) = EvidenceText.CheckSelectorLocated(selector, text, kind.Value, normalised, duration, toReference, cues);
            if (matched == false)
            {
                diagnostics.Add(DiagnosticCodes.C011EvidenceSelectorUnmatched,
                    $"evidence {JsonValues.StringOf(selector["type"])} for source \"{sourceId}\" does not match {path}", file);
            }

            checks.Add(new EvidenceCheck(true, matched, matched == true ? located : null));
        }

        return checks;
    }

    private static void CheckWindow(string file, JsonObject frontmatter, DiagnosticBag diagnostics)
    {
        var moca = frontmatter["moca"] as JsonObject;
        var from = Dates.Parse(moca?["valid_from"]);
        var until = Dates.Parse(moca?["valid_until"]);
        if (from is not null && until is not null && until <= from)
        {
            diagnostics.Add(DiagnosticCodes.C010ValidityWindowInvalid, "moca.valid_until must be later than moca.valid_from", file);
        }
    }

    /// <summary>Resolves a package-relative reference from a node file.</summary>
    /// <returns>The NFC package path, or null; <c>Escapes</c> when it is unsafe (C008).</returns>
    public static (string? Path, bool Escapes) ResolveReference(string fromFile, string reference)
    {
        var (path, escapes, _) = ResolveReferenceWithReason(fromFile, reference);
        return (path, escapes);
    }

    /// <summary>
    /// Resolves a package-relative reference from a node file. An absolute path or a drive prefix is unsafe, as is
    /// one that escapes the package root (C008); so is a malformed percent-encoding, which never throws.
    /// </summary>
    internal static (string? Path, bool Escapes, string Reason) ResolveReferenceWithReason(string fromFile, string reference)
    {
        var trimmed = reference.StartsWith('<') ? reference[1..] : reference;
        trimmed = trimmed.EndsWith('>') ? trimmed[..^1] : trimmed;
        var withoutFragment = trimmed.Split('#')[0].Split('?')[0];
        if (!TryDecodeUriComponent(withoutFragment, out var clean))
        {
            return (null, true, "has a malformed percent-encoding");
        }

        if (clean.Length == 0)
        {
            return (null, false, "");
        }

        if (clean.StartsWith('/') || clean.StartsWith('\\') || Drive().IsMatch(clean))
        {
            return (null, true, "is absolute");
        }

        var joined = PosixPath.Normalize(PosixPath.Join(PosixPath.DirName(fromFile), clean));
        if (joined == ".." || joined.StartsWith("../", StringComparison.Ordinal) || PosixPath.IsAbsolute(joined))
        {
            return (null, true, "escapes the package root");
        }

        return (UnicodeText.Normalize(joined, NormalizationForm.FormC), false, "");
    }

    /// <summary>Decodes percent-escapes as JavaScript's <c>decodeURIComponent</c> does: false on a malformed escape or invalid UTF-8.</summary>
    private static bool TryDecodeUriComponent(string value, out string decoded)
    {
        decoded = value;
        if (!value.Contains('%'))
        {
            return true;
        }

        var builder = new StringBuilder(value.Length);
        var bytes = new List<byte>();
        var strict = new UTF8Encoding(false, true);
        for (var i = 0; i < value.Length;)
        {
            if (value[i] != '%')
            {
                builder.Append(value[i]);
                i++;
                continue;
            }

            bytes.Clear();
            while (i < value.Length && value[i] == '%')
            {
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                {
                    return false;
                }

                bytes.Add((byte)Convert.ToInt32(value.Substring(i + 1, 2), 16));
                i += 3;
            }

            try
            {
                builder.Append(strict.GetString(bytes.ToArray()));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        decoded = builder.ToString();
        return true;
    }

    private static void CheckPaths(string file, JsonObject frontmatter, string body, IReadOnlyDictionary<string, string> files, DiagnosticBag diagnostics)
    {
        if (frontmatter["sources"] is JsonArray sources)
        {
            foreach (var source in sources.OfType<JsonObject>())
            {
                var resource = JsonValues.StringOf(source["resource"]) ?? "";
                // URLs and scope descriptors are not paths.
                if (!PathReference().IsMatch(resource))
                {
                    continue;
                }

                var (path, escapes, reason) = ResolveReferenceWithReason(file, resource);
                if (escapes)
                {
                    diagnostics.Add(DiagnosticCodes.C008UnsafePath, $"source resource \"{resource}\" {reason}", file);
                }
                else if (path is not null && !files.ContainsKey(path))
                {
                    diagnostics.Add(DiagnosticCodes.C007LinkUnresolved, $"source resource \"{resource}\" does not resolve to a package file", file);
                }
            }
        }

        var fence = false;
        foreach (var line in LineBreak().Split(body))
        {
            if (Fence().IsMatch(line))
            {
                fence = !fence;
            }

            if (fence)
            {
                continue;
            }

            foreach (var written in LinkTargets(InlineCode().Replace(line, "")))
            {
                // As the reference Reader: a leading "<" and a trailing ">" are each dropped.
                var target = written.StartsWith('<') ? written[1..] : written;
                target = target.EndsWith('>') ? target[..^1] : target;
                if ((Scheme().IsMatch(target) && !Drive().IsMatch(target)) || target.StartsWith('#'))
                {
                    continue;
                }

                var (path, escapes, reason) = ResolveReferenceWithReason(file, target);
                if (escapes)
                {
                    diagnostics.Add(DiagnosticCodes.C008UnsafePath, $"link \"{target}\" {reason}", file);
                }
                else if (path is not null && !files.ContainsKey(path))
                {
                    diagnostics.Add(DiagnosticCodes.C007LinkUnresolved, $"link \"{target}\" does not resolve", file);
                }
            }
        }
    }

    /// <summary>Contract §3: the evidence checks made and source bytes scanned in one read, by the package and its members.</summary>
    internal sealed class EvidenceSpend
    {
        public int Checks { get; set; }

        public long Bytes { get; set; }
    }

    /// <summary>Contract §3: one package's evidence work: the read's shared spend, and each source prepared once.</summary>
    private sealed class EvidenceBudget(EvidenceSpend spend)
    {
        public EvidenceSpend Spend { get; } = spend;

        public Dictionary<string, string> Normalised { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyList<VttCue>> Cues { get; } = new(StringComparer.Ordinal);
    }
}
