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

    /// <summary>Whether the host allows unsigned packages to make relations and to limit their members (package spec
    /// §7.2, Reader contract §9).</summary>
    public bool AllowUnsignedRelations => _allowUnsignedRelations;

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
    /// The structure operations for a host that hands their answers to a model (Reader contract §10, "Under a terms
    /// request"; moca-spec ADR-0041): the merged structure of the loaded packages that are not closed for the request,
    /// and the overlays. Labels, definitions and relations only a closed package states are absent, and
    /// <see cref="StructureIndex.Nodes"/> returns records only from open packages (gate them too before handing them
    /// on). A request with no <c>ai</c> part closes nothing, so it is <see cref="Structure"/>.
    /// </summary>
    /// <param name="terms">The host's terms request; null for none.</param>
    /// <param name="termsLimits">The host's terms limits; null for none.</param>
    public StructureIndex StructureFor(TermsRequest? terms, TermsLimits? termsLimits = null)
    {
        if (terms?.Ai is null)
        {
            return Structure;
        }

        var resolver = TermsResolver.For(this, termsLimits?.ToAi(), terms);
        return new StructureIndex(this, _overlayFacts, [.. _packages.Where(p => !resolver.Closure(p.Result.Digest!).Closed)]);
    }

    /// <summary>
    /// Whether a loaded package is closed, and hidden, for a terms request (Reader contract §15.4, "Closed and hidden"),
    /// from its package-level resolved terms: the platform, the package, a composing package that speaks for it and the
    /// host's limits. Null when no package with that digest is loaded.
    /// </summary>
    /// <param name="digest">The package digest.</param>
    /// <param name="terms">The host's terms request.</param>
    /// <param name="termsLimits">The host's terms limits; null for none.</param>
    public PackageClosure? ClosureOf(string digest, TermsRequest terms, TermsLimits? termsLimits = null)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(terms);
        var resolver = TermsResolver.For(this, termsLimits?.ToAi(), terms);
        return resolver.Knows(digest) ? resolver.Closure(digest) : null;
    }

    /// <summary>
    /// What a model may be told about the loaded packages (Reader contract §15.4, "The catalogue rule"; moca-spec
    /// ADR-0041), ordered by id then version in code-point order. A package has an entry only when one of its records,
    /// or of its members' at any depth, passes the audience set and (unless <see cref="CatalogueOptions.IncludeAll"/>)
    /// the default retrieval policy; when its own rating passes the request's rating gate; and when it is not hidden for
    /// the request. There is no placeholder for a package left out.
    /// </summary>
    /// <param name="options">The terms request and the host's settings.</param>
    public IReadOnlyList<CatalogueEntry> Catalogue(CatalogueOptions? options = null)
    {
        options ??= new CatalogueOptions();
        var request = options.Terms;
        var resolver = TermsResolver.For(this, options.TermsLimits?.ToAi(), request);
        var index = Relations();
        var now = Clock();
        var audiences = options.Audiences?.ToList();
        var own = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool PassesOwn(PackageResult result)
        {
            if (!own.TryGetValue(result.Digest!, out var passes))
            {
                passes = result.Nodes.Any(node => node.Representations.Any(rep =>
                {
                    var r = OpenMoca.Reader.Citations.Citations.For(result, node, rep, now, index);
                    return (options.IncludeAll || OpenMoca.Reader.Citations.Citations.PassesDefaultPolicy(r))
                        && Audience.Allowed(r, audiences, options.AllowUnlabelled);
                }));
                own[result.Digest!] = passes;
            }

            return passes;
        }

        // Contract §15.4, catalogue rule 1: its own records, or those of a member it composes, at any depth; each
        // package walked once.
        bool Passes(PackageResult result)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<PackageResult>([result]);
            while (queue.Count > 0)
            {
                var r = queue.Dequeue();
                if (!seen.Add(r.Digest!))
                {
                    continue;
                }

                if (PassesOwn(r))
                {
                    return true;
                }

                foreach (var m in r.Members)
                {
                    if (m is { Status: "resolved", Result: { Valid: true, Digest: not null } member })
                    {
                        queue.Enqueue(member);
                    }
                }
            }

            return false;
        }

        var listed = new List<(PackageResult Result, PackageClosure Closure)>();
        foreach (var result in _packages.Select(p => p.Result)
                     .OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal))
        {
            if (!Passes(result))
            {
                continue;
            }

            // Catalogue rule 2: the package's own rating, never a node's, gated as a record with that rating would be.
            if (request?.Rating is { } rating)
            {
                var declared = result.Terms?.Declared as System.Text.Json.Nodes.JsonObject;
                var packageRating = declared is not null && declared.TryGetPropertyValue("rating", out var r) ? r ?? System.Text.Json.Nodes.JsonValue.Create(false) : null;
                // The request has no ai part here, so the ai terms are not read.
                if (TermsRules.GateReasons(new TermsRequest { Rating = rating }, TermsRules.ResolveAi(null, null, null, []), packageRating).Count > 0)
                {
                    continue;
                }
            }

            // Catalogue rule 3: a hidden package has no entry.
            var closure = resolver.Closure(result.Digest!);
            if (!closure.Hidden)
            {
                listed.Add((result, closure));
            }
        }

        var present = listed.Select(x => $"{x.Result.Id}@{x.Result.Version}").ToHashSet(StringComparer.Ordinal);
        return [.. listed.Select(x => EntryOf(x.Result, x.Closure, present))];
    }

    /// <summary>Contract §15.4: a package's catalogue entry, open or closed.</summary>
    private static CatalogueEntry EntryOf(PackageResult result, PackageClosure closure, HashSet<string> present)
    {
        var entry = new CatalogueEntry
        {
            Id = result.Id,
            Version = result.Version,
            Title = result.Title,
            Publisher = result.Publisher?.Name,
            Signed = result.Signers.Count > 0,
        };
        if (closure.Closed)
        {
            return entry with { ClosedBy = [.. closure.ClosedBy] };
        }

        IReadOnlyList<string>? members = null;
        if (result.Manifest?["members"] is System.Text.Json.Nodes.JsonArray list && list.Count > 0)
        {
            members = [.. list.OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(m => present.Contains($"{JsonValues.StringOf(m["id"])}@{JsonValues.StringOf(m["version"])}"))
                .Select(m => JsonValues.StringOf(m["id"])!)];
        }

        return entry with
        {
            Description = JsonValues.StringOf(result.Manifest?["description"]),
            Tags = result.Tags is { } tags ? [.. tags] : null,
            Members = members,
        };
    }

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
                // Package spec §13.5: a second issued copy of the same original is expected, and only noted.
                if (same.Result.IssuedCopy?.OriginalDigest is { } original && original == result.IssuedCopy?.OriginalDigest)
                {
                    _diagnostics.Add(DiagnosticCodes.L002IssuedCopyDuplicate, $"{key} is already loaded as another issued copy of {original}; {result.Digest} is not loaded");
                }
                else
                {
                    _diagnostics.Add(DiagnosticCodes.L001IdentityConflict, $"{key} is already loaded with digest {same.Result.Digest}; {result.Digest} is refused");
                }
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

    /// <summary>
    /// Every node's citation record, one per node, in the representation the locale selects (package spec §5.4).
    /// </summary>
    /// <param name="locale">A language tag or a priority list of them, most preferred first (<c>"fr"</c>,
    /// <c>["fr-CA", "en"]</c>); null or empty for each node's default representation.</param>
    /// <param name="includeText">Whether records carry their text.</param>
    public IReadOnlyList<CitationRecord> Citations(LocaleRequest? locale = null, bool includeText = false) =>
        Citations(locale, includeText, _packages);

    /// <summary>The citation records of some of the loaded packages, with relations among every loaded package.</summary>
    internal IReadOnlyList<CitationRecord> Citations(LocaleRequest? locale, bool includeText, IReadOnlyList<LoadedPackage> packages)
    {
        var index = Relations();
        var now = Clock();
        var records = new List<CitationRecord>();
        foreach (var (result, _, _) in packages)
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
    /// <param name="nodeRef">The node id or versioned reference.</param>
    /// <param name="locale">A language tag or a priority list of them (package spec §5.4); null or empty for the default representation.</param>
    /// <param name="includeText">Whether the record carries its text.</param>
    public CitationRecord? Get(string nodeRef, LocaleRequest? locale = null, bool includeText = true)
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
