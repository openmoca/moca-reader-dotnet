using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader;

/// <summary>A manifest image (package spec §4.6).</summary>
/// <param name="Src">A package path under <c>media/</c>.</param>
/// <param name="Role"><c>icon</c> or <c>cover</c>.</param>
/// <param name="Type"><c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>.</param>
/// <param name="Sizes">Pixel sizes, informative; null when absent.</param>
/// <param name="Alt">Alt text in the default language; null when absent.</param>
public sealed record ManifestImage(string Src, string Role, string Type, string? Sizes, string? Alt);

/// <summary>The manifest's text in one language (package spec §4.7). Each member is null when absent.</summary>
/// <param name="Title">The translated title.</param>
/// <param name="Description">The translated description.</param>
/// <param name="Tags">The translated tags, without repeats.</param>
/// <param name="Alt">An image's <c>src</c> to its alt text in this language.</param>
public sealed record ManifestTranslation(
    string? Title, string? Description, IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, string>? Alt);

/// <summary>A credit's picture (package spec §4.13): a package file under <c>media/</c>, checked as a manifest image.</summary>
/// <param name="Src">A package path under <c>media/</c>.</param>
/// <param name="Type"><c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>.</param>
/// <param name="Alt">Alt text in the default language; null when absent.</param>
public sealed record CreditImage(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("alt")] string? Alt);

/// <summary>Who publishes the package (package spec §4.13), as written. The signer's claim, not proof.</summary>
/// <param name="Name">The name to show.</param>
/// <param name="Url">The publisher's own page; null when absent.</param>
/// <param name="Description">A description in the default language; null when absent.</param>
/// <param name="Image">The publisher's picture; null when absent or when it failed its check (M010).</param>
/// <param name="SameAs">https: URIs of the same party elsewhere (ISNI, ORCID, Wikidata); null when absent.</param>
public sealed record PackagePublisher(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("image")] CreditImage? Image,
    [property: JsonPropertyName("sameAs")] IReadOnlyList<string>? SameAs);

/// <summary>A person or organisation credited for the work (package spec §4.13), as written. The signer's claim, not proof.</summary>
/// <param name="Name">The name to show.</param>
/// <param name="Roles">MARC relator codes, such as <c>aut</c>, <c>nrt</c> or <c>hst</c>.</param>
/// <param name="Url">The contributor's own page; null when absent.</param>
/// <param name="Image">The contributor's picture; null when absent or when it failed its check (M010).</param>
/// <param name="SameAs">https: URIs of the same party elsewhere (ISNI, ORCID, Wikidata); null when absent.</param>
public sealed record Contributor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("image")] CreditImage? Image,
    [property: JsonPropertyName("sameAs")] IReadOnlyList<string>? SameAs);

/// <summary>What the result exposes of images, translations, tags and credits.</summary>
internal sealed record Catalogue(
    IReadOnlyList<ManifestImage>? Images, IReadOnlyDictionary<string, ManifestTranslation>? Translations, IReadOnlyList<string>? Tags,
    PackagePublisher? Publisher = null, string? Url = null, IReadOnlyList<Contributor>? Contributors = null);

