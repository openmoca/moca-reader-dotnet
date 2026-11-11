using OpenMoca.Reader.Json;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader;

/// <summary>Options for a <see cref="Library"/>.</summary>
public sealed record LibraryOptions
{
    /// <summary>The host clock for <c>stale</c> and <c>inForce</c>.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Host overlays loaded over the packages' structure (moca-spec ADR-0008).</summary>
    public IReadOnlyList<StructureOverlay> Overlays { get; init; } = [];

    /// <summary>Limits applied to overlays.</summary>
    public ReaderLimits Limits { get; init; } = ReaderLimits.Default;

    /// <summary>
    /// Applies <c>supersedes</c> and <c>conflictsWith</c> relations from unsigned or unscoped packages (package spec
    /// §7.2). Off by default; safe only for a library of the host's own packages.
    /// </summary>
    public bool AllowUnsignedRelations { get; init; }
}

/// <summary>A package in a library, with its sidecar chunks when one was bound.</summary>
/// <param name="Result">The package as read.</param>
/// <param name="Chunks">Sidecar chunks, or null to search the nodes directly.</param>
/// <param name="Index">The sidecar's index.json, or null.</param>
public sealed record LoadedPackage(PackageResult Result, IReadOnlyList<SidecarChunk>? Chunks, JsonObject? Index);

/// <summary>
/// A library of loaded packages (Reader interface §4): citation records, relations between packages,
/// and their merged structure with the host's overlays.
/// </summary>
public sealed class Library
{
    private readonly List<LoadedPackage> _packages = [];
    private readonly bool _allowUnsignedRelations;
    private readonly List<StructureFact> _overlayFacts = [];
    private readonly DiagnosticBag _diagnostics = new();
    private StructureIndex? _structure;

    /// <summary>Creates an empty library; overlays are parsed now and their problems reported as diagnostics.</summary>
    public Library(LibraryOptions? options = null)
    {
        options ??= new LibraryOptions();
        Clock = options.Clock;
        _allowUnsignedRelations = options.AllowUnsignedRelations;
        foreach (var overlay in options.Overlays)
        {
            if (overlay.Layer is not (StructureLayers.Application or StructureLayers.Organisation))
            {
                throw new ArgumentException($"overlay {overlay.Id}: layer must be application or organisation", nameof(options));
            }

            var parsed = StructureParser.Parse(Encoding.UTF8.GetBytes(overlay.Turtle), $"overlay:{overlay.Id}", overlay.Layer,
                $"overlay:{overlay.Id}", _diagnostics, options.Limits);
            _overlayFacts.AddRange(parsed.Facts);
        }
    }

    /// <summary>The host clock.</summary>
    public Func<DateTimeOffset> Clock { get; }

    /// <summary>The loaded packages, in the order added.</summary>
    public IReadOnlyList<LoadedPackage> Packages => _packages;

    /// <summary>Problems in the host's overlays.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.Items;

    /// <summary>The merged structure of every loaded package and overlay (Reader contract §10).</summary>
    public StructureIndex Structure => _structure ??= new StructureIndex(this, _overlayFacts);

    /// <summary>
    /// Adds a valid package, and its resolved members. A package already loaded (same id and version) is skipped.
    /// </summary>
    /// <exception cref="ArgumentException">The package is invalid or has no digest.</exception>
    public Library Add(PackageResult result, SidecarBinding? sidecar = null)
    {
        if (!result.Valid || result.Digest is null)
        {
            throw new ArgumentException($"refusing to add an invalid package: {(result.Id.Length > 0 ? result.Id : "(unknown)")}", nameof(result));
        }

        var key = $"{result.Id}@{result.Version}";
        if (_packages.Find(p => $"{p.Result.Id}@{p.Result.Version}" == key) is { } same)
        {
            // Contract §7: one id@version, one digest. A second digest is refused and reported.
            if (same.Result.Digest != result.Digest)
            {
                _diagnostics.Add(DiagnosticCodes.L001IdentityConflict, $"{key} is already loaded with digest {same.Result.Digest}; {result.Digest} is refused");
            }

            return this;
        }

        var usable = sidecar is { Usable: true };
        _packages.Add(new LoadedPackage(result, usable ? sidecar!.Chunks : null, usable ? sidecar!.Index : null));
        _structure = null;
        // A composed package brings its verified members with it.
        foreach (var member in result.Members)
        {
            if (member is { Status: "resolved", Result.Valid: true })
            {
                Add(member.Result);
            }
        }

        return this;
    }

    /// <summary>Removes a package by digest.</summary>
    public bool Remove(string digest)
    {
        var removed = _packages.RemoveAll(p => p.Result.Digest == digest) > 0;
        if (removed)
        {
            _structure = null;
        }

        return removed;
    }

    /// <summary>Package-level relation effects among the loaded packages (package spec §7.2).</summary>
    public RelationIndex Relations() =>
        PackageState.RelationIndexOf([.. _packages.Select(p => PackageState.From(p.Result))], _allowUnsignedRelations);

    /// <summary>Every node's citation record, one per node, in the requested locale.</summary>
    public IReadOnlyList<CitationRecord> Citations(string? locale = null, bool includeText = false)
    {
        var index = Relations();
        var now = Clock();
        var records = new List<CitationRecord>();
        foreach (var (result, _, _) in _packages)
        {
            foreach (var node in result.Nodes)
            {
                var rep = OpenMoca.Reader.Citations.Citations.PickRepresentation(node, locale);
                records.Add(OpenMoca.Reader.Citations.Citations.For(result, node, rep, now, index, includeText ? rep.Body : null));
            }
        }

        return records;
    }

    /// <summary>
    /// Finds a node by its id (<c>&lt;package id&gt;#&lt;path&gt;</c>) or versioned reference
    /// (<c>&lt;package id&gt;@&lt;version&gt;#&lt;path&gt;</c>). An id matches the first loaded version.
    /// </summary>
    public CitationRecord? Get(string nodeRef, string? locale = null, bool includeText = true)
    {
        var hash = nodeRef.LastIndexOf('#');
        if (hash < 0)
        {
            return null;
        }

        var path = nodeRef[(hash + 1)..];
        var head = nodeRef[..hash];
        var index = Relations();
        foreach (var (result, _, _) in _packages)
        {
            if (head != result.Id && head != $"{result.Id}@{result.Version}")
            {
                continue;
            }

            var node = result.Nodes.FirstOrDefault(n => n.Path == path);
            if (node is null)
            {
                continue;
            }

            var rep = OpenMoca.Reader.Citations.Citations.PickRepresentation(node, locale);
            return OpenMoca.Reader.Citations.Citations.For(result, node, rep, Clock(), index, includeText ? rep.Body : null);
        }

        return null;
    }

    /// <summary>
    /// Lexical search over the loaded packages with the default retrieval policy applied; a shortcut
    /// for a <see cref="MocaSearch"/> over a <see cref="LexicalBackend"/>.
    /// </summary>
    public Task<IReadOnlyList<CitationRecord>> SearchAsync(string query, SearchOptions? options = null, IReadOnlyList<string>? audiences = null, CancellationToken cancellationToken = default) =>
        new MocaSearch(this, new LexicalBackend(this), audiences).SearchAsync(query, options, cancellationToken);
}
