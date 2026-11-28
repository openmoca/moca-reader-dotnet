using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Json;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Content;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.Search;

/// <summary>
/// The Reader's search (Reader contract §9): ingress hook, backend, resolve hits into citation records,
/// egress hook, then the gate. Backends and hooks only propose candidates; the gate (digest allowlist,
/// default retrieval policy, audience, locale and edition groups, concepts and scope, upTo, then terms) always runs
/// last, so nothing either of them does can return content the policy excludes.
/// </summary>
public sealed class MocaSearch
{
    private readonly bool _allowUnsigned;
    private readonly Library? _library;
    private readonly IPackageStateSource? _source;
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
        : this(new MemoryPackageStates(packages, allowUnsignedRelations), clock, backend, audiences, hooks, overfetch)
    {
    }

    /// <summary>
    /// A search over a store backend with package states supplied on demand (Reader contract §9.4): each call asks
    /// <paramref name="states"/> only for the states of the packages its candidates come from, the packages that compose
    /// them and, with a terms request, the packages their records name. Relation effects, composers and reading orders
    /// across the whole store come from the source's cached <see cref="IPackageStateSource.Index"/>. Results are the same
    /// as with every state in memory.
    /// </summary>
    /// <param name="states">The host's states, allowlist and index. Relations from unsigned packages take effect as its index says.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="backend">The store backend.</param>
    /// <param name="audiences">The host's audience filter; null for none.</param>
    /// <param name="hooks">Retrieval hooks.</param>
    /// <param name="overfetch">How many times the limit to ask for when the gate may drop hits.</param>
    public MocaSearch(IPackageStateSource states, Func<DateTimeOffset> clock, ISearchBackend backend, IReadOnlyList<string>? audiences = null, SearchHooks? hooks = null, int overfetch = 3)
    {
        ArgumentNullException.ThrowIfNull(states);
        // Contract §9.4: the host's package states, so every library- and trust-dependent field of a stored record
        // is recomputed at query time.
        _source = states;
        _allowUnsigned = states.Index.AllowUnsignedRelations;
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
        // A host's candidate pool is a limit in the range its advanced layer uses.
        if (options.Pool is { } pool)
        {
            options = options with { Limit = pool };
        }

        var now = _clock();
        var index = _library?.Relations() ?? _source!.Index.Relations;
        var loaded = Loaded();
        var view = _source is not null ? new StoreView(_source) : null;
        // The caller's concepts and scope bind the gate. Ingress may narrow what the backend searches,
        // but can never widen what the caller asked for.
        // Package spec §5.4: a locale is a priority list; an empty one is no locale.
        var caller = options with
        {
            Concepts = options.Concepts is { Count: > 0 } c ? [.. c] : null,
            Locale = LocaleLookup.List(options.Locale) is { Count: > 0 } locales ? locales : null,
        };
        var findings = new DiagnosticBag();
        // Contract §9: an upTo that no loaded reading order places limits nothing, and never silently.
        if (caller.UpTo is { } upTo)
        {
            var placed = _library is not null
                ? loaded.Keys.Any(d => ReadingOrderOf(d, loaded, null)?.Positions.ContainsKey(upTo) == true)
                : _source!.Index.Places(upTo);
            if (!placed)
            {
                findings.Add(DiagnosticCodes.S008UpToUnresolved, $"upTo {upTo} is on no loaded package's reading order; it limits nothing");
            }
        }

        Diagnostics = findings.Items;
        // Package spec §5.6: what each loaded composing package binds its members to.
        var composition = CompositionOf(loaded, view);
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

        // Contract §9.2 step 5.4: which representations steps 5.1 to 5.3 keep, worked out once per call.
        var eligible = new Eligibility(now, index, caller.IncludeAll, _audiences, _allowUnlabelled);
        var (fetch, filters) = Plan(request, caller, now, index, loaded, composition, eligible);
        // Contract §15.3–15.4: each node's resolved terms, and which packages are closed or hidden for the request.
        var resolver = caller.Terms is { } termsRequest ? ResolverFor(termsRequest, composition, view) : null;
        // Contract §9.2, "Filters come before the limit": ask again, for more each time, until limit records pass the
        // gate or the backend has no more.
        for (var size = fetch; ; size = Next(size))
        {
            var hits = await _backend.SearchAsync(effectiveQuery, size, filters, cancellationToken).ConfigureAwait(false);
            // Contract §9.4: a store search loads the states of the hits' packages and of the packages composing them.
            var grew = view is not null && await view.EnsureAsync(hits.Select(h => h.Digest), cancellationToken).ConfigureAwait(false);
            if (grew)
            {
                composition = CompositionOf(loaded, view);
            }

            IReadOnlyList<CitationRecord> candidates = Narrow(Resolve(hits, now, index, loaded, view), request, caller, composition);
            if (_hooks.Egress is not null)
            {
                var tagged = candidates.Select(r => r with { Retrieval = new CitationRetrieval("search") }).ToList();
                candidates = await _hooks.Egress(tagged, hookContext with { Request = request }, cancellationToken).ConfigureAwait(false);
            }

            // Records an egress hook added, and, with a terms request, the packages records name (for "Nothing names a
            // hidden package", §15.4), need their states too.
            if (view is not null && await view.EnsureForRecordsAsync(candidates, caller.Terms is not null, cancellationToken).ConfigureAwait(false))
            {
                grew = true;
                composition = CompositionOf(loaded, view);
            }

            if (grew && caller.Terms is { } roundTerms)
            {
                resolver = ResolverFor(roundTerms, composition, view);
            }

            // Counted afresh each round: the last round's count is the one reported.
            var excluded = caller.Terms is not null ? new TermsCount() : null;
            var passed = Gate(candidates, caller, loaded, view, composition, resolver, excluded, eligible);
            // Contract §9.2, "Language first": with localeMode first, limit records of the first group, or no more hits.
            var enough = caller.Locale is not null && caller.LocaleMode == LocaleMode.First
                ? passed.Count(r => LocaleGroup(r, caller.Locale) == 0) >= caller.Limit
                : passed.Count >= caller.Limit;
            var done = enough || hits.Count < size;
            var truncated = !done && _maxCandidates is { } max && size >= max;
            if (done || truncated)
            {
                if (truncated)
                {
                    findings.Add(DiagnosticCodes.S010SearchTruncated, $"stopped at maxCandidates {_maxCandidates} with {passed.Count} of {caller.Limit} records");
                }

                // Contract §15.4: the records the terms gate removed, by reason, and how many were from hidden packages.
                excluded?.Report(findings, "records");

                view?.Report();
                Diagnostics = findings.Items;
                return truncated ? passed : passed.Take(caller.Limit).ToList();
            }
        }
    }

