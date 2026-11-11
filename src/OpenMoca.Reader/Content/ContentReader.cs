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

    [GeneratedRegex("""!?\[[^\]]*\]\([\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*(<[^>]+>|[^)\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+)(?:[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+(?:"[^"]*"|'[^']*'))?[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*\)""")]
    private static partial Regex Link();

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
        ReaderLimits limits)
    {
        var sourceText = new Dictionary<string, string>(StringComparer.Ordinal);
        var budget = new EvidenceBudget();
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
            var type = JsonValues.StringOf(frontmatter["type"]);
            if (type is null || UnicodeText.JsTrim(type).Length == 0)
            {
                diagnostics.Add(DiagnosticCodes.C003TypeMissing, "frontmatter needs a non-empty \"type\"", file);
                continue;
            }

            foreach (var error in MocaSchemas.Validate(MocaSchemas.Node, frontmatter))
            {
                diagnostics.Add(DiagnosticCodes.C005FieldInvalid, error, file);
            }

            CheckEvidenceSources(file, frontmatter, diagnostics);
            CheckWindow(file, frontmatter, diagnostics);
            CheckPaths(file, frontmatter, split.Body, files, diagnostics);
            var evidenceChecks = VerifyEvidence(file, frontmatter, files, bytes, diagnostics, limits, sourceText, budget);

            var representation = new Representation
            {
                Locale = nodeLocale,
                File = file,
                Frontmatter = frontmatter,
                Body = split.Body,
                BodyOffset = split.BodyOffset,
                Sha256 = fileDigests.GetValueOrDefault(file),
                EvidenceChecks = evidenceChecks,
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
        EvidenceBudget budget)
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
            if (resource is null || !PathReference().IsMatch(resource))
            {
                checks.Add(new EvidenceCheck(false));
                continue;
            }

            var (path, escapes) = ResolveReference(file, resource);
            if (escapes || path is null || !files.ContainsKey(path) || !EvidenceDirectories.Any(d => path.StartsWith(d, StringComparison.Ordinal)))
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

            // Contract §3: a per-package budget of checks and of source bytes scanned.
            if (budget.Checks >= limits.MaxEvidenceChecksPerPackage || budget.Bytes + bytes[path].Length > limits.MaxEvidenceBytesPerPackage)
            {
                checks.Add(new EvidenceCheck(true));
                continue;
            }

            budget.Checks++;
            budget.Bytes += bytes[path].Length;
            if (!sourceText.TryGetValue(path, out var text))
            {
                // Contract §7: a leading byte order mark is not part of an evidence source.
                text = EvidenceText.Utf8(bytes[path]);
                text = text.StartsWith('\uFEFF') ? text[1..] : text;
                sourceText[path] = text;
            }

            string? normalised = null;
            if (JsonValues.StringOf(selector["type"]) != "FragmentSelector")
            {
                var key = $"{path}\u0000{kind.Value}";
                if (!budget.Normalised.TryGetValue(key, out normalised))
                {
                    normalised = EvidenceText.NormalisedText(text, kind.Value);
                    budget.Normalised[key] = normalised;
                }
            }

            var matched = EvidenceText.CheckSelector(selector, text, kind.Value, normalised);
            if (matched == false)
            {
                diagnostics.Add(DiagnosticCodes.C011EvidenceSelectorUnmatched,
                    $"evidence {JsonValues.StringOf(selector["type"])} for source \"{sourceId}\" does not match {path}", file);
            }

            checks.Add(new EvidenceCheck(true, matched));
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

            foreach (Match m in Link().Matches(InlineCode().Replace(line, "")))
            {
                var target = m.Groups[1].Value;
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

    /// <summary>Contract §3: evidence work across a whole package.</summary>
    internal sealed class EvidenceBudget
    {
        public int Checks { get; set; }

        public long Bytes { get; set; }

        public Dictionary<string, string> Normalised { get; } = new(StringComparer.Ordinal);
    }
}
