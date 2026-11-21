using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenMoca.Reader.Diagnostics;
using Xunit;

namespace OpenMoca.Reader.Tests;

/// <summary>
/// Contract §3, archives: the records every Zip reader must agree on are checked before any entry is read (T003), and
/// an entry is read only when it is stored or deflated, unencrypted, exactly its declared size and its CRC-32 (P002).
/// </summary>
public sealed class ArchiveTests
{
    private const string Manifest = """{ "id": "https://example.com/tests/archive", "version": "1.0.0", "title": "Archive" }""";
    private const string NodeName = "content/hello.md";
    private const string Node = "---\ntype: Note\ntitle: Hello\n---\n\nHello.\n";

    // Stored entries, so a test can find and change an entry's bytes directly.
    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("moca.json", Manifest), (NodeName, Node) })
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }

    private static int EndRecord(byte[] zip) => zip.AsSpan().LastIndexOf("PK\u0005\u0006"u8);

    private static int CentralEntry(byte[] zip, string name)
    {
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(EndRecord(zip) + 16));
        while (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at)) == 0x02014b50)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28));
            if (Encoding.UTF8.GetString(zip, at + 46, nameLength) == name)
            {
                return at;
            }

            at += 46 + nameLength + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 32));
        }

        throw new InvalidOperationException($"no entry {name}");
    }

    private static int LocalHeader(byte[] zip, string name) => (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(CentralEntry(zip, name) + 42));

    private static async Task<PackageResult> Read(byte[] zip) =>
        await MocaReader.ReadPackageAsync(PackageTarget.FromArchive(zip), cancellationToken: TestContext.Current.CancellationToken);

    private static IEnumerable<string> Codes(PackageResult r) => r.Diagnostics.Select(d => d.Code);

    [Fact]
    public async Task ReadPackage_WhenArchiveIsWellFormed_IsValid()
    {
        var result = await Read(Archive());

        Assert.True(result.Valid, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(result.Digest);
    }

    [Fact]
    public async Task ReadPackage_WhenEntryHoldsFewerBytesThanDeclared_ReportsP002()
    {
        var zip = Archive();
        var at = CentralEntry(zip, NodeName);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(at + 24), BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at + 24)) + 1);

        Assert.Contains(DiagnosticCodes.P002UnreadableEntry, Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenEntryFailsItsCrc_ReportsP002()
    {
        var zip = Archive();
        var local = LocalHeader(zip, NodeName);
        var data = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 26)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 28));
        zip[data + Node.Length - 2] ^= 0x01;

        Assert.Contains(DiagnosticCodes.P002UnreadableEntry, Codes(await Read(zip)));
    }

    [Theory]
    [InlineData(9)] // Deflate64
    [InlineData(12)] // bzip2
    public async Task ReadPackage_WhenEntryUsesAnotherMethod_ReportsP002(int method)
    {
        var zip = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(CentralEntry(zip, NodeName) + 10), (ushort)method);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(LocalHeader(zip, NodeName) + 8), (ushort)method);

        Assert.Contains(DiagnosticCodes.P002UnreadableEntry, Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenEntryIsMarkedEncrypted_ReportsP002()
    {
        var zip = Archive();
        var at = CentralEntry(zip, NodeName);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(at + 8), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 8)) | 1));

        Assert.Contains(DiagnosticCodes.P002UnreadableEntry, Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenLocalHeaderNamesAnotherFile_ReportsT003()
    {
        var zip = Archive();
        zip[LocalHeader(zip, NodeName) + 30 + "content/".Length] = (byte)'j';

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenLocalHeaderHasAnotherMethod_ReportsT003()
    {
        var zip = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(LocalHeader(zip, NodeName) + 8), 8);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenThereAreTwoEndRecords_ReportsT003()
    {
        // The first end record's comment is a second end record, so both end at the end of the file.
        var zip = Archive();
        var end = zip.AsSpan(EndRecord(zip), 22).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(EndRecord(zip) + 20), 22);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read([.. zip, .. end])));
    }

    [Fact]
    public async Task ReadPackage_WhenNoEndRecordEndsTheFile_ReportsT003()
    {
        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read([.. Archive(), 0])));
    }

    [Fact]
    public async Task ReadPackage_WhenEndRecordIsMultiDisk_ReportsT003()
    {
        var zip = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(EndRecord(zip) + 4), 1);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenDirectoryDeclaresMoreEntriesThanItHolds_ReportsT003()
    {
        var zip = Archive();
        var end = EndRecord(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 10), 3);

        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read(zip)));
    }

    [Fact]
    public async Task ReadPackage_WhenArchiveIsTooShortForAnEndRecord_ReportsT003()
    {
        Assert.Equal([DiagnosticCodes.T003ArchiveRejected], Codes(await Read("PK\u0005\u0006"u8.ToArray())));
    }
}
