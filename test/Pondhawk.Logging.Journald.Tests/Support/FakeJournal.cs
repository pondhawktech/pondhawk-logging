// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pondhawk.Logging.Journald.Tests.Support;

/// <summary>
/// A datagram socket standing in for journald's: receives each message, read from the datagram or, when one
/// was passed in <c>SCM_RIGHTS</c>, from the memfd.
/// </summary>
public sealed unsafe partial class FakeJournal : IDisposable
{
    private const int F_GET_SEALS = 1034;
    private const int AllSeals = 0x0001 | 0x0002 | 0x0004 | 0x0008;

    private readonly string _directory;
    private readonly Socket _socket;

    public FakeJournal(int receiveBufferBytes = 4 * 1024 * 1024)
    {
        _directory = Path.Combine(Path.GetTempPath(), "jd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        SocketPath = Path.Combine(_directory, "socket");

        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        _socket.ReceiveBufferSize = receiveBufferBytes;
        _socket.ReceiveTimeout = 5_000;
        _socket.Bind(new UnixDomainSocketEndPoint(SocketPath));
    }

    public string SocketPath { get; }

    /// <summary>One message received, and how it came.</summary>
    public sealed record Received(byte[] Message, bool ByMemfd, bool Sealed);

    public Received Receive()
    {
        var buffer = new byte[1024 * 1024];
        var control = stackalloc byte[64];
        long length;
        int? fd = null;

        fixed (byte* data = buffer)
        {
            var iov = new IoVec { Base = data, Length = (nuint)buffer.Length };
            var message = new MsgHdr { Iov = &iov, IovLength = 1, Control = control, ControlLength = 64 };

            var added = false;
            _socket.SafeHandle.DangerousAddRef(ref added);
            try
            {
                length = RecvMsg((int)_socket.SafeHandle.DangerousGetHandle(), &message, 0);
            }
            finally
            {
                if (added)
                    _socket.SafeHandle.DangerousRelease();
            }

            if (length < 0)
                throw new IOException($"recvmsg failed: errno {Marshal.GetLastPInvokeError()}");

            if (message.ControlLength >= 20)
            {
                var header = (CmsgHdr*)control;
                if (header->Level == 1 && header->Type == 1)
                    fd = *(int*)(control + sizeof(CmsgHdr));
            }
        }

        if (fd is not { } descriptor)
            return new Received(buffer[..(int)length], ByMemfd: false, Sealed: false);

        var seals = Fcntl(descriptor, F_GET_SEALS, 0);
        using var memfd = new SafeFileHandle(descriptor, ownsHandle: true);
        var contents = new byte[RandomAccess.GetLength(memfd)];
        RandomAccess.Read(memfd, contents, fileOffset: 0);
        return new Received(contents, ByMemfd: true, Sealed: (seals & AllSeals) == AllSeals);
    }

    public void Dispose()
    {
        _socket.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [LibraryImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    private static partial nint RecvMsg(int socket, MsgHdr* message, int flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int command, int argument);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public byte* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public void* Name;
        public uint NameLength;
        public void* Iov;
        public nuint IovLength;
        public void* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmsgHdr
    {
        public nuint Length;
        public int Level;
        public int Type;
    }
}
