using System.Net;
using System.Runtime.InteropServices;
using Gua.Playtest.Runner.Preparation;

namespace Gua.Playtest.Cli;

/// <summary>Read-only OS evidence binding the loopback listener to the acquired process handle.
/// A port being reachable or Gua's own library BuildId is not game build attestation.</summary>
internal static class WindowsBridgeOwner
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    internal static void Require(Uri endpoint, IOwnedProcessIdentity process)
    {
        if (!OperatingSystem.IsWindows() || endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("WindowsLoopbackRequired");
        if (process.HasExited) throw new InvalidOperationException("OwnedProcessExited");
        var owners = ListenerOwners(endpoint.Port, (IntPtr table, ref int bytes) => GetExtendedTcpTable(table, ref bytes, false, 2, 3, 0));
        if (owners.Count == 0) throw new ConnectionNotReadyException();
        if (owners.Count != 1 || owners[0] != process.ProcessId || process.HasExited)
            throw new InvalidOperationException("ListenerOwnerMismatch");
    }
    internal delegate uint ReadTable(IntPtr table, ref int size);
    internal static IReadOnlyList<int> ListenerOwners(int expectedPort, ReadTable read)
    {
        int size = 0;
        var status = read(IntPtr.Zero, ref size);
        if (status != 122 || size is < 4 or > 1048576) throw new InvalidOperationException("ListenerIdentityUnavailable");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int allocated = size;
            var memory = Marshal.AllocHGlobal(allocated);
            try
            {
            status = read(memory, ref size);
            // The OS table can grow between size and data reads. Retry only buffer
            // allocation, with bounded attempts/bytes; never relax owner matching.
            if (status == 122 && size > allocated && size <= 1048576) continue;
            if (status != 0 || size < 4 || size > allocated) throw new InvalidOperationException("ListenerIdentityUnavailable");
            int count = Marshal.ReadInt32(memory);
            if (count < 0 || count > (size - 4) / 24) throw new InvalidOperationException("ListenerIdentityUnavailable");
            var owners = new List<int>();
            for (int i = 0; i < count; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * 24);
                int port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                uint address = unchecked((uint)Marshal.ReadInt32(row, 4));
                if (port == expectedPort && address is 0 or 0x0100007f) owners.Add(Marshal.ReadInt32(row, 20));
            }
            return owners;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new InvalidOperationException("ListenerIdentityUnavailable");
    }
}
