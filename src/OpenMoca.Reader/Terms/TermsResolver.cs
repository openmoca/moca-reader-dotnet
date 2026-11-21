using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader;

/// <summary>
/// The terms gate's shared view of a set of packages (Reader contract §15.3–15.4, moca-spec ADR-0041): each node's
/// resolved terms, and whether a package is closed or hidden for a request. Search, ingest, the structure operations and
/// the catalogue all use it, so they agree on what is closed.
/// </summary>
internal sealed class TermsResolver
{
    /// <summary>One package as the resolver sees it. <paramref name="Terms"/> null: no terms result, so the strictest.</summary>
    internal sealed record Source(string Digest, string Id, string Version, JsonNode? Terms, string Authority, bool HasTerms, IReadOnlyList<string> RelationScopes);

    private readonly Dictionary<string, Source> _byDigest;
    private readonly CompositionIndex _composition;
    private readonly bool _allowUnsigned;
    private readonly JsonObject? _limits;
    private readonly Dictionary<string, PackageClosure> _closures = new(StringComparer.Ordinal);
    private List<string>? _hiddenNames;

    private TermsResolver(IEnumerable<Source> packages, CompositionIndex composition, bool allowUnsigned, JsonObject? limits, TermsRequest? request)
    {
        _byDigest = new Dictionary<string, Source>(StringComparer.Ordinal);
        foreach (var p in packages)
        {
            _byDigest.TryAdd(p.Digest, p);
        }

        _composition = composition;
        _allowUnsigned = allowUnsigned;
        _limits = limits;
        Request = request;
    }

    /// <summary>The terms request, for closed and hidden; null for none.</summary>
    public TermsRequest? Request { get; }

    /// <summary>A resolver over a library's loaded packages.</summary>
    public static TermsResolver For(Library library, JsonObject? limits, TermsRequest? request) =>
        For(library.Packages.Select(p => p.Result), library.AllowUnsignedRelations, limits, request);

    /// <summary>A resolver over read packages.</summary>
    public static TermsResolver For(IEnumerable<PackageResult> results, bool allowUnsigned, JsonObject? limits, TermsRequest? request)
    {
        var list = results.Where(r => r.Digest is not null).ToList();
        return new TermsResolver(
            list.Select(r => new Source(r.Digest!, r.Id, r.Version, r.Terms?.Declared, r.Terms?.Authority ?? TermsAuthority.Unverified, r.Terms is not null, r.RelationScopes)),
            new CompositionIndex(list.Select(r => (r.Digest!, Composition.Of(r)))),
            allowUnsigned,
            limits,
            request);
    }

    /// <summary>A resolver over package states (contract §9.4), for a store search.</summary>
    public static TermsResolver For(IEnumerable<PackageState> states, CompositionIndex composition, bool allowUnsigned, JsonObject? limits, TermsRequest? request) =>
        new(states.Select(s => new Source(s.Digest, s.Id, s.Version, s.Terms?.Terms, s.Terms?.Authority ?? TermsAuthority.Unverified, s.Terms is not null, s.RelationScopes)),
            composition, allowUnsigned, limits, request);

    /// <summary>Whether the package is one this resolver knows.</summary>
    public bool Knows(string digest) => _byDigest.ContainsKey(digest);

    /// <summary>The package's declared terms and authority; the strictest when it has no terms result.</summary>
    public (JsonNode? Terms, string Authority) TermsOf(string digest) =>
        _byDigest.TryGetValue(digest, out var p) && p.HasTerms ? (p.Terms, p.Authority) : (TermsRules.Strictest, TermsAuthority.Unverified);

    /// <summary>
    /// Contract §15.3 step 3: the terms of the composing packages whose signers speak for this package, as for upTo (§9),
    /// or of every composing package when the host allows unsigned ones. A composer with no terms sets nothing.
    /// </summary>
    public List<JsonNode?> Composites(string digest)
    {
        var id = _byDigest.GetValueOrDefault(digest)?.Id;
        var output = new List<JsonNode?>();
        foreach (var composer in _composition.ComposersOf(digest))
        {
            var c = _byDigest.GetValueOrDefault(composer.Digest);
            if (!_allowUnsigned && !TrustRoot.ScopeCovers(c?.RelationScopes ?? [], id))
            {
                continue;
            }

            if (c is { HasTerms: true, Terms: { } t })
            {
                output.Add(t);
            }
        }

        return output;
    }

