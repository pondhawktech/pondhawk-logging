// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Pondhawk.Logging.Journald.NativeMethods;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// Sends native-protocol messages to journald's socket, one datagram per event, synchronously on the calling
/// thread: nothing is queued, so nothing is left to lose when the process exits.
/// </summary>
/// <remarks>
/// <para>
/// The socket is non-blocking. A send journald cannot take now — its queue full, the daemon restarting, the
/// socket gone — fails at once and <see cref="TrySend"/> returns false, so the caller can write the event
/// elsewhere; it never waits for journald.
/// </para>
/// <para>
/// A message larger than one datagram can carry goes the way <c>sd_journal_send</c> sends it: written to a
/// sealed <c>memfd</c>, whose descriptor is passed in <c>SCM_RIGHTS</c> with an empty datagram.
/// </para>
/// </remarks>
internal sealed class JournalSocket : IDisposable
{
    /// <summary>Where journald listens for native-protocol messages.</summary>
    public const string DefaultPath = "/run/systemd/journal/socket";

    /// <summary>
    /// The send buffer asked for, as <c>sd_journal_send</c> asks: the kernel caps it at <c>wmem_max</c>, and a
    /// message larger than what it grants goes by <c>memfd</c>.
    /// </summary>
    public const int DefaultSendBufferBytes = 8 * 1024 * 1024;

    private readonly Socket _socket;
    private readonly UnixDomainSocketEndPoint _endPoint;
    private readonly byte[] _path;

    private JournalSocket(Socket socket, string path)
    {
        _socket = socket;
        _endPoint = new UnixDomainSocketEndPoint(path);
        _path = Encoding.UTF8.GetBytes(path);
    }

    /// <summary>The socket, or null when one cannot be made here.</summary>
    public static JournalSocket? TryOpen(string path = DefaultPath, int sendBufferBytes = DefaultSendBufferBytes)
    {
        if (!OperatingSystem.IsLinux() || Encoding.UTF8.GetByteCount(path) >= SunPathLength)
            return null;

        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified) { Blocking = false };
            try
            {
                socket.SendBufferSize = sendBufferBytes;
            }
            catch (SocketException)
            {
                // Whatever the kernel grants; anything larger goes by memfd.
            }

            return new JournalSocket(socket, path);
        }
        catch
        {
            socket?.Dispose();
            return null;
        }
    }

    /// <summary>Sends <paramref name="message"/>; false when journald did not take it.</summary>
    public bool TrySend(byte[] message)
    {
        try
        {
            _socket.SendTo(message, SocketFlags.None, _endPoint);
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.MessageSize or SocketError.NoBufferSpaceAvailable)
        {
            return TrySendMemfd(message);
        }
        catch
        {
            return false;
        }
    }

    private bool TrySendMemfd(byte[] message)
    {
        if (!Environment.Is64BitProcess)
            return false;

        try
        {
            var fd = MemfdCreate("journal-message", MFD_CLOEXEC | MFD_ALLOW_SEALING);
            if (fd < 0)
                return false;

            using var memfd = new SafeFileHandle(fd, ownsHandle: true);
            RandomAccess.Write(memfd, message, fileOffset: 0);

            // journald takes only a memfd no one can change any more.
            if (Fcntl(fd, F_ADD_SEALS, F_SEAL_SEAL | F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE) < 0)
                return false;

            return SendDescriptor(fd);
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool SendDescriptor(int fd)
    {
        var address = new SockAddrUn { Family = AF_UNIX };
        _path.AsSpan().CopyTo(new Span<byte>(address.Path, SunPathLength));

        // One cmsghdr carrying one int, padded to the alignment of the next header: CMSG_SPACE(sizeof(int)).
        var controlLength = sizeof(CmsgHdr) + sizeof(nuint);
        var control = stackalloc byte[controlLength];
        new Span<byte>(control, controlLength).Clear();
        var header = (CmsgHdr*)control;
        header->Length = (nuint)(sizeof(CmsgHdr) + sizeof(int));
        header->Level = SOL_SOCKET;
        header->Type = SCM_RIGHTS;
        *(int*)(control + sizeof(CmsgHdr)) = fd;

        var message = new MsgHdr
        {
            Name = &address,
            NameLength = (uint)(sizeof(ushort) + _path.Length + 1),
            Control = control,
            ControlLength = (nuint)controlLength,
        };

        var added = false;
        try
        {
            _socket.SafeHandle.DangerousAddRef(ref added);
            return SendMsg((int)_socket.SafeHandle.DangerousGetHandle(), &message, MSG_NOSIGNAL | MSG_DONTWAIT) >= 0;
        }
        finally
        {
            if (added)
                _socket.SafeHandle.DangerousRelease();
        }
    }

    public void Dispose() => _socket.Dispose();
}
