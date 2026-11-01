// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The libc calls .NET does not offer: a sealed <c>memfd</c> and <c>sendmsg</c> with the descriptor in
/// <c>SCM_RIGHTS</c> for the large-message path, and <c>fstat</c> to tell whether stdout is the journal.
/// Linux, 64-bit layouts only.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint MFD_CLOEXEC = 0x0001;
    public const uint MFD_ALLOW_SEALING = 0x0002;

    public const int F_ADD_SEALS = 1033;
    public const int F_SEAL_SEAL = 0x0001;
    public const int F_SEAL_SHRINK = 0x0002;
    public const int F_SEAL_GROW = 0x0004;
    public const int F_SEAL_WRITE = 0x0008;

    public const ushort AF_UNIX = 1;
    public const int SOL_SOCKET = 1;
    public const int SCM_RIGHTS = 1;
    public const int MSG_DONTWAIT = 0x40;
    public const int MSG_NOSIGNAL = 0x4000;

    /// <summary>The size of <c>sun_path</c>.</summary>
    public const int SunPathLength = 108;

    [LibraryImport("libc", EntryPoint = "memfd_create", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int MemfdCreate(string name, uint flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int Fcntl(int fd, int command, int argument);

    [LibraryImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial nint SendMsg(int socket, MsgHdr* message, int flags);

    /// <summary>
    /// Room for any <c>struct stat</c> (144 bytes on x86-64, 128 on arm64). Both begin with <c>st_dev</c> and
    /// <c>st_ino</c>, 64 bits each, which is all that is read.
    /// </summary>
    public const int StatBufferBytes = 256;

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int Fstat(int fd, byte* buffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct SockAddrUn
    {
        public ushort Family;
        public fixed byte Path[SunPathLength];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsgHdr
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
    public struct CmsgHdr
    {
        public nuint Length;
        public int Level;
        public int Type;
    }
}
