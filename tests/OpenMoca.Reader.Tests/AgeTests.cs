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
        v.Armored ? "ASCII armor: envelopes carry binary age only (encrypted packages §4)"
        : v.Identities.Any(i => i.StartsWith("AGE-SECRET-KEY-PQ-1", StringComparison.Ordinal)) ? "hybrid (ML-KEM) identity: Readers unwrap X25519 and scrypt only"
        : null;

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
    public void WorkFactorLimit_RefusesBeforeScrypt()
    {
        var e = Assert.Throws<AgeException>(() => AgeDecryptor.ParseHeader(Parse("scrypt").Body, 9));
        Assert.Equal(AgeFailure.Header, e.Failure);
    }

    [Fact]
    public void DecryptedBytes_CountAgainstMaxBytes()
    {
        var v = Parse("stream_two_chunks");
        var header = AgeDecryptor.ParseHeader(v.Body);
        var e = Assert.Throws<AgeException>(() => AgeDecryptor.Decrypt(v.Body, header, new AgeKey(Identities: v.Identities), 1000));
        Assert.Equal(AgeFailure.Limit, e.Failure);
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

    private static (int Code, byte[] Stdout) Run(string file, string[] args, byte[] input, Dictionary<string, string>? env)
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
