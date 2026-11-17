using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Lilt.Spotify;

namespace Lilt.App.Platform;

public sealed class SecureTokenStore : ITokenStore
{
    private const int MaximumFileSize = 64 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SecureTokenStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lilt");
        _path = Path.Combine(directory, "session.bin");
    }

    public async Task<SpotifySession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > MaximumFileSize) return null;
            var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            byte[]? clear = null;
            try
            {
                clear = Transform(encrypted, protect: false);
                return JsonSerializer.Deserialize<SpotifySession>(clear);
            }
            catch (CryptographicException) { return null; }
            catch (JsonException) { return null; }
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SpotifySession session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var clear = JsonSerializer.SerializeToUtf8Bytes(session);
            byte[] encrypted;
            try { encrypted = Transform(clear, protect: true); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            var temporary = Path.Combine(directory, $".session-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _gate.Release(); }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, "Lilt Spotify session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Length is < 0 or > MaximumFileSize) throw new CryptographicException("Invalid session data.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(input.Data, index, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var index = 0; index < Math.Clamp(output.Length, 0, MaximumFileSize); index++)
                    Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal int Length; internal IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
