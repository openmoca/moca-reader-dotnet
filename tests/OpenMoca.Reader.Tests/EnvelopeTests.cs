using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OpenMoca.Reader.Age;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Encryption;
using OpenMoca.Reader.Sidecars;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>Encrypted packages: what the corpus cannot show. The same tests as moca-reader-typescript's test/envelope.test.js.</summary>
/// <remarks>
/// One test points the process-wide TMPDIR at a folder that cannot be written to, so these tests run on their own,
/// never beside another class that makes a temporary folder.
/// </remarks>
[Collection(ProcessEnvironment.Name)]
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
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey) with { EnvelopeDecryptor = null }, TestContext.Current.CancellationToken);
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
        var r = await MocaReader.ReadPackageAsync(Fixture("extra-entry.mocae"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.E005EnvelopeInvalid], r.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task Bytes_OpenLikeAPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(File.ReadAllBytes(Fixture("x25519.mocae"))), WithKey(TestKey), ct);
        var b = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey), ct);
        Assert.True(a.Valid);
        Assert.Equal(b.Digest, a.Digest);
        Assert.Equal(new EnvelopeInfo("moca-enc-v1", true), a.Envelope);
    }

    [Fact]
    public async Task ThrowingKeyProvider_IsE001_NeverThrown()
    {
        var options = WithKey(TestKey) with { KeyProvider = (_, _) => throw new InvalidOperationException("vault offline") };
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), options, TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.E001EncryptedNoKey], r.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task KeyProvider_GetsStanzasOutlineAndFile()
    {
        EnvelopeKeyRequest? seen = null;
        var options = WithKey(TestKey) with { KeyProvider = (req, _) => { seen = req; return ValueTask.FromResult<EnvelopeKey?>(null); } };
        await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), options, TestContext.Current.CancellationToken);
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
            var r = await MocaReader.ReadPackageAsync(Fixture("large.mocae"), WithKey(TestKey), TestContext.Current.CancellationToken);
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
        var path = Path.Combine(dir, "with-index.mocae");
        File.Copy(Fixture("x25519.mocae"), path);
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

    [Fact]
    public async Task KeyProviderError_NeverReachesADiagnostic_OnlyItsTypeName()
    {
        var options = WithKey(TestKey) with { KeyProvider = (_, _) => throw new InvalidOperationException("vault https://vault.example/v1?token=s3cret is offline") };
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), options, TestContext.Current.CancellationToken);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(DiagnosticCodes.E001EncryptedNoKey, d.Code);
        Assert.DoesNotContain("s3cret", d.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("vault.example", d.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), d.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingDecryptor(bool inParse) : IEnvelopeDecryptor
    {
        public IEnvelopeHeader ParseHeader(byte[] file, int maxScryptWorkFactor) =>
            inParse ? throw new IndexOutOfRangeException("internal") : AgeEnvelopeDecryptor.Instance.ParseHeader(file, maxScryptWorkFactor);

        public byte[] Decrypt(byte[] file, IEnvelopeHeader header, EnvelopeKey key, long maxBytes) => throw new NullReferenceException("internal");
    }

    [Fact]
    public async Task NothingADecryptorThrows_ReachesTheCaller_E006WhileParsingE002After()
    {
        var ct = TestContext.Current.CancellationToken;
        var parse = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey) with { EnvelopeDecryptor = new ThrowingDecryptor(true) }, ct);
        Assert.Equal([DiagnosticCodes.E006AgeHeaderInvalid], parse.Diagnostics.Select(d => d.Code));
        var later = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey) with { EnvelopeDecryptor = new ThrowingDecryptor(false) }, ct);
        Assert.Equal([DiagnosticCodes.E002DecryptionFailed], later.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void WorkFactorLimitOutside1To22_IsAHostError_WhenTheLimitsAreMade()
    {
        // Refused before any read starts, so no read, of an envelope or not, ever sees it.
        foreach (var bad in new[] { 0, 23, 40, int.MinValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ReaderLimits.Default with { MaxScryptWorkFactor = bad });
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReaderLimits { MaxScryptWorkFactor = bad });
        }

        Assert.Equal(20, ReaderLimits.Default.MaxScryptWorkFactor);
        Assert.Equal(22, (ReaderLimits.Default with { MaxScryptWorkFactor = 22 }).MaxScryptWorkFactor);
    }

    private sealed class CancellingDecryptor(bool inParse) : IEnvelopeDecryptor
    {
        public IEnvelopeHeader ParseHeader(byte[] file, int maxScryptWorkFactor) =>
            inParse ? throw new TaskCanceledException("its own timeout") : AgeEnvelopeDecryptor.Instance.ParseHeader(file, maxScryptWorkFactor);

        public byte[] Decrypt(byte[] file, IEnvelopeHeader header, EnvelopeKey key, long maxBytes) => throw new OperationCanceledException("its own timeout");
    }

    [Fact]
    public async Task ACancellationTheHostDidNotAskFor_IsAFailure_NotThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = WithKey(TestKey) with { KeyProvider = (_, _) => throw new TaskCanceledException("the vault timed out") };
        var r = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), provider, ct);
        Assert.Equal([DiagnosticCodes.E001EncryptedNoKey], r.Diagnostics.Select(d => d.Code));
        var parse = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey) with { EnvelopeDecryptor = new CancellingDecryptor(true) }, ct);
        Assert.Equal([DiagnosticCodes.E006AgeHeaderInvalid], parse.Diagnostics.Select(d => d.Code));
        var later = await MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), WithKey(TestKey) with { EnvelopeDecryptor = new CancellingDecryptor(false) }, ct);
        Assert.Equal([DiagnosticCodes.E002DecryptionFailed], later.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public async Task TheHostCancelling_StillCancels()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = WithKey(TestKey) with
        {
            KeyProvider = (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult<EnvelopeKey?>(null);
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MocaReader.ReadPackageAsync(Fixture("x25519.mocae"), options, cts.Token));
    }

    /// <summary>
    /// A Zip64 envelope (the end record's fields all ones, the real ones in a Zip64 end record): contract §3, this
    /// version does not read Zip64, so the archive is rejected (T003) before any entry, deflated or not, is looked at.
    /// </summary>
    [Fact]
    public async Task Zip64Envelope_IsRejectedWithT003()
    {
        var zip = File.ReadAllBytes(Fixture("deflated-package.mocae"));
        var eocd = zip.AsSpan().LastIndexOf("PK\u0005\u0006"u8);
        var count = BitConverter.ToUInt16(zip, eocd + 10);
        var size = BitConverter.ToUInt32(zip, eocd + 12);
        var offset = BitConverter.ToUInt32(zip, eocd + 16);
        using var m = new MemoryStream();
        m.Write(zip, 0, eocd);
        var zip64At = m.Length;
        using (var w = new BinaryWriter(m, Encoding.UTF8, leaveOpen: true))
        {
            // The Zip64 end of central directory record.
            w.Write(0x06064b50u);
            w.Write(44UL);
            w.Write((ushort)45);
            w.Write((ushort)45);
            w.Write(0u);
            w.Write(0u);
            w.Write((ulong)count);
            w.Write((ulong)count);
            w.Write((ulong)size);
            w.Write((ulong)offset);
            // Its locator.
            w.Write(0x07064b50u);
            w.Write(0u);
            w.Write((ulong)zip64At);
            w.Write(1u);
            // The end record, with the Zip64 markers.
            w.Write(0x06054b50u);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0xFFFF);
            w.Write((ushort)0xFFFF);
            w.Write(0xFFFFFFFFu);
            w.Write(0xFFFFFFFFu);
            w.Write((ushort)0);
        }

        var r = await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(m.ToArray()), WithKey(TestKey), TestContext.Current.CancellationToken);
        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], r.Diagnostics.Select(d => d.Code));
        Assert.Contains("Zip64", r.Diagnostics[0].Message, StringComparison.Ordinal);
    }
}
