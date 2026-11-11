namespace OpenMoca.Reader;

/// <summary>
/// Bounds on the work a Reader does with untrusted input (Reader contract §3). The defaults are the
/// reference implementation's; every limit is configurable.
/// </summary>
public sealed record ReaderLimits
{
    /// <summary>The default limits.</summary>
    public static ReaderLimits Default { get; } = new();

    /// <summary>Most entries an archive or host source may list (T003 beyond it).</summary>
    public int MaxEntries { get; init; } = 20_000;

    /// <summary>Most total uncompressed bytes an archive or host source may hold (T003 beyond it).</summary>
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Largest frontmatter block, in bytes (C002 beyond it).</summary>
    public int MaxFrontmatterBytes { get; init; } = 64 * 1024;

    /// <summary>Largest structure or overlay file, in bytes (O001 beyond it).</summary>
    public int MaxStructureBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Most triples in a structure or overlay file (O001 beyond it).</summary>
    public int MaxTriples { get; init; } = 100_000;

    /// <summary>Evidence entries checked per node; later entries are not checked and <c>matched</c> is absent.</summary>
    public int MaxEvidencePerNode { get; init; } = 256;

    /// <summary>Largest source file checked for evidence; larger files are not checked.</summary>
    public int MaxSourceBytesChecked { get; init; } = 8 * 1024 * 1024;

    /// <summary>Evidence entries checked across a whole package (contract §3).</summary>
    public int MaxEvidenceChecksPerPackage { get; init; } = 10_000;

    /// <summary>Source bytes scanned for evidence across a whole package, counted once per check (contract §3).</summary>
    public long MaxEvidenceBytesPerPackage { get; init; } = 256L * 1024 * 1024;

    /// <summary>How deep members may nest; a member past it is not resolved (package spec §7.1, contract §3).</summary>
    public int MaxMemberDepth { get; init; } = 8;

    /// <summary>Members resolved in one read, at every depth; further members are not resolved (package spec §7.1, contract §3).</summary>
    public int MaxMembers { get; init; } = 256;
}
