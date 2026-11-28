using System.Text.Json.Nodes;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Json;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Reader.Search;

/// <summary>
/// The package states a store search may use (Reader contract §9.4), supplied on demand by the host. A search asks only
/// for the states of the packages its candidates come from, the packages that compose them, and, with a terms request,
/// the packages their records name; everything a search needs about the whole store comes from the cached
/// <see cref="Index"/>. So a search does not hold every package's state in memory.
/// </summary>
/// <remarks>
/// The host keeps the allowlist and the index, and rebuilds the index (<see cref="StoreIndex.Build"/>) when a state is
/// added, replaced or removed. A search reads them once at its start; changing them while a search runs is the host's
/// own race.
/// </remarks>
public interface IPackageStateSource
{
    /// <summary>
    /// The digests a search may return records from: the allowlist (Reader contract §9.2 step 5.1). Handed to the backend
    /// as its digest filter as it is, never copied.
    /// </summary>
    IReadOnlyList<string> Digests { get; }

    /// <summary>Whether a digest is on the allowlist.</summary>
    bool Contains(string digest);

    /// <summary>The store-wide facts a search needs, cached by the host: built from the same states.</summary>
    StoreIndex Index { get; }

    /// <summary>
    /// The states of the given digests. A digest that is not on the allowlist, or has no state, is left out. Never
    /// called with an empty set.
    /// </summary>
    ValueTask<IReadOnlyList<PackageState>> LoadAsync(IReadOnlyCollection<string> digests, CancellationToken cancellationToken);
}

/// <summary>
/// The store-wide facts a store search needs, built once from the package states and kept by the host (Reader contract
/// §9.4): the relation index (package spec §7.2), which packages compose which, whether any binds a member to a
/// concept, the concepts on any reading order, and each package by id. It holds no reviews, terms, contests or
/// structure, so it is much smaller than the states.
/// </summary>
public sealed class StoreIndex
{
    private readonly Dictionary<string, List<string>> _composersByMember;
    private readonly HashSet<string> _placed;
    private readonly Dictionary<string, List<(string Version, string Digest)>> _byId;
    private readonly Dictionary<string, int> _order;

    private StoreIndex(RelationIndex relations, bool allowUnsignedRelations, bool hasBindings, Dictionary<string, List<string>> composersByMember,
        HashSet<string> placed, Dictionary<string, List<(string, string)>> byId, Dictionary<string, int> order)
    {
        Relations = relations;
        AllowUnsignedRelations = allowUnsignedRelations;
        HasBindings = hasBindings;
        _composersByMember = composersByMember;
        _placed = placed;
        _byId = byId;
        _order = order;
    }

    /// <summary>Package-level relation effects among the indexed packages, as <see cref="PackageState.RelationIndexOf"/>.</summary>
    public RelationIndex Relations { get; }

    /// <summary>Whether relations from unsigned packages took effect when the index was built.</summary>
    public bool AllowUnsignedRelations { get; }

    /// <summary>Whether any indexed package binds a member to a concept (Reader contract §9.5).</summary>
    public bool HasBindings { get; }

    /// <summary>How many packages are indexed.</summary>
    public int Count => _order.Count;

    /// <summary>
    /// Builds the index from every state, reading each once and keeping none: relations a signer may make (or every
    /// relation, when <paramref name="allowUnsignedRelations"/>), who composes whom, and the reading orders' concepts.
    /// The order of <paramref name="states"/> is the order a search lists composers and relation effects in.
    /// </summary>
    /// <exception cref="ArgumentException">Two states have the same digest.</exception>
    public static StoreIndex Build(IEnumerable<PackageState> states, bool allowUnsignedRelations)
    {
        ArgumentNullException.ThrowIfNull(states);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var byId = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        var relations = new List<(string Self, string Type, string? TargetId, string? TargetVersion)>();
        var composersByMember = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var hasBindings = false;
        foreach (var state in states)
        {
            ArgumentNullException.ThrowIfNull(state, nameof(states));
            if (!order.TryAdd(state.Digest, order.Count))
            {
                throw new ArgumentException($"two package states have the digest {state.Digest}", nameof(states));
            }

            if (!byId.TryGetValue(state.Id, out var versions))
            {
                versions = [];
                byId[state.Id] = versions;
            }

            versions.Add((state.Version, state.Digest));
            foreach (var relation in state.Relations.OfType<JsonObject>())
            {
                var targetId = JsonValues.StringOf(relation["target"]);
                // Package spec §7.2: only relations a signer of the package may make take effect.
                if (!allowUnsignedRelations && !TrustRoot.ScopeCovers(state.RelationScopes, targetId))
                {
                    continue;
                }

                relations.Add(($"{state.Id}@{state.Version}", JsonValues.StringOf(relation["type"]) ?? "", targetId, JsonValues.StringOf(relation["version"])));
            }

            if (state.Composition is { } composition)
            {
                var composer = new Composer(state.Digest, composition);
                hasBindings |= composer.HasBindings;
                foreach (var member in composer.MemberDigests)
                {
                    if (!composersByMember.TryGetValue(member, out var list))
                    {
                        list = [];
                        composersByMember[member] = list;
                    }

                    list.Add(state.Digest);
                }
            }

            foreach (var concept in state.ReadingOrder?.Positions.Keys ?? [])
            {
                placed.Add(concept);
            }
        }

        return new StoreIndex(RelationsOf(relations, byId), allowUnsignedRelations, hasBindings, composersByMember, placed, byId, order);
    }

