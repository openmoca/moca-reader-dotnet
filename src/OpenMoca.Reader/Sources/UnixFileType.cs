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

    private static bool _unavailable = OperatingSystem.IsWindows();

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
            var status = new byte[512];
            if (LStat(path, status) != 0)
            {
                return false;
            }

            var type = BitConverter.ToInt32(status, 4) & TypeMask;
            return type is not (Regular or Directory or Link);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _unavailable = true;
            return false;
        }
    }
}
