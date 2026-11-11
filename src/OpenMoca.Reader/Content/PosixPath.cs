namespace OpenMoca.Reader.Content;

/// <summary>POSIX path helpers matching Node's <c>path.posix</c>, so package paths resolve identically.</summary>
internal static class PosixPath
{
    public static string DirName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash switch
        {
            < 0 => ".",
            0 => "/",
            _ => path[..slash],
        };
    }

    public static string BaseName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}/{b}";

    public static bool IsAbsolute(string path) => path.StartsWith('/');

    /// <summary>Resolves <c>.</c> and <c>..</c> segments and repeated slashes; leading <c>..</c> stays.</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var absolute = IsAbsolute(path);
        var trailingSlash = path.EndsWith('/');
        var stack = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!absolute)
                {
                    stack.Add("..");
                }

                continue;
            }

            stack.Add(segment);
        }

        var joined = string.Join('/', stack);
        if (absolute)
        {
            return "/" + joined;
        }

        if (joined.Length == 0)
        {
            return ".";
        }

        return trailingSlash ? joined + "/" : joined;
    }
}
