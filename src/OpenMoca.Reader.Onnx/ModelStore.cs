using System.Security.Cryptography;

namespace OpenMoca.Reader.Onnx;

/// <summary>
/// The folder of standard models on this machine, one sub-folder per model id. Loading only reads it, and checks
/// each file's SHA-256 before loading a model. <see cref="AddAsync"/>, which downloads, runs only when a person asks
/// for it (for example <c>moca-mcp models add</c>); reading and loading never download.
/// </summary>
public sealed class ModelStore(string folder)
{
    /// <summary>The default folder: <c>~/.moca/models</c>.</summary>
    public static string DefaultFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moca", "models");

    /// <summary>The store's folder.</summary>
    public string Folder { get; } = folder;

    /// <summary>The standard models whose files are all present (not yet checked).</summary>
    public IReadOnlyList<StandardModel> Installed() =>
        [.. StandardModel.All.Where(m => m.Files.Keys.All(f => File.Exists(Path.Combine(Folder, m.Id, f))))];

    /// <summary>Checks a model's files against their pinned SHA-256; returns the first problem, or null.</summary>
    public string? Check(StandardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var (file, sha) in model.Files)
        {
            var path = Path.Combine(Folder, model.Id, file);
            if (!File.Exists(path))
            {
                return $"{model.Id}: {file} is missing";
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (actual != sha)
            {
                return $"{model.Id}: {file} does not match its pinned SHA-256";
            }
        }

        return null;
    }

    /// <summary>Loads an installed, checked model.</summary>
    public OnnxEmbedder Load(StandardModel model)
    {
        if (Check(model) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        return OnnxEmbedder.Load(model, Path.Combine(Folder, model.Id));
    }

    /// <summary>
    /// Downloads a model's files from Hugging Face at its pinned commit, checks each SHA-256, and only then puts it in
    /// the store. A file that does not match is discarded.
    /// </summary>
    public async Task AddAsync(StandardModel model, HttpClient http, Action<string> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(http);
        var target = Path.Combine(Folder, model.Id);
        var staging = Path.Combine(Folder, $".{model.Id}.partial");
        Directory.CreateDirectory(staging);
        foreach (var (file, sha) in model.Files)
        {
            var url = new Uri($"https://huggingface.co/{model.Repository}/resolve/{model.Commit}/{file}");
            progress($"downloading {model.Id}/{file}");
            var path = Path.Combine(staging, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(path);
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            await using var check = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false));
            if (actual != sha)
            {
                Directory.Delete(staging, recursive: true);
                throw new InvalidOperationException($"{model.Id}: {file} from Hugging Face does not match its pinned SHA-256; nothing was installed");
            }
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(staging, target);
    }
}
