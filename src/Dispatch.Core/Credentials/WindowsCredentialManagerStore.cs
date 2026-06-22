using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace Dispatch.Core.Credentials;

internal static class WindowsCredentialManagerStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static void Write(
        string target,
        string userName,
        SecureString password,
        bool overwrite)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager credentials are supported on Windows only.");
        }

        if (password.Length == 0)
        {
            throw new InvalidOperationException("Windows Credential Manager enrollment requires a non-empty password.");
        }

        if (!overwrite && Exists(target))
        {
            throw new IOException($"Windows Credential Manager target '{target}' already exists.");
        }

        var secretBytes = SecureStringToUtf8Bytes(password);
        var secretPointer = IntPtr.Zero;
        try
        {
            secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager write failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            CryptographicOperationsZeroMemory(secretBytes);
            if (secretPointer != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(secretPointer, secretBytes.Length);
                Marshal.FreeCoTaskMem(secretPointer);
            }
        }
    }

    public static SecureString ReadPassword(string target, string userName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager credentials are supported on Windows only.");
        }

        var credentialPointer = ReadCredentialPointer(target);
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (!string.Equals(credential.UserName, userName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Windows Credential Manager target '{target}' username does not match Dispatch config.");
            }

            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                throw new InvalidDataException($"Windows Credential Manager target '{target}' does not contain a credential secret.");
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            try
            {
                var password = Utf8BytesToSecureString(secretBytes);
                password.MakeReadOnly();
                return password;
            }
            finally
            {
                CryptographicOperationsZeroMemory(secretBytes);
                ZeroUnmanagedMemory(credential.CredentialBlob, (int)credential.CredentialBlobSize);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void Delete(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager credentials are supported on Windows only.");
        }

        if (!CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new IOException($"Windows Credential Manager delete failed with Win32 error {error}.");
            }
        }
    }

    public static bool Exists(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager credentials are supported on Windows only.");
        }

        if (CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            CredFree(credentialPointer);
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new IOException($"Windows Credential Manager read failed with Win32 error {error}.");
    }

    public static bool PasswordsEqual(SecureString left, SecureString right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var leftBytes = SecureStringToUtf8Bytes(left);
        var rightBytes = SecureStringToUtf8Bytes(right);
        try
        {
            return FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperationsZeroMemory(leftBytes);
            CryptographicOperationsZeroMemory(rightBytes);
        }
    }

    private static IntPtr ReadCredentialPointer(string target)
    {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            throw error == ErrorNotFound
                ? new InvalidOperationException($"Windows Credential Manager target '{target}' was not found.")
                : new IOException($"Windows Credential Manager read failed with Win32 error {error}.");
        }

        return credentialPointer;
    }

    private static SecureString Utf8BytesToSecureString(byte[] bytes)
    {
        var chars = Encoding.UTF8.GetChars(bytes);
        try
        {
            var secureString = new SecureString();
            foreach (var character in chars)
            {
                secureString.AppendChar(character);
            }

            return secureString;
        }
        finally
        {
            Array.Clear(chars, 0, chars.Length);
        }
    }

    private static byte[] SecureStringToUtf8Bytes(SecureString value)
    {
        var pointer = IntPtr.Zero;
        char[]? chars = null;
        try
        {
            pointer = Marshal.SecureStringToBSTR(value);
            var length = Marshal.ReadInt32(pointer, -4) / sizeof(char);
            chars = new char[length];
            for (var index = 0; index < chars.Length; index++)
            {
                chars[index] = (char)Marshal.ReadInt16(pointer, index * sizeof(char));
            }

            return Encoding.UTF8.GetBytes(chars);
        }
        finally
        {
            if (chars is not null)
            {
                Array.Clear(chars, 0, chars.Length);
            }

            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(pointer);
            }
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }

    private static void CryptographicOperationsZeroMemory(byte[] bytes)
    {
        Array.Clear(bytes, 0, bytes.Length);
    }

    private static void ZeroUnmanagedMemory(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}
