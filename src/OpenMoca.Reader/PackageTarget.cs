using OpenMoca.Reader.Sources;

namespace OpenMoca.Reader;

/// <summary>What to read: a directory, a <c>.moca</c> archive (path or bytes), or a host package source.</summary>
public abstract record PackageTarget
{
    private PackageTarget()
    {
    }

    /// <summary>A directory, or a <c>.moca</c> / <c>.zip</c> archive path.</summary>
    public static PackageTarget FromPath(string path) => new PathTarget(path);

    /// <summary>A <c>.moca</c> archive held in memory.</summary>
    public static PackageTarget FromArchive(byte[] archive) => new ArchiveTarget(archive);

    /// <summary>A host-supplied source; the Reader applies its safety rules and limits to it.</summary>
    public static PackageTarget FromSource(IPackageSource source) => new SourceTarget(source);

    /// <summary>A path target.</summary>
    public static implicit operator PackageTarget(string path) => FromPath(path);

    internal abstract IPackageSource Open(ReaderLimits limits);

    private sealed record PathTarget(string Path) : PackageTarget
    {
        internal override IPackageSource Open(ReaderLimits limits) => PackageSources.Open(Path, limits);
    }

    private sealed record ArchiveTarget(byte[] Archive) : PackageTarget
    {
        internal override IPackageSource Open(ReaderLimits limits) => PackageSources.OpenArchive(Archive, limits);
    }

    private sealed record SourceTarget(IPackageSource Source) : PackageTarget
    {
        internal override IPackageSource Open(ReaderLimits limits) => PackageSources.Checked(Source, limits);
    }
}
