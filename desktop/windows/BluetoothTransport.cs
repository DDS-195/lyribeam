using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LyricBridge;

internal interface IBluetoothConnection : IDisposable
{
    int Receive(byte[] buffer);
    void Close();
}

internal interface IBluetoothTransport : IDisposable
{
    string Open();
    IBluetoothConnection Accept();
    void Close();
}

// Owns one listener registration. Recreating this object recreates the complete native service.
internal sealed class NativeBluetoothTransport : IBluetoothTransport
{
    private static readonly nint Invalid = new(-1);
    private readonly object gate = new();
    private nint listener = Invalid;
    private bool closed, initialized, registered;
    private nint addressPtr, serviceIdPtr, csAddressPtr, namePtr;
    private QuerySet registration;

    public string Open()
    {
        lock (gate)
        {
            if (closed) throw new OperationCanceledException();
            int startup = WSAStartup(0x202, new byte[512]);
            if (startup != 0) throw new Win32Exception(startup);
            initialized = true;
            listener = socket(32, 1, 3);
            if (listener == Invalid) ThrowLastError();
            uint enabled = 1;
            // Require both authenticated pairing and encryption, as in the original service.
            Check(setsockopt(listener, 3, unchecked((int)0x80000001), ref enabled, 4));
            Check(setsockopt(listener, 3, 2, ref enabled, 4));
            var address = new BluetoothAddress { Family = 32, Port = uint.MaxValue };
            Check(bind(listener, ref address, Marshal.SizeOf<BluetoothAddress>()));
            int size = Marshal.SizeOf<BluetoothAddress>();
            Check(getsockname(listener, ref address, ref size));
            Check(listen(listener, 1));
            Register(address);
            return $"蓝牙已就绪，等待手机连接（通道 {address.Port}）";
        }
    }

    public IBluetoothConnection Accept()
    {
        nint current;
        lock (gate) { if (closed) throw new OperationCanceledException(); current = listener; }
        nint incoming = accept(current, nint.Zero, nint.Zero);
        if (incoming == Invalid) ThrowLastError();
        lock (gate)
        {
            if (closed) { closesocket(incoming); throw new OperationCanceledException(); }
            return new NativeConnection(incoming);
        }
    }

    private void Register(BluetoothAddress address)
    {
        addressPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BluetoothAddress>());
        Marshal.StructureToPtr(address, addressPtr, false);
        serviceIdPtr = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(BluetoothServer.ServiceId, serviceIdPtr, false);
        var cs = new CsAddress { Local = new SocketAddress { Address = addressPtr, Length = Marshal.SizeOf<BluetoothAddress>() }, SocketType = 1, Protocol = 3 };
        csAddressPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CsAddress>());
        Marshal.StructureToPtr(cs, csAddressPtr, false);
        namePtr = Marshal.StringToHGlobalUni("LyricBridge");
        registration = new QuerySet { Size = (uint)Marshal.SizeOf<QuerySet>(), Name = namePtr, ServiceClassId = serviceIdPtr, Namespace = 16, AddressCount = 1, Addresses = csAddressPtr };
        Check(WSASetService(ref registration, 0, 0));
        registered = true;
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            if (listener != Invalid) { closesocket(listener); listener = Invalid; }
        }
    }

    public void Dispose()
    {
        Close();
        lock (gate)
        {
            if (registered) { WSASetService(ref registration, 2, 0); registered = false; }
            foreach (nint pointer in new[] { addressPtr, serviceIdPtr, csAddressPtr, namePtr }) if (pointer != 0) Marshal.FreeHGlobal(pointer);
            addressPtr = serviceIdPtr = csAddressPtr = namePtr = 0;
            if (initialized) { WSACleanup(); initialized = false; }
        }
    }

    internal static int AddressSize => Marshal.SizeOf<BluetoothAddress>();
    private static void Check(int result) { if (result == -1) ThrowLastError(); }
    private static void ThrowLastError() => throw new Win32Exception(WSAGetLastError());

    private sealed class NativeConnection(nint incoming) : IBluetoothConnection
    {
        private readonly object gate = new();
        private nint handle = incoming;
        public int Receive(byte[] buffer)
        {
            nint current;
            lock (gate) { if (handle == Invalid) return 0; current = handle; }
            int count = recv(current, buffer, buffer.Length, 0);
            if (count == -1) ThrowLastError();
            return count;
        }
        public void Close()
        {
            lock (gate)
            {
                if (handle == Invalid) return;
                shutdown(handle, 2); closesocket(handle); handle = Invalid;
            }
        }
        public void Dispose() => Close();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BluetoothAddress { public ushort Family; public ulong Address; public Guid ServiceClass; public uint Port; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SocketAddress { public nint Address; public int Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CsAddress { public SocketAddress Local, Remote; public int SocketType, Protocol; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QuerySet
    {
        public uint Size; public nint Name, ServiceClassId, Version, Comment; public uint Namespace;
        public nint ProviderId, Context; public uint ProtocolCount; public nint Protocols, Query;
        public uint AddressCount; public nint Addresses; public uint OutputFlags; public nint Blob;
    }
    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, byte[] data);
    [DllImport("ws2_32.dll")] private static extern int WSACleanup();
    [DllImport("ws2_32.dll")] private static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] private static extern nint socket(int family, int type, int protocol);
    [DllImport("ws2_32.dll")] private static extern int bind(nint socket, ref BluetoothAddress address, int length);
    [DllImport("ws2_32.dll")] private static extern int getsockname(nint socket, ref BluetoothAddress address, ref int length);
    [DllImport("ws2_32.dll")] private static extern int listen(nint socket, int backlog);
    [DllImport("ws2_32.dll")] private static extern nint accept(nint socket, nint address, nint length);
    [DllImport("ws2_32.dll")] private static extern int recv(nint socket, byte[] data, int length, int flags);
    [DllImport("ws2_32.dll")] private static extern int closesocket(nint socket);
    [DllImport("ws2_32.dll")] private static extern int shutdown(nint socket, int how);
    [DllImport("ws2_32.dll")] private static extern int setsockopt(nint socket, int level, int option, ref uint value, int length);
    [DllImport("ws2_32.dll", EntryPoint = "WSASetServiceW", CharSet = CharSet.Unicode)]
    private static extern int WSASetService(ref QuerySet query, int operation, uint flags);
}
