using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenMoca.Reader.Age;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Encrypted packages §4: the C2SP age test kit (CCTV) against <see cref="AgeDecryptor"/>, and moca-spec's envelope
/// fixtures against the Go age CLI. The same vectors and skips as moca-reader-typescript's test/age.test.js.
/// </summary>
public sealed class AgeTests
{
    private static readonly string Kit = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cctv-age");

    private sealed record Vector(string Expect, string? Payload, List<string> Identities, List<string> Passphrases, bool Armored, byte[] Body);

    private static Vector Parse(string name)
    {
        var raw = File.ReadAllBytes(Path.Combine(Kit, name));
        var split = raw.AsSpan().IndexOf("\n\n"u8);
        string expect = "", payload = "";
        List<string> identities = [], passphrases = [];
        var armored = false;
        var compressed = false;
        foreach (var line in Encoding.UTF8.GetString(raw, 0, split).Split('\n'))
        {
            var at = line.IndexOf(": ", StringComparison.Ordinal);
            var (k, v) = (line[..at], line[(at + 2)..]);
            switch (k)
            {
                case "expect": expect = v; break;
                case "payload": payload = v; break;
                case "identity": identities.Add(v); break;
                case "passphrase": passphrases.Add(v); break;
                case "armored": armored = v == "yes"; break;
                case "compressed": compressed = v == "zlib"; break;
            }
        }

        var body = raw[(split + 2)..];
        if (compressed)
        {
            using var z = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
            using var m = new MemoryStream();
            z.CopyTo(m);
            body = m.ToArray();
        }

        return new Vector(expect, payload, identities, passphrases, armored, body);
    }

    /// <summary>Why a vector is not run. Each reason is recorded in the 0.7.0 notes.</summary>
    private static string? SkipReason(Vector v) =>
        v.Armored ? "ASCII armor: envelopes carry binary age only (encrypted packages §4)" : null;

