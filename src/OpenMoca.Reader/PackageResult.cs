using OpenMoca.Reader.Json;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader;

/// <summary>A member of a composed package and what resolving it found.</summary>
public sealed class PackageMember
{
    /// <summary>The pinned member.</summary>
    public required MemberReference Reference { get; init; }

    /// <summary>unresolved, resolved, mismatch or cycle.</summary>
    public string Status { get; set; } = "unresolved";

    /// <summary>The member as read, when the host resolved it.</summary>
    public PackageResult? Result { get; set; }
}

/// <summary>Everything a Reader concluded about one package (Reader interface §4).</summary>
public sealed class PackageResult
{
    /// <summary>The parsed <c>moca.json</c>, or null when it could not be read.</summary>
    public JsonObject? Manifest { get; internal set; }

    /// <summary>The <c>moca-digest-v2</c> digest, or null when the package's entries made it ambiguous.</summary>
    public string? Digest { get; internal set; }

    /// <summary>The BagIt-style payload manifest the digest was computed over.</summary>
    public string? PayloadManifest { get; internal set; }

    /// <summary>Lowercase hex SHA-256 per NFC path, <c>moca.json</c> included, <c>attestations/</c> excluded.</summary>
    public IReadOnlyDictionary<string, string> FileDigests { get; internal set; } = new Dictionary<string, string>();

    /// <summary>The content nodes, sorted by path.</summary>
    public IReadOnlyList<ContentNode> Nodes { get; internal set; } = [];

    /// <summary>What each attestation file concluded.</summary>
    public List<AttestationRecord> Attestations { get; } = [];

    /// <summary>Verified, current reviews per NFC file path.</summary>
    public Dictionary<string, List<AttestedReview>> ReviewsByFile { get; } = new(StringComparer.Ordinal);

    /// <summary>Identities of the signers whose package attestations verified.</summary>
    public List<string> Signers { get; } = [];

    /// <summary>Package ids the signers of its valid package attestations may speak for (package spec §7.2).</summary>
    public List<string> RelationScopes { get; } = [];

    /// <summary>The package's members.</summary>
    public List<PackageMember> Members { get; } = [];

    /// <summary>Profile URIs the manifest declares.</summary>
    public IReadOnlyList<string> Profiles { get; internal set; } = [];

    /// <summary>The structure layer.</summary>
    public PackageStructure? Structure { get; internal set; }

    /// <summary>Every diagnostic, in the order found.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];

    /// <summary>No error in the T, P, M, C or E families (Reader contract §11).</summary>
    public bool Valid { get; internal set; }

    /// <summary>Derived capabilities, sorted (package spec §11). Empty when the package is invalid.</summary>
    public IReadOnlyList<string> Capabilities { get; internal set; } = [];

    /// <summary>
    /// The publisher's intended distribution: <c>open</c>, <c>encrypted-only</c> or <c>service-only</c>; <c>open</c>
    /// when the manifest has none, and null when the manifest is missing or fails its schema (package spec §4.1).
    /// A label only: it never changes what the Reader does.
    /// </summary>
    public string? Distribution { get; internal set; }

    /// <summary>
    /// The manifest images that passed their checks, as declared (package spec §4.6). No bytes: a host reads an image
    /// from the package as it reads any file. Null when the manifest has none or the package is invalid.
    /// </summary>
    public IReadOnlyList<ManifestImage>? Images { get; internal set; }

    /// <summary>
    /// The manifest's own text per declared locale, as declared, without dropped alt text or repeated tags (package
    /// spec §4.7). Null when the manifest has none or the package is invalid.
    /// </summary>
    public IReadOnlyDictionary<string, ManifestTranslation>? Translations { get; internal set; }

    /// <summary>The package tags without repeats (package spec §4.8). Null when the manifest has none or the package is invalid.</summary>
    public IReadOnlyList<string>? Tags { get; internal set; }

    /// <summary>
    /// Where the publisher suggests the package is opened, as declared: https: URIs naming apps and app-type tokens,
    /// first preferred (package spec §4.9). Null when the manifest has none or fails its schema. Advice only: the
    /// Reader never fetches, resolves or opens an app URI.
    /// </summary>
    public IReadOnlyList<string>? Apps { get; internal set; }

    /// <summary>
    /// The IRI of the ordered collection the package is read in (package spec §4.10), when it resolves against the
    /// package's own structure.ttl; null otherwise.
    /// </summary>
    public string? ReadingOrder { get; internal set; }

    /// <summary>The resolved reading order, for search (Reader contract §9).</summary>
    internal OpenMoca.Reader.Structure.ReadingOrder? ResolvedReadingOrder { get; set; }

    /// <summary>Images, translations and tags, held until the package is known to be valid.</summary>
    internal Catalogue? PendingCatalogue { get; set; }

    /// <summary>
    /// For an envelope only (encrypted packages §5.3): its format, whether it was opened, and its unverified outline
    /// when it was not. Null for a package that is not an envelope.
    /// </summary>
    public Encryption.EnvelopeInfo? Envelope { get; internal set; }

    /// <summary>The source the package was read from.</summary>
    public IPackageSource? Source { get; internal set; }

    /// <summary>The package id from the manifest.</summary>
    public string Id => JsonValues.StringOf(Manifest?["id"]) ?? "";

    /// <summary>The package version from the manifest.</summary>
    public string Version => JsonValues.StringOf(Manifest?["version"]) ?? "";

    /// <summary>The package title from the manifest.</summary>
    public string Title => JsonValues.StringOf(Manifest?["title"]) ?? "";

    /// <summary>
    /// The bytes of every package file by NFC path, attestations included, for hosts that show package
    /// files. Empty when the package could not be read.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> FileBytes { get; internal set; } = new Dictionary<string, byte[]>();
}
