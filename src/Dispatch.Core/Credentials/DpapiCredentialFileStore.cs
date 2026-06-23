using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core.Credentials;

internal static class DpapiCredentialFileStore
{
    private const int CurrentVersion = 1;
    private const int CryptProtectUiForbidden = 0x1;

    public static void Write(
        string referenceName,
        string userName,
        string path,
        SecureString password,
        bool overwrite)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI credential files are supported on Windows only.");
        }

        if (password.Length == 0)
        {
            throw new InvalidOperationException("DPAPI credential enrollment requires a non-empty password.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plaintext = SecureStringToUtf8Bytes(password);
        try
        {
            var protectedBytes = Protect(plaintext, $"Dispatch:{referenceName}");
            try
            {
                var file = new DpapiCredentialFile(
                    CurrentVersion,
                    "dpapi_file",
                    referenceName,
                    userName,
                    "current_user",
                    Convert.ToBase64String(protectedBytes),
                    DateTimeOffset.UtcNow);
                var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
                var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
                using (var stream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                }

                ApplyRestrictiveFileAcl(fullPath);
            }
            finally
            {
                CryptographicOperationsZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperationsZeroMemory(plaintext);
        }
    }

    public static SecureString ReadPassword(string referenceName, string userName, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI credential files are supported on Windows only.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"DPAPI credential file '{fullPath}' was not found.", fullPath);
        }

        var file = JsonSerializer.Deserialize<DpapiCredentialFile>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException($"DPAPI credential file '{fullPath}' is empty or invalid.");
        if (file.Version != CurrentVersion
            || !file.Provider.Equals("dpapi_file", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"DPAPI credential file '{fullPath}' is not a supported Dispatch credential file.");
        }

        if (!file.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"DPAPI credential file '{fullPath}' is for credential '{file.ReferenceName}', not '{referenceName}'.");
        }

        if (!file.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"DPAPI credential file '{fullPath}' username does not match Dispatch config.");
        }

        var protectedBytes = Convert.FromBase64String(file.ProtectedValue);
        var plaintext = Unprotect(protectedBytes, $"Dispatch:{referenceName}");
        try
        {
            var password = Utf8BytesToSecureString(plaintext);
            password.MakeReadOnly();
            return password;
        }
        finally
        {
            CryptographicOperationsZeroMemory(plaintext);
            CryptographicOperationsZeroMemory(protectedBytes);
        }
    }

    public static void Delete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
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

    private static byte[] Protect(byte[] plaintext, string description)
    {
        using var input = DataBlob.FromBytes(plaintext);
        if (!CryptProtectData(
                ref input.Blob,
                description,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new InvalidOperationException($"DPAPI protect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return CopyAndFree(output);
    }

    private static byte[] Unprotect(byte[] protectedBytes, string description)
    {
        using var input = DataBlob.FromBytes(protectedBytes);
        var dataDescription = IntPtr.Zero;
        if (!CryptUnprotectData(
                ref input.Blob,
                out dataDescription,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new InvalidOperationException($"DPAPI unprotect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            return CopyAndFree(output);
        }
        finally
        {
            if (dataDescription != IntPtr.Zero)
            {
                LocalFree(dataDescription);
            }
        }
    }

    private static byte[] CopyAndFree(DataBlobStruct blob)
    {
        try
        {
            if (blob.DataPointer == IntPtr.Zero || blob.DataLength <= 0)
            {
                return [];
            }

            var bytes = new byte[blob.DataLength];
            Marshal.Copy(blob.DataPointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (blob.DataPointer != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(blob.DataPointer, blob.DataLength);
                LocalFree(blob.DataPointer);
            }
        }
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

    [SupportedOSPlatform("windows")]
    private static void ApplyRestrictiveFileAcl(string fullPath)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not determine the current Windows user SID for DPAPI credential file ACL hardening.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var fileSecurity = new FileSecurity();
        fileSecurity.SetOwner(currentUser);
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(localSystem, FileSystemRights.FullControl, AccessControlType.Allow));

        new FileInfo(fullPath).SetAccessControl(fileSecurity);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlobStruct dataIn,
        string? dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlobStruct dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlobStruct dataIn,
        out IntPtr dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlobStruct dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlobStruct
    {
        public int DataLength;
        public IntPtr DataPointer;
    }

    private sealed class DataBlob : IDisposable
    {
        private DataBlob(IntPtr pointer, int length)
        {
            Blob = new DataBlobStruct
            {
                DataLength = length,
                DataPointer = pointer
            };
        }

        public DataBlobStruct Blob;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new DataBlob(pointer, bytes.Length);
        }

        public void Dispose()
        {
            if (Blob.DataPointer != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(Blob.DataPointer, Blob.DataLength);
                Marshal.FreeHGlobal(Blob.DataPointer);
                Blob.DataPointer = IntPtr.Zero;
                Blob.DataLength = 0;
            }
        }
    }

    private sealed record DpapiCredentialFile(
        int Version,
        string Provider,
        string ReferenceName,
        string UserName,
        string Protection,
        string ProtectedValue,
        DateTimeOffset CreatedAt);

    private static void ZeroUnmanagedMemory(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }
}
