using System.Runtime.InteropServices;

namespace OpenMoca.Reader.Sources;

/// <summary>
/// Whether a path names something other than a regular file, directory or symbolic link: a named pipe, a device or a
/// socket (contract §3: only regular files are read). .NET reports these as ordinary files, and opening a named pipe
/// blocks until something writes to it, so the type is checked first, with <c>lstat</c>. This uses the .NET runtime's
/// own native shim; where it is not available (Windows, or a host that does not ship it), the answer is "unknown" and
/// the directory source falls back to never opening an entry that reports no bytes.
/// </summary>
internal static partial class UnixFileType
{
    private const int TypeMask = 0xF000;
    private const int Regular = 0x8000;
    private const int Directory = 0x4000;
    private const int Link = 0xA000;

    // The shim's struct is the runtime's own, not a public API: it is trusted only after it reads a known directory and a
    // known regular file correctly, so a layout this code does not expect can never make a regular file look special.
    private static bool _unavailable = OperatingSystem.IsWindows() || !SelfTest();

    private static bool SelfTest()
    {
        try
        {
            var file = typeof(object).Assembly.Location;
            return file.Length > 0 && TypeOf(AppContext.BaseDirectory) == Directory && TypeOf(file) == Regular;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or BadImageFormatException)
        {
            return false;
        }
    }

    private static int? TypeOf(string path)
    {
        var status = new byte[512];
        return LStat(path.TrimEnd('/'), status) == 0 ? BitConverter.ToInt32(status, 4) & TypeMask : null;
    }

    // The runtime's FileStatus: Flags (int32), then Mode (int32). The buffer is larger than the struct.
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat")]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] status);

    /// <summary>True when the path is known to be a pipe, device or socket; false when it is not, or cannot be told.</summary>
    public static bool IsSpecial(string path)
    {
        if (_unavailable)
        {
            return false;
        }

        try
        {
            return TypeOf(path) is { } type && type is not (Regular or Directory or Link);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _unavailable = true;
            return false;
        }
    }
}
