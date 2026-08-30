using System.Security.Cryptography;

namespace DshLauncher.Core;

public sealed class PairingLinkSecret : IDisposable
{
    private char[]? _ownedCharacters;

    private PairingLinkSecret(char[] ownedCharacters)
    {
        _ownedCharacters = ownedCharacters;
    }

    public bool IsCleared => _ownedCharacters is null;

    public static PairingLinkSecret TakeOwnership(char[] characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return new PairingLinkSecret(characters);
    }

    public void Clear()
    {
        var characters = Interlocked.Exchange(ref _ownedCharacters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    public void Dispose() => Clear();

    internal ReadOnlyMemory<char> DangerousGetMemory()
    {
        var characters = _ownedCharacters;
        ObjectDisposedException.ThrowIf(characters is null, this);

        return characters;
    }
}
