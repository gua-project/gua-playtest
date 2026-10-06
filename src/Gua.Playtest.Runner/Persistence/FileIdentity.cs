using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gua.Playtest.Runner.Artifacts;

/// <summary>Filesystem identity from the same open handle used for hash readback; names and content
/// equality cannot identify hard links. Uses only installed OS APIs, with no Gua/native engine dependency.</summary>
internal readonly record struct FileIdentity(ulong Device, ulong Low, ulong High = 0)
{
    internal static (FileStream Stream, FileIdentity Identity) OpenRegular(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { return (stream, Read(stream.SafeFileHandle)); }
            catch { stream.Dispose(); throw; }
        }
        // Nonblocking open prevents a FIFO from waiting for a writer before its type can be checked.
        // NOFOLLOW also closes the final-component symlink race; the containing tree is owner-private.
        var flags = OperatingSystem.IsLinux() ? 0x800 | 0x20000 | 0x80000 :
            OperatingSystem.IsMacOS() ? 0x4 | 0x100 | 0x1000000 : throw new IOException("FileIdentityUnavailable");
        var descriptor = OperatingSystem.IsMacOS() ? MacOpen(path, flags) : Open(path, flags);
        if (descriptor < 0) throw new IOException("ArtifactOpenFailed");
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            var identity = Read(handle);
            return (new FileStream(handle, FileAccess.Read), identity);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static FileIdentity Read(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(handle) != 1 /* FILE_TYPE_DISK */) throw new InvalidDataException("ArtifactNotRegular");
            if (!GetFileInformationByHandleEx(handle, 18 /* FileIdInfo */, out var info, 24)) throw new IOException("FileIdentityUnavailable");
            return new(info.Volume, info.Low, info.High);
        }
        if (OperatingSystem.IsLinux())
        {
            if (Statx(handle, "", 0x1000 /* AT_EMPTY_PATH */, 0x101 /* STATX_TYPE | STATX_INO */, out var info) != 0 ||
                (info.Mask & 0x101) != 0x101) throw new IOException("FileIdentityUnavailable");
            if ((info.Mode & 0xf000) != 0x8000) throw new InvalidDataException("ArtifactNotRegular");
            return new(((ulong)info.DeviceMajor << 32) | info.DeviceMinor, info.Inode);
        }
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
        {
            var code = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStat64(handle, out var info) : MacStat(handle, out info);
            if (code != 0) throw new IOException("FileIdentityUnavailable");
            if ((info.Mode & 0xf000) != 0x8000) throw new InvalidDataException("ArtifactNotRegular");
            return new(info.Device, info.Inode);
        }
        throw new IOException("FileIdentityUnavailable");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsId { public ulong Volume, Low, High; }
    // Linux UAPI statx ABI is architecture-independent, size 0x100.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    // Darwin 64-bit inode stat ABI on x64/arm64; reserve the full struct, read identity fields only.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(8)] public ulong Inode;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out WindowsId info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx info);
    [DllImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacStat64(SafeFileHandle fd, out DarwinStat info);
    [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacStat(SafeFileHandle fd, out DarwinStat info);
}
