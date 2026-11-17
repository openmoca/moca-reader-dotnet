using OpenMoca.Reader.Citations;

namespace OpenMoca.Reader.Content;

/// <summary>
/// Package spec §5.7, "Alternates": media descriptions of one node that carry the same content on timelines that line
/// up stretch by stretch. Each entry of <c>sync</c> starts a stretch of the reference, from its <c>at</c> up to the next
/// entry's <c>at</c>; the last runs to the end. A stretch with an offset is on the alternate at <c>t + offset</c>; one
/// whose offset is null is not in the alternate.
/// </summary>
public static class Alternates
{
    private sealed record Stretch(double From, double To, double? Offset)
    {
        public double Lo => From + (Offset ?? 0);

        public double Hi => To + (Offset ?? 0);
    }

    /// <summary>The sync of an alternate that gives none: the reference's timeline unchanged.</summary>
    public static IReadOnlyList<SyncEntry> DefaultSync { get; } = [new SyncEntry(0, 0)];

    private static List<Stretch> Stretches(IReadOnlyList<SyncEntry> sync) =>
        [.. sync.Select((e, i) => new Stretch(e.At, i + 1 < sync.Count ? sync[i + 1].At : double.PositiveInfinity, e.Offset))];

    /// <summary>Why a sync list cannot be used (<c>at</c> out of order, or stretches that overlap on the alternate), or null.</summary>
    public static string? SyncProblem(IReadOnlyList<SyncEntry> sync)
    {
        for (var i = 1; i < sync.Count; i++)
        {
            if (!(sync[i].At > sync[i - 1].At))
            {
                return "its \"at\" values are not in strictly increasing order";
            }
        }

        var mapped = Stretches(sync).Where(s => s.Offset is not null).OrderBy(s => s.Lo).ToList();
        for (var i = 1; i < mapped.Count; i++)
        {
            if (mapped[i - 1].Hi > mapped[i].Lo)
            {
                return "two of its stretches overlap on the alternate's timeline";
            }
        }

        return null;
    }

    /// <summary>A moment on the reference, on the alternate; null when its stretch is not in the alternate.</summary>
    public static double? ToAlternate(IReadOnlyList<SyncEntry> sync, double t)
    {
        var s = Stretches(sync).LastOrDefault(x => x.From <= t);
        return s is { Offset: { } offset } && t < s.To ? t + offset : null;
    }

    /// <summary>A moment on the alternate, on the reference; null when it lies in no stretch.</summary>
    public static double? ToReference(IReadOnlyList<SyncEntry> sync, double t)
    {
        var s = Stretches(sync).FirstOrDefault(x => x.Offset is not null && x.Lo <= t && t < x.Hi);
        return s is { Offset: { } offset } ? t - offset : null;
    }

    /// <summary>
    /// A time range on the alternate, on the reference: it maps when its start and end lie in one stretch (the end may
    /// equal the stretch's last moment). A range with no end keeps none. Null when it does not map.
    /// </summary>
    public static (double Start, double End)? RangeToReference(IReadOnlyList<SyncEntry> sync, (double Start, double End) range)
    {
        var s = Stretches(sync).FirstOrDefault(x => x.Offset is not null && x.Lo <= range.Start && range.Start < x.Hi);
        if (s is not { Offset: { } offset })
        {
            return null;
        }

        // A range with no end runs to the end of its stretch on the reference.
        if (double.IsPositiveInfinity(range.End))
        {
            return (range.Start - offset, s.To);
        }

        return range.End <= s.Hi ? (range.Start - offset, range.End - offset) : null;
    }

    private static bool Within(double t, CitationMedia d) =>
        double.IsFinite(t) && t >= 0 && (d.Duration is not { } duration || t <= duration);

    /// <summary>
    /// Reader interface §4: the same moment on another media description of the record's node, or null when it is not
    /// there. A description maps a moment on its own timeline to itself; two alternates of one reference map through it.
    /// </summary>
    /// <param name="record">A citation record.</param>
    /// <param name="from">The <c>source</c> of the description the moment is on.</param>
    /// <param name="to">The <c>source</c> of the description to map it to.</param>
    /// <param name="t">The moment, in seconds.</param>
    public static double? MapTime(CitationRecord record, string from, string to, double t)
    {
        ArgumentNullException.ThrowIfNull(record);
        var media = record.Node.Media ?? [];
        var a = media.FirstOrDefault(d => d.Source == from);
        var b = media.FirstOrDefault(d => d.Source == to);
        if (a is null || b is null || !Within(t, a))
        {
            return null;
        }

        if (ReferenceEquals(a, b))
        {
            return t;
        }

        var refId = a.AlternateOf ?? a.Source;
        if ((b.AlternateOf ?? b.Source) != refId || media.FirstOrDefault(d => d.Source == refId) is not { } reference)
        {
            return null;
        }

        var onRef = a.AlternateOf is null ? t : ToReference(a.Sync ?? DefaultSync, t);
        if (onRef is not { } r || !Within(r, reference))
        {
            return null;
        }

        var output = b.AlternateOf is null ? r : ToAlternate(b.Sync ?? DefaultSync, r);
        return output is { } o && Within(o, b) ? o : null;
    }
}
