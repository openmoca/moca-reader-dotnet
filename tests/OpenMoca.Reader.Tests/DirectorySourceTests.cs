using System.Diagnostics;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Sources;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3: a directory source reads regular files only. A named pipe is never opened (opening one waits for a
/// writer): it is listed as an entry that is not a file (P001), and the read finishes.
/// </summary>
public sealed class DirectorySourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("moca-reader-dir-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WritePackage()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        File.WriteAllText(Path.Combine(_dir, "moca.json"), """{ "id": "https://example.com/tests/dir", "version": "1.0.0", "title": "Dir" }""");
        File.WriteAllText(Path.Combine(_dir, "content", "a.md"), "---\ntype: Note\ntitle: A\n---\n\nA.\n");
    }

    private static void MakeFifo(string path)
    {
        using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [path]) { UseShellExecute = false })!;
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    [Fact]
    public void IsSpecial_ForARegularFileOrDirectory_IsFalse()
    {
        WritePackage();

        Assert.False(UnixFileType.IsSpecial(Path.Combine(_dir, "moca.json")));
        Assert.False(UnixFileType.IsSpecial(Path.Combine(_dir, "content")));
    }

    [Fact]
    public async Task ReadPackage_WhenAContentEntryIsANamedPipe_ReportsP001AndFinishes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "named pipes in a directory are a Unix case");
        WritePackage();
        var pipe = Path.Combine(_dir, "content", "pipe.md");
        MakeFifo(pipe);

        Assert.True(UnixFileType.IsSpecial(pipe));
        var result = await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.P001UnsupportedEntry);
    }

    [Fact]
    public async Task ReadPackage_WhenAnEntryIsEmpty_ReadsItWithoutOpeningIt()
    {
        WritePackage();
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "");

        var result = await MocaReader.ReadPackageAsync(_dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Valid, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.FileBytes["notes.txt"]);
    }
}
