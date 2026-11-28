using System.Collections;
using System.Runtime.CompilerServices;

namespace OpenMoca.Reader;

/// <summary>
/// A request for a locale (package spec §5.4): a priority list of language tags, most preferred first. One tag is a
/// list of one, so <c>Locale = "fr"</c> and <c>Locale = ["fr-CA", "en"]</c> both work. Empty entries are dropped; an
/// empty list, like a null or empty string, is no request. Immutable.
/// </summary>
[CollectionBuilder(typeof(LocaleRequest), nameof(Create))]
public sealed class LocaleRequest : IReadOnlyList<string>
{
    private readonly string[] _tags;

    private LocaleRequest(IEnumerable<string?> tags) =>
        _tags = [.. tags.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)];

    /// <summary>The number of tags.</summary>
    public int Count => _tags.Length;

    /// <summary>Whether this is no request: it has no tag.</summary>
    public bool IsEmpty => _tags.Length == 0;

    /// <summary>The tag at a position in the list.</summary>
    /// <param name="index">The position, most preferred first.</param>
    public string this[int index] => _tags[index];

    /// <summary>A request from tags in priority order, for a collection expression.</summary>
    /// <param name="tags">The tags, most preferred first.</param>
    public static LocaleRequest Create(ReadOnlySpan<string> tags) => new(tags.ToArray());

    /// <summary>A request from tags in priority order; null when <paramref name="tags"/> is null.</summary>
    /// <param name="tags">The tags, most preferred first.</param>
    public static LocaleRequest? From(IEnumerable<string?>? tags) => tags is null ? null : new LocaleRequest(tags);

    /// <summary>One tag as a list of one; null for a null or empty string, which is no request.</summary>
    /// <param name="tag">A language tag.</param>
    public static implicit operator LocaleRequest?(string? tag) => string.IsNullOrEmpty(tag) ? null : new LocaleRequest([tag]);

    /// <summary>Tags in priority order; null for a null array.</summary>
    /// <param name="tags">The tags, most preferred first.</param>
    public static implicit operator LocaleRequest?(string[]? tags) => From(tags);

    /// <summary>One tag as a list of one, as the implicit conversion does.</summary>
    /// <param name="tag">A language tag.</param>
    public static LocaleRequest? FromString(string? tag) => tag;

    /// <summary>Tags in priority order, as the implicit conversion does.</summary>
    /// <param name="tags">The tags, most preferred first.</param>
    public static LocaleRequest? FromStringArray(string[]? tags) => tags;

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_tags).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The tags, comma-separated.</summary>
    public override string ToString() => string.Join(", ", _tags);
}
