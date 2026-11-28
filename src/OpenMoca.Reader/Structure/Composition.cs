using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Json;

namespace OpenMoca.Reader.Structure;

/// <summary>
/// One resolved, valid, pin-matched member of a composing package, at any depth (package spec §5.6, Reader contract
/// §9). Plain JSON in a package state (Reader contract §9.4).
/// </summary>
public sealed record CompositionMember
{
    /// <summary>The member's digest.</summary>
    [JsonPropertyName("digest")] public required string Digest { get; init; }

    /// <summary>The member's package id.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>
    /// Whether the composing package lists the member in its own <c>members</c> (another member may also compose it).
    /// Only such a member can be an edition (package spec §7.4).
    /// </summary>
    [JsonPropertyName("direct")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Direct { get; init; }

    /// <summary>The member's manifest <c>language</c>, when it has one: its tag when it is an edition (package spec §7.4).</summary>
    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    /// <summary>Whether the composing package's structure declares the member's id as a concept.</summary>
    [JsonPropertyName("bound")] public required bool Bound { get; init; }

    /// <summary>The paths of the member's nodes whose identifiers the structure declares as concepts, sorted.</summary>
    [JsonPropertyName("nodes")] public required IReadOnlyList<string> Nodes { get; init; }

    /// <summary>The digests of the members that compose this one within the composition, sorted; empty when the
    /// composing package composes it directly.</summary>
    [JsonPropertyName("parents")] public required IReadOnlyList<string> Parents { get; init; }
}

/// <summary>
/// What a composing package's own structure binds its members to: each member once, and, for each concept on the way
/// up from the declared identifiers, its broader concepts and the concepts it is a part of.
/// </summary>
public sealed record CompositionState
{
    /// <summary>Every resolved, valid, pin-matched member, by digest.</summary>
    [JsonPropertyName("members")] public required IReadOnlyList<CompositionMember> Members { get; init; }

    /// <summary>The links above each declared identifier, in the composing package's own graph.</summary>
    [JsonPropertyName("up")] public required IReadOnlyDictionary<string, IReadOnlyList<string>> Up { get; init; }

