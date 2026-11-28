using System.Diagnostics;

namespace OpenMoca.Reader;

/// <summary>
/// The Reader's OpenTelemetry spans, through <see cref="ActivitySource"/> (part of .NET, so no package). A host that
/// traces adds this source by name (with OpenTelemetry's <c>AddSource</c>). Spans carry counts, outcomes and
/// durations, and never package content: no text, no manifest fields beyond the package id and version, no keys.
/// </summary>
public static class ReaderTelemetry
{
    /// <summary>The name of the Reader's activity source.</summary>
    public const string SourceName = "OpenMoca.Reader";

    /// <summary>The span for <see cref="PackageTrust.Evaluate(ReadOnlyMemory{byte}, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, byte[]}, ReadOptions?)"/>.</summary>
    public const string PackageTrustSpan = "moca.reader.package_trust";

    /// <summary>The span for one load of package states from a host's <see cref="Search.IPackageStateSource"/>.</summary>
    public const string StateLoadSpan = "moca.reader.package_states.load";

    internal static ActivitySource Source { get; } = new(SourceName, typeof(ReaderTelemetry).Assembly.GetName().Version?.ToString());
}
