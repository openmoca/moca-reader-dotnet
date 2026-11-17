using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Package spec §4.6–4.8: images, translations and tags. The corpus covers the codes; these cover what it cannot.
/// </summary>
public sealed class CatalogueTests : IDisposable
{
    private const string Node = "---\ntype: Note\ntitle: A\n---\n\n# A\n\nText.\n";
    private static readonly byte[] Png = Convert.FromHexString("89504e470d0a1a0a0000000d49484452");
    private static readonly byte[] Jpeg = Convert.FromHexString("ffd8ffe000104a464946");
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8L"u8];

    private readonly string _dir = Directory.CreateTempSubdirectory("moca-catalogue-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Package(string extra, params (string Path, byte[] Bytes)[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "moca.json"), $$"""{ "id": "https://example.com/t", "version": "1.0.0", "title": "T"{{extra}} }""");
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        File.WriteAllText(Path.Combine(_dir, "content", "a.md"), Node);
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(_dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return _dir;
    }

    [Fact]
    public async Task EachImageType_IsCheckedByItsOwnSignatureOnly()
    {
        var dir = Package("""
            , "images": [
              { "src": "media/a.png", "role": "icon", "type": "image/png" },
              { "src": "media/a.jpg", "role": "cover", "type": "image/jpeg" },
              { "src": "media/a.webp", "role": "cover", "type": "image/webp" },
              { "src": "media/b.webp", "role": "cover", "type": "image/webp" } ]
            """, ("media/a.png", Png), ("media/a.jpg", Jpeg), ("media/a.webp", Webp), ("media/b.webp", Png));

        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.M010ImageInvalid], result.Diagnostics.Select(d => d.Code));
        Assert.Equal(["media/a.png", "media/a.jpg", "media/a.webp"], result.Images!.Select(i => i.Src));
    }

    [Fact]
    public async Task AnImageAtTheLimit_Passes_AndOneByteOver_DoesNot()
    {
        var dir = Package(""", "images": [{ "src": "media/a.png", "role": "icon", "type": "image/png" }]""", ("media/a.png", Png));

        var at = await MocaReader.ReadPackageAsync(dir, new ReadOptions { Limits = new ReaderLimits { MaxImageBytes = Png.Length } }, TestContext.Current.CancellationToken);
        var over = await MocaReader.ReadPackageAsync(dir, new ReadOptions { Limits = new ReaderLimits { MaxImageBytes = Png.Length - 1 } }, TestContext.Current.CancellationToken);

        Assert.Empty(at.Diagnostics);
        Assert.Equal([DiagnosticCodes.M010ImageInvalid], over.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task AnImageUnderAHiddenDirectory_IsMissing()
    {
        var dir = Package(""", "images": [{ "src": "media/.hidden/a.png", "role": "icon", "type": "image/png" }]""", ("media/.hidden/a.png", Png));

        var result = await MocaReader.ReadPackageAsync(dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([DiagnosticCodes.M010ImageInvalid], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task TheFields_AreNull_WhenAbsent_AndWhenThePackageIsInvalid()
    {
        var none = await MocaReader.ReadPackageAsync(Package(""), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(none.Images);
        Assert.Null(none.Translations);
        Assert.Null(none.Tags);

        var invalid = await MocaReader.ReadPackageAsync(Package(""", "tags": ["a"]""", ("content/b.md", "no frontmatter\n"u8.ToArray())), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(invalid.Valid);
        Assert.Null(invalid.Tags);
    }
}
