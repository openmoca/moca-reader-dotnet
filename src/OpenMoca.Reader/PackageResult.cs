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

    /// <summary>No error in the T, P, M or C families (Reader contract §11).</summary>
    public bool Valid { get; internal set; }

    /// <summary>Derived capabilities, sorted (package spec §11). Empty when the package is invalid.</summary>
    public IReadOnlyList<string> Capabilities { get; internal set; } = [];

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