    // A hybrid (ML-KEM) vector: Readers do not unwrap its stanzas, so it is run parse-only.
    private static bool IsHybrid(Vector v) => v.Identities.Any(i => i.StartsWith("AGE-SECRET-KEY-PQ-1", StringComparison.Ordinal));

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Kit).Select(Path.GetFileName).Where(n => n != "README.md").Order(StringComparer.Ordinal))
        {
            data.Add(f!);
        }

        return data;
    }

    [Fact]
    public void Kit_HasEveryVector() => Assert.Equal(147, Vectors().Count);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Cctv(string name)
    {
        var v = Parse(name);
        var skip = SkipReason(v);
        Assert.SkipWhen(skip is not null, skip ?? "");
        if (IsHybrid(v))
        {
            // The parser sees every hybrid vector: a valid header parses, and then no key unwraps it (E001).
            AgeHeader parsed;
            try
            {
                parsed = AgeDecryptor.ParseHeader(v.Body, 22);
            }
            catch (AgeException e) when (e.Failure == AgeFailure.Header)
            {
                Assert.Equal("header failure", v.Expect);
                return;
            }

            Assert.Equal(AgeFailure.NoKey, Assert.Throws<AgeException>(() => AgeDecryptor.Decrypt(v.Body, parsed, new AgeKey())).Failure);
            return;
        }

        var (expect, payload) = Outcome(v);
        Assert.Equal(v.Expect, expect);
        // A failure releases no plaintext at all, so only success compares the payload hash.
        if (v.Expect == "success")
        {
            Assert.Equal(v.Payload, payload);
        }
    }

    private static (string Expect, string? Payload) Outcome(Vector v)
    {
        AgeHeader header;
        try
        {
            // The kit uses the age reference limit for the work factor (22); MOCA's default is 20.
            header = AgeDecryptor.ParseHeader(v.Body, 22);
        }
        catch (AgeException e) when (e.Failure == AgeFailure.Header)
        {
            return ("header failure", null);
        }

        var keys = v.Identities.Count > 0 ? [new AgeKey(Identities: v.Identities)] : v.Passphrases.Select(p => new AgeKey(Passphrase: p)).ToList();
        if (keys.Count == 0)
        {
            keys.Add(new AgeKey());
        }

        AgeException? last = null;
        foreach (var key in keys)
        {
            try
            {
                var plain = AgeDecryptor.Decrypt(v.Body, header, key);
                return ("success", Convert.ToHexStringLower(SHA256.HashData(plain)));
            }
            catch (AgeException e)
            {
                last = e;
                if (e.Failure is not (AgeFailure.NoKey or AgeFailure.NoMatch))
                {
                    break;
                }
            }
        }

        return (last!.Failure switch
        {
            AgeFailure.Header => "header failure",
            AgeFailure.NoKey or AgeFailure.NoMatch => "no match",
            AgeFailure.Mac => "HMAC failure",
            _ => "payload failure",
        }, null);
    }

    [Fact]
    public void WorkFactorLimit_RefusesBeforeScrypt_AndMustBeFrom1To22()
    {
        var body = Parse("scrypt").Body;
        Assert.Equal(AgeFailure.Header, Assert.Throws<AgeException>(() => AgeDecryptor.ParseHeader(body, 9)).Failure);
        // A limit outside 1–22 never disables the check (and 1 << 31 can never be reached).
        foreach (var bad in new[] { 0, -1, 23, 31, 40, int.MaxValue })
        {
            Assert.Equal(AgeFailure.Header, Assert.Throws<AgeException>(() => AgeDecryptor.ParseHeader(body, bad)).Failure);
        }
    }

    [Fact]
    public void DecryptedBytes_CountAgainstMaxBytes_BeforeDecrypting_AndExactlyEqualPasses()
    {
        var v = Parse("stream_two_chunks");
        var header = AgeDecryptor.ParseHeader(v.Body);
        var key = new AgeKey(Identities: v.Identities);
        Assert.Equal(AgeFailure.Limit, Assert.Throws<AgeException>(() => AgeDecryptor.Decrypt(v.Body, header, key, 1000)).Failure);
        var plain = AgeDecryptor.Decrypt(v.Body, header, key);
        Assert.Equal(plain.Length, AgeDecryptor.Decrypt(v.Body, header, key, plain.Length).Length);
        Assert.Equal(AgeFailure.Limit, Assert.Throws<AgeException>(() => AgeDecryptor.Decrypt(v.Body, header, key, plain.Length - 1)).Failure);
    }

    [Fact]
    public void PlaintextLength_ComesFromTheCiphertextLength()
    {
        const long full = (64 * 1024) + 16;
        Assert.Equal(0, AgeDecryptor.PlaintextLength(16, long.MaxValue));
        Assert.Equal(64 * 1024, AgeDecryptor.PlaintextLength(full, long.MaxValue));
        Assert.Equal((64 * 1024) + 1, AgeDecryptor.PlaintextLength(full + 17, long.MaxValue));
        foreach (var bad in new long[] { 0, 15, full + 16, full + 15 })
        {
            Assert.Equal(AgeFailure.Payload, Assert.Throws<AgeException>(() => AgeDecryptor.PlaintextLength(bad, long.MaxValue)).Failure);
        }

        Assert.Equal(AgeFailure.Limit, Assert.Throws<AgeException>(() => AgeDecryptor.PlaintextLength(full, 100)).Failure);
    }

    [Fact]
    public void KeyResults_WrongKindsAreNoKey_BadKeysAreNoMatch_SeveralKindsAreAllTried()
    {
        var x = Parse("x25519");
        var s = Parse("scrypt");
        var xh = AgeDecryptor.ParseHeader(x.Body);
        var sh = AgeDecryptor.ParseHeader(s.Body, 22);
        AgeFailure? Outcome(byte[] body, AgeHeader header, AgeKey? key)
        {
            try
            {
                AgeDecryptor.Decrypt(body, header, key);
                return null;
            }
            catch (AgeException e)
            {
                return e.Failure;
            }
        }

        Assert.Equal(AgeFailure.NoKey, Outcome(x.Body, xh, new AgeKey(Passphrase: "p")));
        Assert.Equal(AgeFailure.NoKey, Outcome(s.Body, sh, new AgeKey(Identities: x.Identities)));
        Assert.Equal(AgeFailure.NoKey, Outcome(x.Body, xh, null));
        Assert.Equal(AgeFailure.NoMatch, Outcome(x.Body, xh, new AgeKey(Identities: ["not an identity"])));
        Assert.Equal(AgeFailure.NoMatch, Outcome(x.Body, xh, new AgeKey(Identities: [null!])));
        Assert.Equal(AgeFailure.NoMatch, Outcome(x.Body, xh, new AgeKey(FileKey: new byte[15])));
        // A wrong file key beside the right identities: identities are tried first.
        Assert.Null(Outcome(x.Body, xh, new AgeKey(Identities: x.Identities, FileKey: new byte[16])));
        // A passphrase for an X25519 file beside the right identities.
        Assert.Null(Outcome(x.Body, xh, new AgeKey(Identities: x.Identities, Passphrase: "p")));
    }

    [Fact]
    public void Header_HoldsAtMost256Stanzas()
    {
        static byte[] Header(int n) => Encoding.Latin1.GetBytes(
            "age-encryption.org/v1\n" + string.Concat(Enumerable.Repeat("-> moca-pad x\n\n", n)) + "--- " + new string('A', 43) + "\n" + new string('\0', 16));
        var e = Assert.Throws<AgeException>(() => AgeDecryptor.ParseHeader(Header(AgeDecryptor.MaxStanzas + 1)));
        Assert.Contains("more than 256", e.Message, StringComparison.Ordinal);
        Assert.Equal(AgeDecryptor.MaxStanzas, AgeDecryptor.ParseHeader(Header(AgeDecryptor.MaxStanzas)).Stanzas.Count);
    }

    /// <summary>CI installs the Go age CLI and sets MOCA_REQUIRE_GO_AGE, so the cross-check never skips there.</summary>
    [Fact]
    public void GoAge_IsInstalledWhereRequired()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOCA_REQUIRE_GO_AGE")), "MOCA_REQUIRE_GO_AGE is not set");
        Assert.True(Run("age", ["--version"], [], null).Code == 0, "MOCA_REQUIRE_GO_AGE is set, and `age` is not on the PATH");
    }

    [Fact]
    public void Identities_AreBech32Checked()
    {
        const string good = "AGE-SECRET-KEY-1EGTZVFFV20835NWYV6270LXYVK2VKNX2MMDKWYKLMGR48UAWX40Q2P2LM0";
        Assert.Equal(32, AgeDecryptor.DecodeIdentity(good)?.Length);
        Assert.Null(AgeDecryptor.DecodeIdentity(good[..^1] + "Q"));
        Assert.Equal(32, AgeDecryptor.DecodeIdentity(good.ToLowerInvariant())?.Length);
        Assert.Null(AgeDecryptor.DecodeIdentity("AGE-secret-KEY-1" + good[16..]));
    }

    /// <summary>Every moca-spec envelope fixture decrypts to the same bytes here and with the Go age CLI.</summary>
    [Fact]
    public void EnvelopeFixtures_MatchGoAge()
    {
        var envelopes = Path.Combine(MocaSpec.Conformance, "fixtures", "envelopes");
        Assert.SkipUnless(Directory.Exists(envelopes), "moca-spec has no envelope fixtures");
        Assert.SkipUnless(Run("age", ["--version"], [], null).Code == 0, "the Go age CLI is not installed");

        var identityFile = Path.Combine(envelopes, "keys", "INSECURE-test.txt");
        var identities = File.ReadAllLines(identityFile).Where(l => l.StartsWith("AGE-SECRET-KEY-1", StringComparison.Ordinal)).ToList();
        const string passphrase = "moca-conformance-INSECURE-passphrase";
        var compared = 0;
        foreach (var path in Directory.GetFiles(envelopes, "*.moca.enc"))
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("package.age");
            if (entry is null)
            {
                continue;
            }

            using var s = entry.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            var bytes = m.ToArray();
            var usesScrypt = Encoding.Latin1.GetString(bytes, 0, Math.Min(200, bytes.Length)).Contains("-> scrypt", StringComparison.Ordinal);
            var go = usesScrypt
                ? Run("age", ["--decrypt", "-j", "batchpass"], bytes, new() { ["AGE_PASSPHRASE"] = passphrase, ["AGE_PASSPHRASE_MAX_WORK_FACTOR"] = "22" })
                : Run("age", ["--decrypt", "-i", identityFile], bytes, null);
            byte[]? ours;
            try
            {
                var header = AgeDecryptor.ParseHeader(bytes, 22);
                ours = AgeDecryptor.Decrypt(bytes, header, usesScrypt ? new AgeKey(Passphrase: passphrase) : new AgeKey(Identities: identities));
            }
            catch (AgeException)
            {
                ours = null;
            }

            // MOCA's own limit: a header over 64 KiB is E006 here, and Go age has no such limit.
            if (Path.GetFileName(path) == "header-over-64k.moca.enc")
            {
                Assert.Null(ours);
                Assert.Equal(0, go.Code);
                compared++;
                continue;
            }

            // The mixed-stanza fixture: Go age fails at the MAC, this module at the header. Both refuse it.
            Assert.True(ours is not null == (go.Code == 0), $"{Path.GetFileName(path)}: ours {(ours is null ? "refuses" : "decrypts")}, Go age {(go.Code == 0 ? "decrypts" : "refuses")}");
            if (ours is not null)
            {
                Assert.Equal(go.Stdout, ours);
            }

            compared++;
        }

        Assert.True(compared >= 15);
    }

    internal static (int Code, byte[] Stdout) Run(string file, string[] args, byte[] input, Dictionary<string, string>? env)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env ?? [])
        {
            info.Environment[k] = v;
        }

        try
        {
            using var p = Process.Start(info)!;
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
            using var m = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(m);
            _ = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            copy.Wait();
            return (p.ExitCode, m.ToArray());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, []);
        }
    }
}