/// <summary>Package spec §4.6–4.8: checks run once the manifest passes its schema.</summary>
internal static class CatalogueChecks
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Package spec §4.6: whether bytes start with the signature of an image type. Never decodes.</summary>
    public static bool HasSignature(string type, ReadOnlySpan<byte> b) => type switch
    {
        "image/png" => b.StartsWith(Png),
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/webp" => b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    /// <summary>Package spec §4.6: why an image fails its checks, or null when it passes.</summary>
    public static string? ImageProblem(string src, string type, IReadOnlyDictionary<string, byte[]> bytes, int maxImageBytes)
    {
        if (!bytes.TryGetValue(src.Normalize(System.Text.NormalizationForm.FormC), out var data))
        {
            return $"{src} is not a file in the package";
        }

        if (data.Length > maxImageBytes)
        {
            return $"{src} is {data.Length} bytes, over the limit of {maxImageBytes}";
        }

        return HasSignature(type, data) ? null : $"{src} does not start with the signature of {type}";
    }

    /// <summary>
    /// Package spec §4.13: a credit's picture, or null when it is absent or fails the check of §4.6 (M010, reported
    /// against <paramref name="file"/>). The rest of the credit stands.
    /// </summary>
    private static CreditImage? CheckedImage(JsonNode? node, string where, IReadOnlyDictionary<string, byte[]> bytes, int maxImageBytes, DiagnosticBag diagnostics, string file)
    {
        if (node is not JsonObject image)
        {
            return null;
        }

        var src = JsonValues.StringOf(image["src"])!;
        var type = JsonValues.StringOf(image["type"])!;
        if (ImageProblem(src, type, bytes, maxImageBytes) is { } problem)
        {
            diagnostics.Add(DiagnosticCodes.M010ImageInvalid, $"{where}.image: {problem}; the image is left out", file);
            return null;
        }

        return new CreditImage(src, type, JsonValues.StringOf(image["alt"]));
    }

    private static List<string>? Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(JsonValues.StringOf).OfType<string>()] : null;

    /// <summary>Package spec §4.13: a publisher that passed its schema, as written, less an image that fails its check.</summary>
    public static PackagePublisher Publisher(JsonObject p, IReadOnlyDictionary<string, byte[]> bytes, int maxImageBytes, DiagnosticBag diagnostics) =>
        new(JsonValues.StringOf(p["name"])!, JsonValues.StringOf(p["url"]), JsonValues.StringOf(p["description"]),
            CheckedImage(p["image"], "publisher", bytes, maxImageBytes, diagnostics, PackageDigest.ManifestFile), Strings(p["sameAs"]));

    /// <summary>Package spec §4.13: contributors that passed their schema, as written, less images that fail their check.</summary>
    public static List<Contributor> Contributors(JsonArray list, string where, IReadOnlyDictionary<string, byte[]> bytes, int maxImageBytes, DiagnosticBag diagnostics, string file) =>
        [.. list.OfType<JsonObject>().Select((c, i) => new Contributor(
            JsonValues.StringOf(c["name"])!, Strings(c["roles"]) ?? [], JsonValues.StringOf(c["url"]),
            CheckedImage(c["image"], $"{where}[{i}]", bytes, maxImageBytes, diagnostics, file), Strings(c["sameAs"])))];

    /// <summary>Package spec §2: case-insensitive means A–Z and a–z only.</summary>
    private static string FoldAscii(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (var i = 0; i < src.Length; i++)
        {
            span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
        }
    });

    private static List<string> DedupeTags(JsonArray tags, string where, DiagnosticBag diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var tag in tags.Select(JsonValues.StringOf).OfType<string>())
        {
            if (!seen.Add(FoldAscii(tag)))
            {
                diagnostics.Add(DiagnosticCodes.M012TagDuplicate, $"{where} repeats the tag \"{tag}\"; the repeat is dropped", PackageDigest.ManifestFile);
                continue;
            }

            result.Add(tag);
        }

        return result;
    }

    public static Catalogue Check(JsonObject manifest, IReadOnlyDictionary<string, byte[]> bytes, DiagnosticBag diagnostics, ReaderLimits limits)
    {
        const string file = PackageDigest.ManifestFile;
        List<ManifestImage>? images = null;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        if (manifest["images"] is JsonArray imageArray)
        {
            images = [];
            foreach (var image in imageArray.OfType<JsonObject>())
            {
                var src = JsonValues.StringOf(image["src"])!;
                var type = JsonValues.StringOf(image["type"])!;
                declared.Add(src);
                if (ImageProblem(src, type, bytes, limits.MaxImageBytes) is { } problem)
                {
                    diagnostics.Add(DiagnosticCodes.M010ImageInvalid, $"{problem}; the image is left out", file);
                    dropped.Add(src);
                    continue;
                }

                images.Add(new ManifestImage(src, JsonValues.StringOf(image["role"])!, type,
                    JsonValues.StringOf(image["sizes"]), JsonValues.StringOf(image["alt"])));
            }
        }

        var tags = manifest["tags"] is JsonArray tagArray ? DedupeTags(tagArray, "tags", diagnostics) : null;

        Dictionary<string, ManifestTranslation>? translations = null;
        if (manifest["translations"] is JsonObject translationObject)
        {
            var locales = (manifest["locales"] as JsonArray ?? []).Select(JsonValues.StringOf).OfType<string>().Select(FoldAscii).ToHashSet(StringComparer.Ordinal);
            translations = new Dictionary<string, ManifestTranslation>(StringComparer.Ordinal);
            foreach (var (tag, node) in translationObject)
            {
                if (!locales.Contains(FoldAscii(tag)))
                {
                    diagnostics.Add(DiagnosticCodes.M011TranslationLocaleUndeclared, $"translations has \"{tag}\", which is not a tag in locales", file);
                }

                var value = node!.AsObject();
                var translatedTags = value["tags"] is JsonArray t ? DedupeTags(t, $"translations.{tag}.tags", diagnostics) : null;
                Dictionary<string, string>? alt = null;
                if (value["alt"] is JsonObject altObject)
                {
                    alt = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (src, text) in altObject)
                    {
                        if (!declared.Contains(src))
                        {
                            diagnostics.Add(DiagnosticCodes.M013AltImageUnknown, $"translations.{tag}.alt names {src}, which is not in images; it is dropped", file);
                            continue;
                        }

                        // An image dropped with M010 takes its alt text with it, without a further diagnostic.
                        if (!dropped.Contains(src))
                        {
                            alt[src] = JsonValues.StringOf(text)!;
                        }
                    }
                }

                translations[tag] = new ManifestTranslation(
                    JsonValues.StringOf(value["title"]), JsonValues.StringOf(value["description"]), translatedTags, alt);
            }
        }

        // Package spec §4.13: credits, exposed as written; labels, not proof.
        var publisher = manifest["publisher"] is JsonObject p ? Publisher(p, bytes, limits.MaxImageBytes, diagnostics) : null;
        var contributors = manifest["contributors"] is JsonArray c
            ? Contributors(c, "contributors", bytes, limits.MaxImageBytes, diagnostics, file)
            : null;
        return new Catalogue(images, translations, tags, publisher, JsonValues.StringOf(manifest["url"]), contributors);
    }
}
