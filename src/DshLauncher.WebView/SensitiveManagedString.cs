using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DshLauncher.WebView;

internal sealed class SensitiveManagedString : IDisposable
{
    private string? _value;

    private SensitiveManagedString(string value)
    {
        _value = value;
    }

    public static SensitiveManagedString Create(
        ReadOnlySpan<char> prefix,
        ReadOnlySpan<char> secret)
    {
        var characters = new char[prefix.Length + secret.Length];
        try
        {
            prefix.CopyTo(characters);
            secret.CopyTo(characters.AsSpan(prefix.Length));
            return new SensitiveManagedString(new string(characters));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    public void UseAndClear(Action<string> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var value = Interlocked.Exchange(ref _value, null) ??
            throw new ObjectDisposedException(nameof(SensitiveManagedString));
        try
        {
            consumer(value);
        }
        finally
        {
            Clear(value);
        }
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
        {
            Clear(value);
        }
    }

    private static unsafe void Clear(string value)
    {
        fixed (char* first = value)
        {
            new Span<char>(first, value.Length).Clear();
        }
    }
}