    // The same effects, in the same order, as the scan over every pair of packages it replaces.
    internal static RelationIndex RelationsOf(List<(string Self, string Type, string? TargetId, string? TargetVersion)> relations,
        Dictionary<string, List<(string Version, string Digest)>> byId)
    {
        var superseded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var contested = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Push(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        foreach (var (self, type, targetId, targetVersion) in relations)
        {
            if (targetId is null || !byId.TryGetValue(targetId, out var targets))
            {
                continue;
            }

            foreach (var (version, _) in targets)
            {
                if (!string.IsNullOrEmpty(targetVersion) && version != targetVersion)
                {
                    continue;
                }

                var key = $"{targetId}@{version}";
                if (type == "supersedes")
                {
                    Push(superseded, key, self);
                }

                if (type == "conflictsWith")
                {
                    Push(contested, key, self);
                    Push(contested, self, key);
                }
            }
        }

        return new RelationIndex(superseded, contested);
    }

    /// <summary>The digests of the indexed packages that compose a member, at any depth.</summary>
    internal IReadOnlyList<string> ComposersOf(string digest) => _composersByMember.GetValueOrDefault(digest) ?? [];

    /// <summary>Whether any indexed package's reading order places a concept (for <c>S008</c>).</summary>
    internal bool Places(string concept) => _placed.Contains(concept);

    /// <summary>Where a digest was in the states the index was built from; past the end when it was not indexed.</summary>
    internal int OrderOf(string digest) => _order.TryGetValue(digest, out var i) ? i : int.MaxValue;

    /// <summary>
    /// The digests of the packages a reference names (Reader contract §15.4, "Nothing names a hidden package"): a
    /// package id or id@version, alone or before <c>#</c>.
    /// </summary>
    internal IEnumerable<string> DigestsNamed(string reference)
    {
        for (var i = 1; i <= reference.Length; i++)
        {
            if (i < reference.Length && reference[i] is not ('@' or '#'))
            {
                continue;
            }

            if (!_byId.TryGetValue(reference[..i], out var versions))
            {
                continue;
            }

            var id = reference[..i];
            foreach (var (version, digest) in versions)
            {
                var key = $"{id}@{version}";
                if (reference == id || reference.StartsWith(id + "#", StringComparison.Ordinal)
                    || reference == key || reference.StartsWith(key + "#", StringComparison.Ordinal))
                {
                    yield return digest;
                }
            }
        }
    }
}

/// <summary>
/// Package states the host holds in memory, as an <see cref="IPackageStateSource"/>: what the store search's
/// <see cref="MocaSearch(IEnumerable{PackageState}, Func{DateTimeOffset}, ISearchBackend, IReadOnlyList{string}?, SearchHooks?, int, bool)"/>
/// constructor uses. Build it once and share it between searches so the index is built once.
/// </summary>
public sealed class MemoryPackageStates : IPackageStateSource
{
    private readonly Dictionary<string, PackageState> _states;
    private readonly List<string> _digests;

    /// <summary>Holds the states and builds their index.</summary>
    /// <exception cref="ArgumentException">Two states have the same digest.</exception>
    public MemoryPackageStates(IEnumerable<PackageState> states, bool allowUnsignedRelations = false)
    {
        ArgumentNullException.ThrowIfNull(states);
        var list = states.ToList();
        Index = StoreIndex.Build(list, allowUnsignedRelations);
        _states = list.ToDictionary(s => s.Digest, StringComparer.Ordinal);
        _digests = [.. _states.Keys];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Digests => _digests;

    /// <inheritdoc/>
    public StoreIndex Index { get; }

    /// <inheritdoc/>
    public bool Contains(string digest) => _states.ContainsKey(digest);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<PackageState>> LoadAsync(IReadOnlyCollection<string> digests, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Load(digests));

    internal IReadOnlyList<PackageState> Load(IReadOnlyCollection<string> digests)
    {
        ArgumentNullException.ThrowIfNull(digests);
        return [.. digests.Select(d => _states.GetValueOrDefault(d)).OfType<PackageState>()];
    }
}
