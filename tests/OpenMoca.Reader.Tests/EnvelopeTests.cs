using System.IO.Compression;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Age;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Encryption;
using OpenMoca.Reader.Sidecars;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Encrypted packages: what the corpus cannot show. The same tests as moca-reader-typescript's test/envelope.test.js.</summary>
public sealed partial class EnvelopeTests
{
    private static string Envelopes => Path.Combine(MocaSpec.Conformance, "fixtures", "envelopes");

    private static string Fixture(string name) => Path.Combine(Envelopes, name);

    private static List<string> IdentitiesIn(string file) =>
        File.ReadAllLines(file).Where(l => l.StartsWith("AGE-SECRET-KEY-1", StringComparison.Ordinal)).ToList();

    private static ReadOptions WithKey(string keyFile) => new()
    {
        EnvelopeDecryptor = AgeEnvelopeDecryptor.Instance,
        KeyProvider = (_, _) => ValueTask.FromResult<EnvelopeKey?>(new EnvelopeKey(Identities: IdentitiesIn(keyFile))),
    };

    private static string TestKey => Fixture(Path.Combine("keys", "INSECURE-test.txt"));

    [Fact]
    public async Task NoDecryptor_RecognisesAndDoesNotOpen()
    {
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.moca.enc"), WithKey(TestKey) with { EnvelopeDecryptor = null }, TestContext.Current.CancellationToken);
        Assert.False(r.Valid);
        Assert.Equal([DiagnosticCodes.E001EncryptedNoKey], r.Diagnostics.Select(d => d.Code));
        Assert.False(r.Envelope!.Opened);
        Assert.Equal("moca-enc-v1", r.Envelope.Format);
        Assert.Equal("Fixture", r.Envelope.Outline?["title"]?.GetValue<string>());
        Assert.Null(r.Manifest);
        Assert.Null(r.Digest);
        Assert.Empty(r.Nodes);
    }

    [Fact]
    public async Task NoDecryptor_EntryChecksStillRun()
    {
        var r = await MocaReader.ReadPackageAsync(Fixture("extra-entry.moca.enc"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.E005EnvelopeInvalid], r.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task Bytes_OpenLikeAPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(File.ReadAllBytes(Fixture("x25519.moca.enc"))), WithKey(TestKey), ct);
        var b = await MocaReader.ReadPackageAsync(Fixture("x25519.moca.enc"), WithKey(TestKey), ct);
        Assert.True(a.Valid);
        Assert.Equal(b.Digest, a.Digest);
        Assert.Equal(new EnvelopeInfo("moca-enc-v1", true), a.Envelope);
    }

    [Fact]
    public async Task ThrowingKeyProvider_IsE001_NeverThrown()
    {
        var options = WithKey(TestKey) with { KeyProvider = (_, _) => throw new InvalidOperationException("vault offline") };
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.moca.enc"), options, TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.E001EncryptedNoKey], r.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task KeyProvider_GetsStanzasOutlineAndFile()
    {
        EnvelopeKeyRequest? seen = null;
        var options = WithKey(TestKey) with { KeyProvider = (req, _) => { seen = req; return ValueTask.FromResult<EnvelopeKey?>(null); } };
        await MocaReader.ReadPackageAsync(Fixture("x25519.moca.enc"), options, TestContext.Current.CancellationToken);
        Assert.Equal("package", seen!.File);
        Assert.Equal("X25519", seen.Stanzas[0].Type);
        Assert.Equal(32, seen.Stanzas[0].Body.Length);
        Assert.Equal("Fixture", seen.Outline?["title"]?.GetValue<string>());
    }

    /// <summary>Encrypted packages §5.4: decrypted bytes never go to a file; the Reader needs no writable temp directory.</summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task OpensWithAnUnwritableTempDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX permissions only");
        var locked = Directory.CreateTempSubdirectory("moca-locked-").FullName;
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var saved = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMPDIR", locked);
        try
        {
            Assert.StartsWith(locked, Path.GetTempPath(), StringComparison.Ordinal);
            var r = await MocaReader.ReadPackageAsync(Fixture("large.moca.enc"), WithKey(TestKey), TestContext.Current.CancellationToken);
            Assert.True(r.Valid);
            Assert.True(r.Envelope!.Opened);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", saved);
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(locked, true);
        }
    }

    [GeneratedRegex(@"public key: (age1\w+)")]
    private static partial Regex PublicKey();

    /// <summary>Encrypted packages §6: index.age, encrypted at test time with the Go age CLI.</summary>
    [Fact]
    public async Task IndexAge_BindsThroughTheKeyProvider()
    {
        Assert.SkipUnless(AgeTests.Run("age", ["--version"], [], null).Code == 0, "the Go age CLI is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var sidecar = new MemoryStream();
        ZipFile.CreateFromDirectory(Path.Combine(MocaSpec.Conformance, "fixtures", "sidecars", "valid"), sidecar);
        var recipient = PublicKey().Match(File.ReadAllText(TestKey)).Groups[1].Value;
        var indexAge = AgeTests.Run("age", ["-r", recipient], sidecar.ToArray(), null).Stdout;

        var dir = Directory.CreateTempSubdirectory("moca-index-age-").FullName;
        var path = Path.Combine(dir, "with-index.moca.enc");
        File.Copy(Fixture("x25519.moca.enc"), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var s = zip.CreateEntry("index.age", CompressionLevel.NoCompression).Open();
            s.Write(indexAge);
        }

        try
        {
            var pkg = await MocaReader.ReadPackageAsync(path, WithKey(TestKey), ct);
            Assert.True(pkg.Valid);
            var bound = await SidecarBinder.BindEnvelopeAsync(path, pkg, WithKey(TestKey), ct);
            Assert.True(bound.Usable);
            Assert.Empty(bound.Diagnostics);
            var wrong = await SidecarBinder.BindEnvelopeAsync(path, pkg, WithKey(Fixture(Path.Combine("keys", "INSECURE-other.txt"))), ct);
            Assert.False(wrong.Usable);
            Assert.Equal([DiagnosticCodes.E002DecryptionFailed], wrong.Diagnostics.Select(d => d.Code));
            Assert.True(pkg.Valid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
