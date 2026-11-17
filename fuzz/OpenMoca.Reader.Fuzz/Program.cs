// Fuzz targets for the open-and-search path (moca-spec SECURITY.md, "Archive handling in the Readers"), run by
// libFuzzer through SharpFuzz. A package is untrusted input, so ReadPackageAsync must never throw, and a package
// it reads as valid must be searchable without throwing. Any exception is a crash.
//
//   MOCA_FUZZ_TARGET=open   archive bytes (the default)
//   MOCA_FUZZ_TARGET=files  one file of a valid package replaced, read through a host source; the first byte
//                           chooses the file. The base is MOCA_FUZZ_BASE (default structure-procedure) in
//                           MOCA_SPEC_DIR's conformance fixtures.
//
//   OpenMoca.Reader.Fuzz seed <moca-spec dir> <corpus dir>   builds <corpus dir> and <corpus dir>-files
using System.IO.Compression;
using System.Security.Cryptography;
using OpenMoca.Reader;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sources;
using SharpFuzz;

if (args.Length == 3 && args[0] == "seed")
{
    Seeds.Build(args[1], args[2]);
    return;
}

// Nothing from the instrumented Reader may run before Fuzzer.LibFuzzer.Run: its coverage map does not exist yet.
if (Environment.GetEnvironmentVariable("MOCA_FUZZ_TARGET") == "files")
{
    var files = Seeds.BaseFiles();
    var paths = files.Keys.Order(StringComparer.Ordinal).ToArray();
    Fuzzer.LibFuzzer.Run(data =>
    {
        if (data.Length == 0) return;
        var source = new ReplacedFileSource(files, paths, paths[data[0] % paths.Length], data[1..].ToArray());
        ReadAndSearch(PackageTarget.FromSource(source), "module");
    });
}
else
{
    Fuzzer.LibFuzzer.Run(data => ReadAndSearch(PackageTarget.FromArchive(data.ToArray()), "refund", "the", "a b c"));
}

static void ReadAndSearch(PackageTarget target, params string[] queries)
{
    var limits = new ReaderLimits { MaxEntries = 512, MaxBytes = 8 * 1024 * 1024 };
    var result = MocaReader.ReadPackageAsync(target, new ReadOptions { Limits = limits }).GetAwaiter().GetResult();
    if (!result.Valid || result.Digest is null) return;
    var library = new Library().Add(result);
    var search = new MocaSearch(library, new LexicalBackend(library), audiences: ["public"]);
    foreach (var query in queries) search.SearchAsync(query).GetAwaiter().GetResult();
}

/// <summary>A valid package's files, with one replaced by fuzzed bytes.</summary>
sealed class ReplacedFileSource(IReadOnlyDictionary<string, byte[]> files, string[] paths, string target, byte[] data) : IPackageSource
{
    public string Kind => "host";
    public string? Location => null;
    public IReadOnlyList<PackageEntry> List() => [.. paths.Select(p => new PackageEntry(p))];
    public byte[] Read(string path) => path == target ? data : files[path];
}

static class Seeds
{
    const int Max = 1024 * 1024; // keep seeds small, so each run explores more

    static string Spec => Environment.GetEnvironmentVariable("MOCA_SPEC_DIR") ?? Path.Combine("..", "..", "..", "moca-spec");
    static string BaseName => Environment.GetEnvironmentVariable("MOCA_FUZZ_BASE") ?? "structure-procedure";

    public static Dictionary<string, byte[]> BaseFiles(string? spec = null)
    {
        var root = Path.GetFullPath(Path.Combine(spec ?? Spec, "conformance", "fixtures", BaseName));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"no fixture at {root}: set MOCA_SPEC_DIR");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes);
    }

    public static void Build(string spec, string output)
    {
        var filesOutput = output.TrimEnd('/', '\\') + "-files";
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(filesOutput);
        var n = 0;
        var fixtures = Path.Combine(spec, "conformance", "fixtures");
        foreach (var dir in Directory.EnumerateDirectories(fixtures).Order(StringComparer.Ordinal)) n += Save(Zipped(dir), output);
        foreach (var file in Directory.EnumerateFiles(fixtures).Order(StringComparer.Ordinal))
        {
            if (file.EndsWith(".zip", StringComparison.Ordinal) || file.EndsWith(".moca", StringComparison.Ordinal)) n += Save(File.ReadAllBytes(file), output);
        }
        var examples = Path.Combine(spec, "dist", "examples");
        if (Directory.Exists(examples))
        {
            foreach (var file in Directory.EnumerateFiles(examples, "*.moca").Order(StringComparer.Ordinal)) n += Save(File.ReadAllBytes(file), output);
        }
        // Seeds for the files target, in its file order (sorted paths), each led by the byte choosing it.
        var files = BaseFiles(spec);
        var paths = files.Keys.Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < paths.Length; i++) n += Save([(byte)i, .. files[paths[i]]], filesOutput);
        Console.WriteLine($"{n} seed(s) in {output} and {filesOutput}");
    }

    static byte[] Zipped(string dir)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                using var entry = zip.CreateEntry(Path.GetRelativePath(dir, file).Replace('\\', '/')).Open();
                entry.Write(File.ReadAllBytes(file));
            }
        }
        return buffer.ToArray();
    }

    static int Save(byte[] data, string folder)
    {
        if (data.Length > Max) return 0;
        File.WriteAllBytes(Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(data))[..16]), data);
        return 1;
    }
}
