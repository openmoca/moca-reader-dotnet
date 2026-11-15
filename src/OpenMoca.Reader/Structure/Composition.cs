using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OpenMoca.Reader.Citations;

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

        var entries = new Dictionary<string, (string Id, bool Bound, List<string> Nodes, SortedSet<string> Parents)>(StringComparer.Ordinal);
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
                    entry = (child.Id, Declared(child.Id), nodes, new SortedSet<string>(StringComparer.Ordinal));
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

            var parents = graph.Out("broader", n).Concat(graph.In("hasPart", n)).Select(e => e.Iri)
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
                Bound = kv.Value.Bound,
                Nodes = kv.Value.Nodes,
                Parents = [.. kv.Value.Parents],
            })],
            Up = up.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
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
