using Nevolution.Core.Abstractions;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Nevolution.Infrastructure.Secrets;

public sealed class WindowsCredentialStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const string TargetPrefix = "Nevolution";

    public Task SetPasswordAsync(string accountId, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(password);

        var secretBytes = Encoding.UTF8.GetBytes(password);
        var credentialBlob = IntPtr.Zero;

        try
        {
            credentialBlob = Marshal.AllocHGlobal(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, credentialBlob, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = GetTargetName(accountId),
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = credentialBlob,
                Persist = CredPersistLocalMachine,
                UserName = accountId
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to store password for account '{accountId}' in Windows Credential Manager.");
            }
        }
        finally
        {
            if (credentialBlob != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(credentialBlob);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (!CredRead(GetTargetName(accountId), CredTypeGeneric, 0, out var credentialPtr))
        {
            var errorCode = Marshal.GetLastWin32Error();

            if (errorCode == 1168)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(errorCode, $"Unable to read password for account '{accountId}' from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);

            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(secretBytes));
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static string GetTargetName(string accountId)
    {
        return $"{TargetPrefix}:{accountId}";
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
