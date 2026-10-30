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
        int size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (status != 122 || size is < 4 or > 1048576) throw new InvalidOperationException("ListenerIdentityUnavailable");
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(memory, ref size, false, 2, 3, 0);
            if (status != 0) throw new InvalidOperationException("ListenerIdentityUnavailable");
            int count = Marshal.ReadInt32(memory);
            if (count < 0 || count > (size - 4) / 24) throw new InvalidOperationException("ListenerIdentityUnavailable");
            var owners = new List<int>();
            for (int i = 0; i < count; i++)
            {
                var row = IntPtr.Add(memory, 4 + i * 24);
                int port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                uint address = unchecked((uint)Marshal.ReadInt32(row, 4));
                if (port == endpoint.Port && address is 0 or 0x0100007f) owners.Add(Marshal.ReadInt32(row, 20));
            }
            if (owners.Count == 0) throw new ConnectionNotReadyException();
            if (owners.Count != 1 || owners[0] != process.ProcessId || process.HasExited)
                throw new InvalidOperationException("ListenerOwnerMismatch");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