    /// <summary>The composing package's edition groups (package spec §7.4), when it has any.</summary>
    [JsonPropertyName("slots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EditionSlot>? Slots { get; init; }
}

/// <summary>
/// Binding by composition (package spec §5.6, Reader contract §9): a composing package's own structure may use a
/// member's package id, at any depth, or a member's node identifier as a concept. The member's records are then bound
/// to it, and to every concept that has it as a descendant, for the concepts and scope filters, upTo and nodes().
/// Contract §3: the work stays roughly linear. Only the direct bindings and the links above them are kept; a search
/// walks down once from the concepts it needs.
/// </summary>
public static class Composition
{
    private static readonly ConditionalWeakTable<PackageResult, StrongBox<CompositionState?>> Cache = new();

    /// <summary>What a composing package binds its members to, or null when it has no valid members.</summary>
    public static CompositionState? Of(PackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Cache.GetValue(result, r => new StrongBox<CompositionState?>(Compute(r))).Value;
    }

    private static CompositionState? Compute(PackageResult result)
    {
        var graph = result.Structure?.Graph;
        bool Declared(string iri) => graph is not null && graph.Concepts.TryGetValue(iri, out var c) && c.Declared is not null;

        var entries = new Dictionary<string, (string Id, string? Language, bool Bound, List<string> Nodes, SortedSet<string> Parents)>(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var starts = new List<string>();
        var queue = new Queue<(IEnumerable<PackageMember> Members, string? Parent)>();
        queue.Enqueue((result.Members, null));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (result.Digest is not null)
        {
            visited.Add(result.Digest);
        }

        while (queue.Count > 0)
        {
            var (members, parent) = queue.Dequeue();
            foreach (var m in members)
            {
                var child = m.Result;
                if (m.Status != "resolved" || child is null || !child.Valid || child.Digest is null || child.Digest == result.Digest)
                {
                    continue;
                }

                if (!entries.TryGetValue(child.Digest, out var entry))
                {
                    var nodes = child.Nodes.Select(n => n.Path).Where(p => Declared($"{child.Id}#{p}")).OrderBy(p => p, StringComparer.Ordinal).ToList();
                    var language = JsonValues.StringOf(child.Manifest?["language"]) is { Length: > 0 } l ? l : null;
                    entry = (child.Id, language, Declared(child.Id), nodes, new SortedSet<string>(StringComparer.Ordinal));
                    entries[child.Digest] = entry;
                    if (entry.Bound)
                    {
                        starts.Add(child.Id);
                    }

                    starts.AddRange(nodes.Select(p => $"{child.Id}#{p}"));
                }

                if (parent is not null)
                {
                    entry.Parents.Add(parent);
                }
                else
                {
                    // Package spec §7.4: the composing package lists this member itself.
                    listed.Add(child.Digest);
                }

                if (visited.Add(child.Digest))
                {
                    queue.Enqueue((child.Members, child.Digest));
                }
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        // The links above each declared identifier, each concept walked once.
        var up = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var stack = new Stack<string>(starts);
        while (graph is not null && stack.Count > 0)
        {
            var n = stack.Pop();
            if (up.ContainsKey(n))
            {
                continue;
            }

            // Package spec §7.4: an edition is bound to its slot as a part is.
            var parents = graph.Out("broader", n).Concat(graph.In("hasPart", n)).Concat(graph.In("workExample", n)).Select(e => e.Iri)
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            up[n] = parents;
            foreach (var p in parents.Where(p => !up.ContainsKey(p)))
            {
                stack.Push(p);
            }
        }

        return new CompositionState
        {
            Members = [.. entries.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CompositionMember
            {
                Digest = kv.Key,
                Id = kv.Value.Id,
                Direct = listed.Contains(kv.Key),
                Language = kv.Value.Language,
                Bound = kv.Value.Bound,
                Nodes = kv.Value.Nodes,
                Parents = [.. kv.Value.Parents],
            })],
            Up = up.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            // Package spec §7.4: the composite's edition groups.
            Slots = result.Structure?.Slots is { Count: > 0 } slots ? [.. slots] : null,
        };
    }
}

/// <summary>One composing package's bindings, from <see cref="Composition.Of"/> or a package state.</summary>
public sealed class Composer
{
    private readonly Dictionary<string, (CompositionMember Member, HashSet<string> Nodes)> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _memberChildren = new(StringComparer.Ordinal);
    private readonly Dictionary<IReadOnlySet<string>, (HashSet<string> Below, HashSet<string> Covered)> _reach = new(ReferenceEqualityComparer.Instance);

    internal Composer(string digest, CompositionState composition)
    {
        Digest = digest;
        foreach (var m in composition.Members ?? [])
        {
            if (m?.Digest is null || m.Id is null)
            {
                continue;
            }

            _members[m.Digest] = (m, new HashSet<string>(m.Nodes ?? [], StringComparer.Ordinal));
            foreach (var p in m.Parents ?? [])
            {
                if (!_memberChildren.TryGetValue(p, out var list))
                {
                    list = [];
                    _memberChildren[p] = list;
                }

                list.Add(m.Digest);
            }
        }

        foreach (var (n, parents) in composition.Up ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            foreach (var p in parents ?? [])
            {
                if (!_children.TryGetValue(p, out var list))
                {
                    list = [];
                    _children[p] = list;
                }

                list.Add(n);
            }
        }

        HasBindings = _members.Values.Any(m => m.Member.Bound || m.Nodes.Count > 0);

        // Package spec §7.4: each slot, and its editions that resolved, matched their pin and are valid (direct members).
        var direct = new Dictionary<string, CompositionMember>(StringComparer.Ordinal);
        foreach (var (m, _) in _members.Values)
        {
            if (m.Direct)
            {
                direct.TryAdd(m.Id, m);
            }
        }

        foreach (var slot in composition.Slots ?? [])
        {
            if (slot?.Iri is null)
            {
                continue;
            }

            var editions = (slot.Editions ?? []).Where(e => e is not null).ToList();
            var available = editions.Where(direct.ContainsKey).Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, id => direct[id], StringComparer.Ordinal);
            _slots.Add((slot with { Editions = editions }, available));
        }
    }

    private readonly List<(EditionSlot Slot, Dictionary<string, CompositionMember> Available)> _slots = [];

    /// <summary>Whether the composing package has edition groups (package spec §7.4).</summary>
    public bool HasSlots => _slots.Count > 0;

    /// <summary>
    /// Package spec §7.4 and Reader contract §9.2 step 5.4: whether this composer's edition groups keep a record for a
    /// locale. A record from one or more of a slot's editions, or their members at any depth, passes only when the
    /// edition the slot chooses is one of them. A record from no slot's editions is not affected.
    /// </summary>
    public bool KeepsEdition(CitationRecord record, IReadOnlyList<string>? locale)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_slots.Count == 0)
        {
            return true;
        }

        var under = Under(record.Package.Digest);
        if (under.Count == 0)
        {
            return true;
        }

        foreach (var (slot, available) in _slots)
        {
            var holding = available.Where(kv => under.Contains(kv.Value.Digest)).Select(kv => kv.Key).ToList();
            if (holding.Count == 0)
            {
                continue;
            }

            var languages = available.ToDictionary(kv => kv.Key, kv => kv.Value.Language, StringComparer.Ordinal);
            if (Editions.Choose(slot, languages, locale) is not { } chosen || !holding.Contains(chosen, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The digests of the members a member is under, itself included, at any depth in this composition.</summary>
    private HashSet<string> Under(string digest)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([digest]);
        while (stack.TryPop(out var d))
        {
            if (!_members.TryGetValue(d, out var m) || !output.Add(d))
            {
                continue;
            }

            foreach (var p in m.Member.Parents ?? [])
            {
                stack.Push(p);
            }
        }

        return output;
    }

    /// <summary>The composing package's digest.</summary>
    public string Digest { get; }

    /// <summary>Whether any member is bound to a concept.</summary>
    public bool HasBindings { get; }

    /// <summary>The digests of the members it holds.</summary>
    public IEnumerable<string> MemberDigests => _members.Keys;

    /// <summary>Whether this composer binds a record to a concept in <paramref name="wanted"/>, directly or as an
    /// ancestor of its binding.</summary>
    public bool Binds(CitationRecord record, IReadOnlySet<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(wanted);
        if (!_members.TryGetValue(record.Package.Digest, out var m))
        {
            return false;
        }

        var (below, covered) = Reached(wanted);
        return covered.Contains(m.Member.Digest)
            || (m.Nodes.Contains(record.Node.Path) && below.Contains($"{m.Member.Id}#{record.Node.Path}"));
    }

    /// <summary>The concepts at or below <paramref name="wanted"/> in this composer's links, and the members covered
    /// (a bound member id among them, and every member it composes). Computed once per set.</summary>
    private (HashSet<string> Below, HashSet<string> Covered) Reached(IReadOnlySet<string> wanted)
    {
        if (_reach.TryGetValue(wanted, out var hit))
        {
            return hit;
        }

        var below = new HashSet<string>(wanted, StringComparer.Ordinal);
        var stack = new Stack<string>(wanted);
        while (stack.Count > 0)
        {
            foreach (var c in _children.GetValueOrDefault(stack.Pop()) ?? [])
            {
                if (below.Add(c))
                {
                    stack.Push(c);
                }
            }
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Stack<string>(_members.Values.Where(m => m.Member.Bound && below.Contains(m.Member.Id)).Select(m => m.Member.Digest));
        while (queue.Count > 0)
        {
            var d = queue.Pop();
            if (!covered.Add(d))
            {
                continue;
            }

            foreach (var c in _memberChildren.GetValueOrDefault(d) ?? [])
            {
                queue.Push(c);
            }
        }

        _reach[wanted] = (below, covered);
        return (below, covered);
    }
}

/// <summary>The loaded composing packages, by the digests of the members they hold.</summary>
public sealed class CompositionIndex
{
    private readonly Dictionary<string, List<Composer>> _byMember = new(StringComparer.Ordinal);

    /// <summary>Indexes composing packages: results through <see cref="Composition.Of"/>, or package states.</summary>
    public CompositionIndex(IEnumerable<(string Digest, CompositionState? Composition)> composers)
    {
        ArgumentNullException.ThrowIfNull(composers);
        foreach (var (digest, composition) in composers)
        {
            if (digest is null || composition is null)
            {
                continue;
            }

            var composer = new Composer(digest, composition);
            HasBindings |= composer.HasBindings;
            HasSlots |= composer.HasSlots;
            foreach (var d in composer.MemberDigests)
            {
                if (!_byMember.TryGetValue(d, out var list))
                {
                    list = [];
                    _byMember[d] = list;
                }

                list.Add(composer);
            }
        }
    }

    /// <summary>Whether any loaded composing package binds a member to a concept (contract §9.5).</summary>
    public bool HasBindings { get; }

    /// <summary>Whether any loaded composing package has edition groups (package spec §7.4).</summary>
    public bool HasSlots { get; }

    /// <summary>The composing packages that hold a member.</summary>
    public IReadOnlyList<Composer> ComposersOf(string digest) => _byMember.GetValueOrDefault(digest) ?? [];

    /// <summary>Whether a record is bound to a concept in <paramref name="wanted"/>: by its node's own concepts, or by
    /// composition.</summary>
    public bool BoundTo(CitationRecord record, IReadOnlySet<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(wanted);
        return (record.Concepts ?? []).Any(wanted.Contains) || ComposersOf(record.Package.Digest).Any(k => k.Binds(record, wanted));
    }
}