    /// <summary>A node's resolved <c>ai</c> terms and effective rating, with the composites they came from.</summary>
    public (CitationAiTerms Ai, JsonNode? Rating, List<JsonNode?> Composites) NodeTerms(string digest, string path)
    {
        var (terms, _) = TermsOf(digest);
        var composites = Composites(digest);
        return (TermsRules.ResolveAi(terms, path, _limits, composites), TermsRules.RatingOf(terms, path), composites);
    }

    /// <summary>Contract §15.4: whether the package is closed, and hidden, for the request.</summary>
    public PackageClosure Closure(string digest)
    {
        if (Request?.Ai is null)
        {
            return PackageClosure.Open;
        }

        if (!_closures.TryGetValue(digest, out var closure))
        {
            var (terms, _) = TermsOf(digest);
            closure = TermsRules.ClosureOf(Request, TermsRules.ResolveAi(terms, null, _limits, Composites(digest)));
            _closures[digest] = closure;
        }

        return closure;
    }

    /// <summary>
    /// Contract §15.4, "Nothing names a hidden package": whether a reference (a package id, an id@version, or a node id
    /// under either, as in <c>trust.supersededBy</c> and <c>trust.contestedBy</c>) names a package hidden for the request.
    /// </summary>
    public bool NamesHidden(string reference)
    {
        if (Request?.Ai is null)
        {
            return false;
        }

        _hiddenNames ??= [.. _byDigest.Values.Where(p => Closure(p.Digest).Hidden).SelectMany(p => new[] { p.Id, $"{p.Id}@{p.Version}" })];
        return _hiddenNames.Exists(n => reference == n || reference.StartsWith(n + "#", StringComparison.Ordinal));
    }

    /// <summary>A record whose <c>supersededBy</c> and <c>contestedBy</c> leave out every entry that names a hidden package.</summary>
    public CitationRecord WithoutHidden(CitationRecord record)
    {
        var trust = record.Trust;
        if (trust.SupersededBy is not { Count: > 0 } && trust.ContestedBy is not { Count: > 0 })
        {
            return record;
        }

        IReadOnlyList<string>? Keep(IReadOnlyList<string>? list) =>
            list is null ? null : list.Where(r => !NamesHidden(r)).ToList() is { Count: > 0 } kept ? kept : null;

        return record with { Trust = trust with { SupersededBy = Keep(trust.SupersededBy), ContestedBy = Keep(trust.ContestedBy) } };
    }
}

/// <summary>The counts of a <c>U004_TERMS_EXCLUDED</c> finding (Reader contract §15.4): by reason, and how many were hidden.</summary>
internal sealed class TermsCount
{
    private readonly SortedDictionary<string, int> _reasons = new(StringComparer.Ordinal);

    /// <summary>How many removed records or units belong to packages hidden for the request.</summary>
    public int Hidden { get; private set; }

    /// <summary>Counts one removed record or unit under each of its reasons.</summary>
    public void Add(IEnumerable<string> reasons, bool hidden)
    {
        foreach (var r in reasons)
        {
            _reasons[r] = _reasons.GetValueOrDefault(r) + 1;
        }

        if (hidden)
        {
            Hidden++;
        }
    }

    /// <summary>Adds U004, with its counts, when anything was removed.</summary>
    /// <param name="findings">Where to add it.</param>
    /// <param name="what">What was removed, for the message: <c>records</c> or <c>units from the ingest</c>.</param>
    public void Report(DiagnosticBag findings, string what)
    {
        if (_reasons.Count == 0)
        {
            return;
        }

        var counts = string.Join(", ", _reasons.Select(kv => $"{kv.Key}: {kv.Value}"));
        findings.AddExisting(new Diagnostic(DiagnosticCodes.U004TermsExcluded, DiagnosticCodes.DefaultSeverity(DiagnosticCodes.U004TermsExcluded),
            $"the terms request removed {what} ({counts}; from hidden packages: {Hidden})")
        {
            Excluded = new Dictionary<string, int>(_reasons, StringComparer.Ordinal),
            Hidden = Hidden,
        });
    }
}
