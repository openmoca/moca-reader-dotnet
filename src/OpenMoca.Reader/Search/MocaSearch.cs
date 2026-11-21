using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Json;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.Search;

/// <summary>
/// The Reader's search (Reader contract §9): ingress hook, backend, resolve hits into citation records,
/// egress hook, then the gate. Backends and hooks only propose candidates; the gate (digest allowlist,
/// default retrieval policy, audience, locale, concepts and scope) always runs last, so nothing either
/// of them does can return content the policy excludes.
/// </summary>
public sealed class MocaSearch
{
    private readonly bool _allowUnsigned;
    private readonly Library? _library;
    private readonly HashSet<string>? _allowed;
    private readonly Dictionary<string, PackageState>? _states;
    private readonly RelationIndex? _storeIndex;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ISearchBackend _backend;
    private readonly IReadOnlyList<string>? _audiences;
    private readonly bool _allowUnlabelled;
    private readonly SearchHooks _hooks;
    private readonly int _overfetch;
    private readonly int? _maxCandidates;
    private readonly JsonObject? _termsLimits;

    /// <summary>A search over a library's loaded packages.</summary>
    public MocaSearch(Library library, ISearchBackend backend, IReadOnlyList<string>? audiences = null, SearchHooks? hooks = null, int overfetch = 3)
    {
        _library = library;
        _allowUnsigned = library.AllowUnsignedRelations;
        _clock = library.Clock;
        _backend = backend;
        _audiences = audiences?.ToList();
        _hooks = hooks ?? new SearchHooks();
        _overfetch = overfetch;
    }

    /// <summary>
    /// A search over a store backend: hits resolve from their ingest-time records, and only records whose
    /// package is among <paramref name="packages"/> are returned, with every library- and trust-dependent field recomputed from their states (contract §9.4).
    /// </summary>
    public MocaSearch(IEnumerable<PackageState> packages, Func<DateTimeOffset> clock, ISearchBackend backend, IReadOnlyList<string>? audiences = null, SearchHooks? hooks = null, int overfetch = 3, bool allowUnsignedRelations = false)
    {
        // Contract §9.4: the host's package states, so every library- and trust-dependent field of a stored record
        // is recomputed at query time.
        _states = packages.ToDictionary(p => p.Digest, StringComparer.Ordinal);
        _allowed = [.. _states.Keys];
        _storeIndex = PackageState.RelationIndexOf([.. _states.Values], allowUnsignedRelations);
        _allowUnsigned = allowUnsignedRelations;
        _clock = clock;
        _backend = backend;
        _audiences = audiences?.ToList();
        _hooks = hooks ?? new SearchHooks();
        _overfetch = overfetch;
    }

    /// <summary>
    /// The findings of the latest <see cref="SearchAsync"/> call on this search, such as S008 (Reader interface §5.1).
    /// Empty when it found none.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The library searched, or null for a search over a store backend.</summary>
    public Library? Library => _library;

    /// <summary>The host's audience filter, applied to every search; null for none.</summary>
    public IReadOnlyList<string>? Audiences => _audiences;

    /// <summary>
    /// Contract §8 (moca-spec ADR-0016): under an audience set, unlabelled content is searched alongside the audiences only when
    /// the host allows it. Off by default.
    /// </summary>
    public bool AllowUnlabelled
    {
        get => _allowUnlabelled;
        init => _allowUnlabelled = value;
    }

    /// <summary>
    /// For a store search: the structure source (Reader contract §9.4), a library of packages the host has read (a show
    /// and its seasons) whose structure the hooks see and <c>scope</c> follows. It adds no records. Ignored for a library
    /// search, which uses its own library's structure.
    /// </summary>
    public Library? StructureSource { get; init; }

    private StructureIndex? Structure => _library?.Structure ?? StructureSource?.Structure;

