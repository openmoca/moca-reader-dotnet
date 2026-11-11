namespace OpenMoca.Reader.Tests;

/// <summary>Finds the moca-spec checkout: <c>MOCA_SPEC_DIR</c>, else <c>../moca-spec</c> next to this repository.</summary>
internal static class MocaSpec
{
    public static string Root { get; } = Find();

    public static string Conformance => Path.Combine(Root, "conformance");

    public static string Examples => Path.Combine(Root, "examples");

    private static string Find()
    {
        var configured = Environment.GetEnvironmentVariable("MOCA_SPEC_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "moca-reader-dotnet.slnx")))
            {
                return Path.GetFullPath(Path.Combine(dir.FullName, "..", "moca-spec"));
            }
        }

        throw new InvalidOperationException("Cannot find moca-spec. Clone it next to this repository or set MOCA_SPEC_DIR.");
    }
}
