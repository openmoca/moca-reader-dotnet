namespace OpenMoca.Reader.Diagnostics;

/// <summary>Collects diagnostics, applying strict mode (warnings promoted to errors, codes unchanged).</summary>
public sealed class DiagnosticBag(bool strict = false)
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>The diagnostics collected so far, in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>Records a diagnostic with the code's default severity (or error, in strict mode, for a warning).</summary>
    public Diagnostic Add(string code, string message, string? file = null, int? line = null)
    {
        var severity = DiagnosticCodes.DefaultSeverity(code);
        if (strict && severity == DiagnosticSeverity.Warning)
        {
            severity = DiagnosticSeverity.Error;
        }

        var diagnostic = new Diagnostic(code, severity, message, file, line);
        _items.Add(diagnostic);
        return diagnostic;
    }

    /// <summary>True when any diagnostic with this code has been reported.</summary>
    public bool Has(string code) => _items.Exists(d => d.Code == code);

    /// <summary>The number of diagnostics reported so far; use with <see cref="Since"/> to scope a check.</summary>
    public int Count => _items.Count;

    /// <summary>The diagnostics reported after the first <paramref name="count"/>.</summary>
    public IEnumerable<Diagnostic> Since(int count) => _items.Skip(count);
}