    /// <summary>
    /// The host's bound on the candidates one search call examines (Reader contract §9.2, Reader interface §5.1). A call
    /// it stops before limit records pass the gate reports S010_SEARCH_TRUNCATED. Null (the default) for no bound.
    /// </summary>
    public int? MaxCandidates
    {
        get => _maxCandidates;
        init => _maxCandidates = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxCandidates), value, "must be at least 1");
    }

    /// <summary>
    /// The host's terms limits (Reader contract §15.3 step 4), applied when a search has a terms request: floors, never
    /// values. Null (the default) for none.
    /// </summary>
    public TermsLimits? TermsLimits
    {
        get;
        init
        {
            field = value;
            _termsLimits = value?.ToAi();
        }
    }

    /// <summary>Runs one search; returns citation records with scores, best first unless an egress hook ordered them.</summary>
    public async Task<IReadOnlyList<CitationRecord>> SearchAsync(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var now = _clock();
        var index = _library?.Relations() ?? RelationIndex.Empty;
        var loaded = (_library?.Packages ?? []).ToDictionary(p => p.Result.Digest!, p => p.Result, StringComparer.Ordinal);
        // The caller's concepts and scope bind the gate. Ingress may narrow what the backend searches,
        // but can never widen what the caller asked for.
        var caller = options with { Concepts = options.Concepts is { Count: > 0 } c ? [.. c] : null };
        var findings = new DiagnosticBag();
        // Contract §9: an upTo that no loaded reading order places limits nothing, and never silently.
        if (caller.UpTo is { } upTo)
        {
            IEnumerable<string> digests = _library is not null ? loaded.Keys : _states!.Keys;
            if (!digests.Any(d => ReadingOrderOf(d, loaded)?.Positions.ContainsKey(upTo) == true))
            {
                findings.Add(DiagnosticCodes.S008UpToUnresolved, $"upTo {upTo} is on no loaded package's reading order; it limits nothing");
            }
        }

        Diagnostics = findings.Items;
        // Package spec §5.6: what each loaded composing package binds its members to.
        var composition = new CompositionIndex(_library is not null
            ? loaded.Values.Select(r => (r.Digest!, Composition.Of(r)))
            : _states!.Values.Select(s => (s.Digest, s.Composition)));
        var request = caller;
        var hookContext = new HookContext(request, _library, Structure, _audiences, now);
        var effectiveQuery = query;

        if (_hooks.Ingress is not null && await _hooks.Ingress(query, hookContext, cancellationToken).ConfigureAwait(false) is { } ingress)
        {
            effectiveQuery = ingress.Query ?? query;
            request = request with
            {
                Concepts = ingress.Concepts ?? request.Concepts,
                Scope = ingress.Scope ?? request.Scope,
            };
        }

        var (fetch, filters) = Plan(request, caller, now, index, loaded, composition);
        // Contract §9.2, "Filters come before the limit": ask again, for more each time, until limit records pass the
        // gate or the backend has no more.
        for (var size = fetch; ; size = Next(size))
        {
            var hits = await _backend.SearchAsync(effectiveQuery, size, filters, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CitationRecord> candidates = Narrow(Resolve(hits, now, index, loaded), request, caller, composition);
            if (_hooks.Egress is not null)
            {
                var tagged = candidates.Select(r => r with { Retrieval = new CitationRetrieval("search") }).ToList();
                candidates = await _hooks.Egress(tagged, hookContext with { Request = request }, cancellationToken).ConfigureAwait(false);
            }

            // Counted afresh each round: the last round's count is the one reported.
            var excluded = caller.Terms is not null ? new SortedDictionary<string, int>(StringComparer.Ordinal) : null;
            var passed = Gate(candidates, caller, loaded, composition, excluded);
            var done = passed.Count >= caller.Limit || hits.Count < size;
            var truncated = !done && _maxCandidates is { } max && size >= max;
            if (done || truncated)
            {
                if (truncated)
                {
                    findings.Add(DiagnosticCodes.S010SearchTruncated, $"stopped at maxCandidates {_maxCandidates} with {passed.Count} of {caller.Limit} records");
                }

                // Contract §15.4: the records the terms gate removed, counted by reason.
                if (excluded is { Count: > 0 })
                {
                    findings.Add(DiagnosticCodes.U004TermsExcluded,
                        $"the terms request removed records ({string.Join(", ", excluded.Select(kv => $"{kv.Key}: {kv.Value}"))})");
                }

                Diagnostics = findings.Items;
                return truncated ? passed : passed.Take(caller.Limit).ToList();
            }
        }
    }

    /// <summary>
    /// Reader contract §9.2, "Ingress narrowing": concepts or a scope an ingress hook set keep only records bound to them,
    /// by their own concepts or by composition, after the backend and before the egress hook and the gate, on every round.
    /// </summary>
    private List<CitationRecord> Narrow(List<CitationRecord> candidates, SearchOptions request, SearchOptions caller, CompositionIndex composition)
    {
        if (ReferenceEquals(request.Concepts, caller.Concepts) && request.Scope == caller.Scope)
        {
            return candidates;
        }

        if (ConceptsFor(request) is not { } concepts)
        {
            return candidates;
        }

        var wanted = concepts.ToHashSet(StringComparer.Ordinal);
        return candidates.Where(r => composition.BoundTo(r, wanted)).ToList();
    }

    /// <summary>The next, larger request: double, up to the host's bound.</summary>
    private int Next(int size)
    {
        var doubled = size > int.MaxValue / 2 ? int.MaxValue : size * 2;
        return _maxCandidates is { } max ? Math.Min(doubled, max) : doubled;
    }

    private (int Fetch, SearchFilters Filters) Plan(SearchOptions request, SearchOptions caller, DateTimeOffset now, RelationIndex index,
        Dictionary<string, PackageResult> loaded, CompositionIndex composition)
    {
        var composed = composition.HasBindings;
        var exclude = new List<ExcludedNode>();
        if (!request.IncludeAll && _library is not null)
        {
            foreach (var (result, _, _) in _library.Packages)
            {
                foreach (var node in result.Nodes)
                {
                    var allExcluded = node.Representations.All(rep => !Citations.Citations.PassesDefaultPolicy(Citations.Citations.For(result, node, rep, now, index)));
                    if (allExcluded)
                    {
                        exclude.Add(new ExcludedNode(result.Digest!, $"{result.Id}#{node.Path}"));
                    }
                }
            }
        }

        var concepts = ConceptsFor(request);
        var digests = _library is not null ? _library.Packages.Select(p => p.Result.Digest!).ToList() : [.. _allowed!];
        // Contract §9.5: a record bound by composition carries none of those concepts itself, so the filter is left to
        // the gate when any loaded package binds members.
        var filters = new SearchFilters(digests, exclude, _audiences, request.Locale, composed ? null : concepts, _allowUnlabelled)
        {
            // The search's own candidate check for what has no pushdown filter: upTo, and concepts bound by composition.
            Accept = AcceptFor(caller, concepts, loaded, composition),
        };
        // Ask for more than the limit when the gate may drop hits (no pushdown, or a filter with no pushdown), or when
        // an egress hook reorders them and needs candidates to choose from. Either way the search asks again while too
        // few pass (contract §9.2).
        var pushdown = _backend.Features.HasFlag(BackendFeatures.FilterPushdown);
        var first = pushdown && _hooks.Egress is null && request.UpTo is null && !(composed && concepts is not null)
            ? request.Limit
            : (int)Math.Min((long)request.Limit * _overfetch, int.MaxValue);
        return (Math.Max(1, _maxCandidates is { } max ? Math.Min(first, max) : first), filters);
    }

    /// <summary>
    /// The candidate check for upTo and for concepts bound by composition, on a hit before it is resolved: the gate's own
    /// tests (steps 5.5 and 5.6) on the node's package, path and concepts. Library searches only.
    /// </summary>
    private Func<SearchHit, bool>? AcceptFor(SearchOptions caller, IReadOnlyList<string>? concepts, Dictionary<string, PackageResult> loaded,
        CompositionIndex composition)
    {
        if (_library is null)
        {
            return null;
        }

        var upTo = caller.UpTo;
        var composed = concepts is not null && composition.HasBindings ? concepts.ToHashSet(StringComparer.Ordinal) : null;
        if (upTo is null && composed is null)
        {
            return null;
        }

        var late = new Dictionary<string, (ReadingOrder Order, HashSet<string> Late)?>(StringComparer.Ordinal);
        var paths = new Dictionary<string, Dictionary<string, Content.ContentNode>>(StringComparer.Ordinal);
        var now = _clock();
        return hit =>
        {
            if (!loaded.TryGetValue(hit.Digest, out var result))
            {
                return true;
            }

            if (!paths.TryGetValue(result.Digest!, out var byPath))
            {
                paths[result.Digest!] = byPath = result.Nodes.ToDictionary(n => n.Path, StringComparer.Ordinal);
            }

            if (!byPath.TryGetValue(hit.Path, out var node) || node.Representations.FirstOrDefault(r => r.Locale == hit.Locale) is not { } rep)
            {
                return true;
            }

            var record = Citations.Citations.For(result, node, rep, now, RelationIndex.Empty, "");
            if (composed is not null && !composition.BoundTo(record, composed))
            {
                return false;
            }

            return upTo is null || PassesUpTo(record, upTo, loaded, composition, late);
        };
    }

    /// <summary>The concepts a record must be bound to: the given concepts, plus a scope and its descendants.</summary>
    private IReadOnlyList<string>? ConceptsFor(SearchOptions request)
    {
        var set = new List<string>(request.Concepts ?? []);
        if (request.Scope is not null)
        {
            set.Add(request.Scope);
            set.AddRange(Structure?.Descendants(request.Scope) ?? []);
        }

        var distinct = set.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count > 0 ? distinct : null;
    }

    /// <summary>
    /// Contract §9: the record's own package limits it by the node's own concepts; each loaded package that composes it
    /// and whose reading order places upTo limits it by the node's own concepts and the concepts it binds it to.
    /// </summary>
    private bool PassesUpTo(CitationRecord record, string upTo, Dictionary<string, PackageResult> loaded,
        CompositionIndex composition, Dictionary<string, (ReadingOrder Order, HashSet<string> Late)?> late)
    {
        if (!ReadingOrder.Passes(record, ReadingOrderOf(record.Package.Digest, loaded), upTo))
        {
            return false;
        }

        // Every composer that holds the member limits it by the node's own concepts (contract §9, Members), and by the
        // concepts it binds it to: none of them, or their ancestors, may come after upTo.
        foreach (var composer in composition.ComposersOf(record.Package.Digest))
        {
            // Contract §9, Members: a composer limits a member only when its signers speak for the member (as for
            // relations, R004), or the host allows unsigned packages to.
            if (!_allowUnsigned && !TrustRoot.ScopeCovers(RelationScopesOf(composer.Digest, loaded), record.Package.Id))
            {
                continue;
            }

            if (!late.TryGetValue(composer.Digest, out var entry))
            {
                var order = ReadingOrderOf(composer.Digest, loaded);
                entry = order is not null && order.Positions.TryGetValue(upTo, out var limit)
                    ? (order, order.Positions.Where(kv => kv.Value > limit).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal))
                    : null;
                late[composer.Digest] = entry;
            }

            if (entry is not { } e)
            {
                continue;
            }

            if (!ReadingOrder.Passes(record, e.Order, upTo) || composer.Binds(record, e.Late))
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyList<string> RelationScopesOf(string digest, Dictionary<string, PackageResult> loaded) =>
        _library is not null
            ? loaded.GetValueOrDefault(digest)?.RelationScopes ?? []
            : _states!.GetValueOrDefault(digest)?.RelationScopes ?? [];

    private ReadingOrder? ReadingOrderOf(string digest, Dictionary<string, PackageResult> loaded) =>
        _library is not null
            ? loaded.GetValueOrDefault(digest)?.ResolvedReadingOrder
            : _states!.GetValueOrDefault(digest)?.ReadingOrder;

    private List<CitationRecord> Resolve(IReadOnlyList<SearchHit> hits, DateTimeOffset now, RelationIndex index, Dictionary<string, PackageResult> loaded)
    {
        var records = new List<CitationRecord>();
        foreach (var hit in hits)
        {
            if (hit.Record is not null)
            {
                var refreshed = _library is not null ? RefreshClock(hit.Record, now) : PackageState.Refresh(hit.Record, now, _states!, _storeIndex!);
                if (refreshed is not null)
                {
                    records.Add(refreshed with { Score = hit.Score });
                }
                continue;
            }

            if (!loaded.TryGetValue(hit.Digest, out var result) || hit.Node != $"{result.Id}#{hit.Path}")
            {
                continue;
            }

            var node = result.Nodes.FirstOrDefault(n => n.Path == hit.Path);
            var rep = node?.Representations.FirstOrDefault(r => r.Locale == hit.Locale);
            if (node is null || rep is null)
            {
                continue;
            }

            var text = hit.Span is not null ? SliceBody(rep, hit.Span) : rep.Body;
            if (text is null)
            {
                continue;
            }

            var record = Citations.Citations.For(result, node, rep, now, index, text);
            if (hit.Span is not null)
            {
                record = record with { Span = hit.Span };
            }

            records.Add(record with { Score = hit.Score });
        }

        return records;
    }

    /// <summary>
    /// The gate (contract §9.2 step 5): always last, whatever the backend and the hooks did. Every record that passes,
    /// best first; the caller keeps the limit.
    /// </summary>
    private List<CitationRecord> Gate(IReadOnlyList<CitationRecord> candidates, SearchOptions caller, Dictionary<string, PackageResult> loaded,
        CompositionIndex composition, SortedDictionary<string, int>? excluded)
    {
        var concepts = ConceptsFor(caller);
        var wanted = concepts is null ? null : concepts.ToHashSet(StringComparer.Ordinal);
        var late = new Dictionary<string, (ReadingOrder Order, HashSet<string> Late)?>(StringComparer.Ordinal);
        var allowed = _library is not null ? loaded.Keys.ToHashSet(StringComparer.Ordinal) : _allowed!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<CitationRecord>();
        foreach (var record in candidates)
        {
            if (!allowed.Contains(record.Package.Digest))
            {
                continue;
            }

            if (!caller.IncludeAll && !Citations.Citations.PassesDefaultPolicy(record))
            {
                continue;
            }

            if (!Audience.Allowed(record, _audiences, _allowUnlabelled))
            {
                continue;
            }

            if (caller.Locale is not null && !LocaleOk(record, caller.Locale, loaded))
            {
                continue;
            }

            if (wanted is not null && !composition.BoundTo(record, wanted))
            {
                continue;
            }

            // Contract §9.2 step 6: upTo follows the caller; an ingress hook never changes it.
            if (caller.UpTo is { } upTo && !PassesUpTo(record, upTo, loaded, composition, late))
            {
                continue;
            }

            // Contract §9.2: one result per node representation and span, the whole span.
            var key = $"{record.Node.Ref}\u0000{record.Node.Locale}\u0000{record.Span?.Start.ToString(CultureInfo.InvariantCulture) ?? ""}\u0000{record.Span?.End.ToString(CultureInfo.InvariantCulture) ?? ""}";
            if (seen.Contains(key))
            {
                continue;
            }

            // Contract §9.2 step 5.3 and §15.4: with a terms request, the terms gate. Without one, terms remove nothing,
            // and a record never carries terms a backend or hook gave it.
            var kept = record with { Terms = null };
            if (caller.Terms is { } request)
            {
                if (WithTerms(record, request, loaded, composition, excluded!) is not { } termed)
                {
                    continue;
                }

                kept = termed;
            }

            seen.Add(key);
            records.Add(kept);
        }

        if (_hooks.Egress is null)
        {
            // Stable sort: equal scores keep their backend order, as the reference Reader's does.
            records = records.OrderByDescending(r => r.Score ?? 0).ToList();
        }

        return records;
    }

    /// <summary>
    /// Contract §15.3–15.4: the record with its resolved terms, or null when the terms gate removes it (counted by
    /// reason). A package with no terms result has every field at its strictest.
    /// </summary>
    private CitationRecord? WithTerms(CitationRecord record, TermsRequest request, Dictionary<string, PackageResult> loaded,
        CompositionIndex composition, SortedDictionary<string, int> excluded)
    {
        (JsonNode? Terms, string Authority)? SourceOf(string digest)
        {
            if (_library is not null)
            {
                return loaded.GetValueOrDefault(digest)?.Terms is { } t ? (t.Declared, t.Authority) : null;
            }

            return _states!.GetValueOrDefault(digest)?.Terms is { } s ? (s.Terms, s.Authority) : null;
        }

        // Contract §15.3 step 3: a composing package's terms apply only when its signers speak for the member, as for
        // upTo (§9), or the host allows unsigned packages to. A composer with no terms result counts as setting nothing.
        var composites = new List<JsonNode?>();
        foreach (var composer in composition.ComposersOf(record.Package.Digest))
        {
            if (!_allowUnsigned && !TrustRoot.ScopeCovers(RelationScopesOf(composer.Digest, loaded), record.Package.Id))
            {
                continue;
            }

            if (SourceOf(composer.Digest) is { Terms: { } c })
            {
                composites.Add(c);
            }
        }

        var (terms, authority) = SourceOf(record.Package.Digest) ?? (TermsRules.Strictest, TermsAuthority.Unverified);
        var ai = TermsRules.ResolveAi(terms, record.Node.Path, _termsLimits, composites);
        var rating = TermsRules.RatingOf(terms, record.Node.Path);
        var reasons = TermsRules.GateReasons(request, ai, rating);
        if (reasons.Count > 0)
        {
            foreach (var reason in reasons)
            {
                excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
            }

            return null;
        }

        return record with { Terms = TermsRules.RecordTerms(authority, ai, rating, TermsRules.ResolveExport(terms, composites)) };
    }

    private static bool LocaleOk(CitationRecord record, string locale, Dictionary<string, PackageResult> loaded)
    {
        loaded.TryGetValue(record.Package.Digest, out var result);
        var node = result?.Nodes.FirstOrDefault(n => n.Path == record.Node.Path);
        if (node is null)
        {
            var want = locale.ToLowerInvariant();
            var have = (record.Node.Locale ?? "").ToLowerInvariant();
            return have == want || want.StartsWith(have + "-", StringComparison.Ordinal) || have.StartsWith(want + "-", StringComparison.Ordinal);
        }

        var rep = Citations.Citations.PickRepresentation(node, locale);
        return (rep.Locale ?? JsonValues.StringOf(result!.Manifest?["language"])) == record.Node.Locale;
    }

    /// <summary>A stored record with its clock-dependent fields recomputed for now.</summary>
    private static CitationRecord RefreshClock(CitationRecord record, DateTimeOffset now)
    {
        var trust = record.Trust;
        var staleAt = Content.Dates.Parse(trust.StaleAfter);
        var from = Content.Dates.Parse(trust.ValidFrom);
        var until = Content.Dates.Parse(trust.ValidUntil);
        return record with
        {
            Trust = trust with
            {
                Stale = trust.StaleAfter is not null && staleAt is not null && now >= staleAt,
                InForce = (trust.ValidFrom is null || (from is not null && now >= from)) && (trust.ValidUntil is null || (until is not null && now < until)),
            },
        };
    }

    /// <summary>The text of a byte span inside a representation's body, or null when it lies outside.</summary>
    private static string? SliceBody(Content.Representation rep, CitationSpan span)
    {
        var body = Encoding.UTF8.GetBytes(rep.Body);
        var from = span.Start - rep.BodyOffset;
        var to = span.End - rep.BodyOffset;
        if (from < 0 || to < from || to > body.Length)
        {
            return null;
        }

        return Encoding.UTF8.GetString(body, (int)from, (int)(to - from));
    }
}