    /// <summary>
    /// The terms gate (Reader contract §15.4) for records found outside a search, such as a node fetched by its id or the
    /// nodes bound to a concept, when they are about to be handed to a model or shown under a rating filter. Returns the
    /// records the creator's terms allow, each with its resolved terms, resolved as a search resolves them (the host's
    /// <see cref="TermsLimits"/>, and composing packages). Records whose package is not loaded are left out. The
    /// records removed are counted in <see cref="Diagnostics"/> (U004, with its <c>excluded</c> and <c>hidden</c>
    /// counts), and a record kept names no package hidden for the request in <c>supersededBy</c> or <c>contestedBy</c>.
    /// </summary>
    /// <param name="records">Citation records from this search's library or packages.</param>
    /// <param name="request">The host's terms request.</param>
    /// <exception cref="InvalidOperationException">The search's states come from a host's own <see cref="IPackageStateSource"/>, which may need to wait: use <see cref="ApplyTermsAsync"/>.</exception>
    public IReadOnlyList<CitationRecord> ApplyTerms(IEnumerable<CitationRecord> records, TermsRequest request)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(request);
        var list = records.ToList();
        StoreView? view = null;
        if (_source is not null)
        {
            if (_source is not MemoryPackageStates memory)
            {
                throw new InvalidOperationException("this search loads package states on demand: use ApplyTermsAsync");
            }

            view = new StoreView(memory);
            view.EnsureForRecordsAsync(list, true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        return ApplyTermsTo(list, request, view);
    }

    /// <summary>
    /// As <see cref="ApplyTerms"/>, loading the states it needs from the search's <see cref="IPackageStateSource"/>:
    /// the records' packages, the packages composing them and the packages their records name.
    /// </summary>
    /// <param name="records">Citation records from this search's library or packages.</param>
    /// <param name="request">The host's terms request.</param>
    /// <param name="cancellationToken">Cancels loading states.</param>
    public async Task<IReadOnlyList<CitationRecord>> ApplyTermsAsync(IEnumerable<CitationRecord> records, TermsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(request);
        var list = records.ToList();
        var view = _source is not null ? new StoreView(_source) : null;
        if (view is not null)
        {
            await view.EnsureForRecordsAsync(list, true, cancellationToken).ConfigureAwait(false);
        }

        return ApplyTermsTo(list, request, view);
    }

    private List<CitationRecord> ApplyTermsTo(List<CitationRecord> records, TermsRequest request, StoreView? view)
    {
        var loaded = Loaded();
        var composition = CompositionOf(loaded, view);
        var resolver = ResolverFor(request, composition, view);
        var excluded = new TermsCount();
        var kept = new List<CitationRecord>();
        foreach (var record in records)
        {
            if (Allowed(record.Package.Digest, loaded) && WithTerms(record, request, resolver, excluded) is { } termed)
            {
                kept.Add(termed);
            }
        }

        var findings = new DiagnosticBag();
        excluded.Report(findings, "records");
        view?.Report();
        Diagnostics = findings.Items;
        return kept;
    }

    private bool Allowed(string digest, Dictionary<string, PackageResult> loaded) =>
        _library is not null ? loaded.ContainsKey(digest) : _source!.Contains(digest);

    private TermsResolver ResolverFor(TermsRequest request, CompositionIndex composition, StoreView? view) =>
        _library is not null
            ? TermsResolver.For(_library, _termsLimits, request)
            : TermsResolver.For(view!.Ordered, composition, _allowUnsigned, _termsLimits, request);

    private Dictionary<string, PackageResult> Loaded() =>
        (_library?.Packages ?? []).ToDictionary(p => p.Result.Digest!, p => p.Result, StringComparer.Ordinal);

    private CompositionIndex CompositionOf(Dictionary<string, PackageResult> loaded, StoreView? view) =>
        new(_library is not null
            ? loaded.Values.Select(r => (r.Digest!, Composition.Of(r)))
            : view!.Ordered.Select(s => (s.Digest, s.Composition)));

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
        Dictionary<string, PackageResult> loaded, CompositionIndex composition, Eligibility eligible)
    {
        // Contract §9.5: for a store search, whether any package in the store binds members, from the cached index.
        var composed = _library is not null ? composition.HasBindings : _source!.Index.HasBindings;
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

        // Contract §9.2 step 5.4 and §9.5: the locale chooses among the representations steps 5.1 to 5.3 keep, which a
        // backend cannot see; so when a node's representations differ there, locale is applied only in the gate.
        var localeHeld = request.Locale is not null && _library is not null && _library.Packages.Any(p =>
            p.Result.Nodes.Any(node => node.Representations.Count > 1 && node.Representations.Select(eligible.In(p.Result, node)).Distinct().Count() > 1));
        var concepts = ConceptsFor(request);
        // The store's allowlist is the host's own list, handed on as it is, never copied.
        var digests = _library is not null ? _library.Packages.Select(p => p.Result.Digest!).ToList() : _source!.Digests;
        // Contract §9.5: a record bound by composition carries none of those concepts itself, so the filter is left to
        // the gate when any loaded package binds members.
        var filters = new SearchFilters(digests, exclude, _audiences, localeHeld ? null : request.Locale, composed ? null : concepts, _allowUnlabelled)
        {
            // The search's own candidate check for what has no pushdown filter: upTo, and concepts bound by composition.
            Accept = AcceptFor(caller, concepts, loaded, composition),
        };
        // Ask for more than the limit when the gate may drop hits (no pushdown, or a filter with no pushdown), or when
        // an egress hook reorders them and needs candidates to choose from. Either way the search asks again while too
        // few pass (contract §9.2).
        var pushdown = _backend.Features.HasFlag(BackendFeatures.FilterPushdown);
        // A store search loads its composers' states with its hits, after this plan, so it cannot yet tell whether any
        // folds editions: with a locale it asks for more, as when one does.
        var folds = request.Locale is not null && request.Editions != EditionsMode.All && (_library is null || composition.HasSlots);
        var moded = request.Locale is not null && request.LocaleMode != LocaleMode.Choose;
        var first = pushdown && !localeHeld && !folds && !moded && _hooks.Egress is null && request.UpTo is null && !(composed && concepts is not null)
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

            return upTo is null || PassesUpTo(record, upTo, loaded, null, composition, late);
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
    private bool PassesUpTo(CitationRecord record, string upTo, Dictionary<string, PackageResult> loaded, StoreView? view,
        CompositionIndex composition, Dictionary<string, (ReadingOrder Order, HashSet<string> Late)?> late)
    {
        if (!ReadingOrder.Passes(record, ReadingOrderOf(record.Package.Digest, loaded, view), upTo))
        {
            return false;
        }

        // Every composer that holds the member limits it by the node's own concepts (contract §9, Members), and by the
        // concepts it binds it to: none of them, or their ancestors, may come after upTo.
        foreach (var composer in composition.ComposersOf(record.Package.Digest))
        {
            // Contract §9, Members: a composer limits a member only when its signers speak for the member (as for
            // relations, R004), or the host allows unsigned packages to.
            if (!_allowUnsigned && !TrustRoot.ScopeCovers(RelationScopesOf(composer.Digest, loaded, view), record.Package.Id))
            {
                continue;
            }

            if (!late.TryGetValue(composer.Digest, out var entry))
            {
                var order = ReadingOrderOf(composer.Digest, loaded, view);
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

    private bool KeepsEdition(CitationRecord record, IReadOnlyList<string> locale, Dictionary<string, PackageResult> loaded, StoreView? view,
        CompositionIndex composition)
    {
        foreach (var composer in composition.ComposersOf(record.Package.Digest))
        {
            if (!composer.HasSlots)
            {
                continue;
            }

            // Package spec §7.4, "Authority": only where the composite's signers cover the record's package, as for upTo.
            // For a store search the composite's state is among those the view loaded for the record's composers.
            if (!_allowUnsigned && !TrustRoot.ScopeCovers(RelationScopesOf(composer.Digest, loaded, view), record.Package.Id))
            {
                continue;
            }

            if (!composer.KeepsEdition(record, locale))
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyList<string> RelationScopesOf(string digest, Dictionary<string, PackageResult> loaded, StoreView? view) =>
        _library is not null
            ? loaded.GetValueOrDefault(digest)?.RelationScopes ?? []
            : view!.States.GetValueOrDefault(digest)?.RelationScopes ?? [];

    private ReadingOrder? ReadingOrderOf(string digest, Dictionary<string, PackageResult> loaded, StoreView? view) =>
        _library is not null
            ? loaded.GetValueOrDefault(digest)?.ResolvedReadingOrder
            : view!.States.GetValueOrDefault(digest)?.ReadingOrder;

    private List<CitationRecord> Resolve(IReadOnlyList<SearchHit> hits, DateTimeOffset now, RelationIndex index, Dictionary<string, PackageResult> loaded, StoreView? view)
    {
        var records = new List<CitationRecord>();
        foreach (var hit in hits)
        {
            if (hit.Record is not null)
            {
                var refreshed = _library is not null ? RefreshClock(hit.Record, now) : PackageState.Refresh(hit.Record, now, view!.States, index);
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
        StoreView? view, CompositionIndex composition, TermsResolver? resolver, TermsCount? excluded, Eligibility eligible)
    {
        var concepts = ConceptsFor(caller);
        var wanted = concepts is null ? null : concepts.ToHashSet(StringComparer.Ordinal);
        var late = new Dictionary<string, (ReadingOrder Order, HashSet<string> Late)?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<CitationRecord>();
        foreach (var record in candidates)
        {
            if (!Allowed(record.Package.Digest, loaded))
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

            if (caller.Locale is not null && !LocaleOk(record, caller.Locale, loaded, eligible))
            {
                continue;
            }

            // Contract §9.2 step 5.4, package spec §7.4: one edition per slot, where the composite speaks for the record.
            if (caller.Locale is not null && caller.Editions != EditionsMode.All && composition.HasSlots && !KeepsEdition(record, caller.Locale, loaded, view, composition))
            {
                continue;
            }

            // Contract §9: localeMode only keeps records whose language matches an entry of the locale list.
            if (caller.Locale is not null && caller.LocaleMode == LocaleMode.Only && LocaleGroup(record, caller.Locale) == caller.Locale.Count)
            {
                continue;
            }

            if (wanted is not null && !composition.BoundTo(record, wanted))
            {
                continue;
            }

            // Contract §9.2 step 6: upTo follows the caller; an ingress hook never changes it.
            if (caller.UpTo is { } upTo && !PassesUpTo(record, upTo, loaded, view, composition, late))
            {
                continue;
            }

            // Contract §9.2: one result per node representation and span, the whole span.
            var key = $"{record.Node.Ref}\u0000{record.Node.Locale}\u0000{record.Span?.Start.ToString(CultureInfo.InvariantCulture) ?? ""}\u0000{record.Span?.End.ToString(CultureInfo.InvariantCulture) ?? ""}";
            if (seen.Contains(key))
            {
                continue;
            }

            // Contract §9.2 step 5.7 and §15.4: with a terms request, the terms gate, after every other filter but one per
            // span, so U004 counts only records the search would otherwise return. Without one, terms remove nothing,
            // and a record never carries terms a backend or hook gave it.
            var kept = record with { Terms = null };
            if (caller.Terms is { } request)
            {
                if (WithTerms(record, request, resolver!, excluded!) is not { } termed)
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

        // Contract §9: localeMode first orders by the first locale entry each record matches, stably within a group.
        if (caller.Locale is { } locales && caller.LocaleMode == LocaleMode.First)
        {
            records = records.OrderBy(r => LocaleGroup(r, locales)).ToList();
        }

        return records;
    }

    /// <summary>Contract §9: the index of the first locale entry a record's language matches, or the list's length for none.</summary>
    private static int LocaleGroup(CitationRecord record, IReadOnlyList<string> locales)
    {
        for (var i = 0; i < locales.Count; i++)
        {
            if (LocaleLookup.Matches(record.Node.Locale, [locales[i]]))
            {
                return i;
            }
        }

        return locales.Count;
    }

    /// <summary>
    /// Contract §15.3–15.4: the record with its resolved terms, or null when the terms gate removes it (counted by
    /// reason, and as hidden when its package is hidden for the request). A package with no terms result has every field
    /// at its strictest. A record that passes names no hidden package in supersededBy or contestedBy (§15.4, "Nothing
    /// names a hidden package").
    /// </summary>
    private static CitationRecord? WithTerms(CitationRecord record, TermsRequest request, TermsResolver resolver, TermsCount excluded)
    {
        var digest = record.Package.Digest;
        var (ai, rating, composites) = resolver.NodeTerms(digest, record.Node.Path);
        var reasons = TermsRules.GateReasons(request, ai, rating);
        if (reasons.Count > 0)
        {
            excluded.Add(reasons, resolver.Closure(digest).Hidden);
            return null;
        }

        var (terms, authority) = resolver.TermsOf(digest);
        return resolver.WithoutHidden(record with { Terms = TermsRules.RecordTerms(authority, ai, rating, TermsRules.ResolveExport(terms, composites)) });
    }

    private static bool LocaleOk(CitationRecord record, IReadOnlyList<string> locale, Dictionary<string, PackageResult> loaded, Eligibility eligible)
    {
        loaded.TryGetValue(record.Package.Digest, out var result);
        var node = result?.Nodes.FirstOrDefault(n => n.Path == record.Node.Path);
        // A store record has no node to choose within: it passes when its own language matches the request.
        if (node is null)
        {
            return LocaleLookup.Matches(record.Node.Locale, locale);
        }

        // Contract §9.2 step 5.4: chosen among the representations that pass steps 5.1 to 5.3, so a retired translation
        // falls back as a missing one does.
        var rep = Citations.Citations.PickRepresentation(node, locale, eligible.In(result!, node));
        return rep is not null && LocaleLookup.RepresentationTag(node, rep) == record.Node.Locale;
    }

    /// <summary>
    /// Contract §9.2 steps 5.2 and 5.3, per representation: whether the default retrieval policy (unless the host opted
    /// in) and the audience set keep it. Cached for one search call. The terms gate is per node, so it never tells two
    /// representations apart.
    /// </summary>
    private sealed class Eligibility(DateTimeOffset now, RelationIndex index, bool includeAll, IReadOnlyList<string>? audiences, bool allowUnlabelled)
    {
        private readonly Dictionary<(PackageResult, ContentNode), Dictionary<Representation, bool>> _kept = [];

        public Func<Representation, bool> In(PackageResult result, ContentNode node)
        {
            if (!_kept.TryGetValue((result, node), out var byRep))
            {
                byRep = new Dictionary<Representation, bool>(ReferenceEqualityComparer.Instance);
                foreach (var rep in node.Representations)
                {
                    var record = Citations.Citations.For(result, node, rep, now, index);
                    byRep[rep] = (includeAll || Citations.Citations.PassesDefaultPolicy(record)) && Audience.Allowed(record, audiences, allowUnlabelled);
                }

                _kept[(result, node)] = byRep;
            }

            return rep => byRep.GetValueOrDefault(rep);
        }
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

    /// <summary>
    /// The package states one store search call has loaded (Reader contract §9.4), from the host's source: only those
    /// its candidates need. Ordered as the source's index, so composers and terms resolve as with every state in memory.
    /// </summary>
    private sealed class StoreView(IPackageStateSource source)
    {
        private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
        private int _loads;
        private int _requested;
        private TimeSpan _elapsed;

        public Dictionary<string, PackageState> States { get; } = new(StringComparer.Ordinal);

        public IEnumerable<PackageState> Ordered => States.Values.OrderBy(s => source.Index.OrderOf(s.Digest));

        /// <summary>Loads the states of these packages and of the packages composing them; true when any was new.</summary>
        public async ValueTask<bool> EnsureAsync(IEnumerable<string> digests, CancellationToken cancellationToken)
        {
            var wanted = new List<string>();
            foreach (var digest in digests)
            {
                Want(digest, wanted);
                foreach (var composer in source.Index.ComposersOf(digest))
                {
                    Want(composer, wanted);
                }
            }

            if (wanted.Count == 0)
            {
                return false;
            }

            using var activity = ReaderTelemetry.Source.StartActivity(ReaderTelemetry.StateLoadSpan);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var states = await source.LoadAsync(wanted, cancellationToken).ConfigureAwait(false);
            var asked = wanted.ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var state in states)
            {
                // Only what was asked for, and only from the allowlist: a source can never widen a search.
                if (state is not null && asked.Contains(state.Digest) && States.TryAdd(state.Digest, state))
                {
                    added++;
                }
            }

            _elapsed += System.Diagnostics.Stopwatch.GetElapsedTime(started);
            _loads++;
            _requested += wanted.Count;
            activity?.SetTag("moca.states.requested", wanted.Count);
            activity?.SetTag("moca.states.loaded", added);
            activity?.SetTag("moca.states.missing", wanted.Count - added);
            activity?.SetTag("moca.store.packages", source.Index.Count);
            return added > 0;
        }

        /// <summary>
        /// Loads the states for records: their packages and their composers; with a terms request, also the packages
        /// named in <c>supersededBy</c> and <c>contestedBy</c>, and their composers (Reader contract §15.4).
        /// </summary>
        public async ValueTask<bool> EnsureForRecordsAsync(IReadOnlyList<CitationRecord> records, bool named, CancellationToken cancellationToken)
        {
            var grew = await EnsureAsync(records.Select(r => r.Package.Digest), cancellationToken).ConfigureAwait(false);
            if (!named)
            {
                return grew;
            }

            var references = records
                .SelectMany(r => (r.Trust.SupersededBy ?? []).Concat(r.Trust.ContestedBy ?? []))
                .Distinct(StringComparer.Ordinal)
                .SelectMany(source.Index.DigestsNamed)
                .ToList();
            return await EnsureAsync(references, cancellationToken).ConfigureAwait(false) || grew;
        }

        /// <summary>Records the call's totals on the current span, if any: loads, states asked for and loaded, time spent.</summary>
        public void Report()
        {
            if (System.Diagnostics.Activity.Current is { } activity)
            {
                activity.SetTag("moca.states.loads", _loads);
                activity.SetTag("moca.states.requested", _requested);
                activity.SetTag("moca.states.held", States.Count);
                activity.SetTag("moca.states.load_ms", _elapsed.TotalMilliseconds);
            }
        }

        private void Want(string digest, List<string> wanted)
        {
            if (_asked.Add(digest) && source.Contains(digest))
            {
                wanted.Add(digest);
            }
        }
    }
}
