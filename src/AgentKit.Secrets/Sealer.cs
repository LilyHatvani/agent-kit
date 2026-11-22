using System.Security.Cryptography;
using System.Text;

namespace AgentKit.Secrets;

/// <summary>A sealed value as a row holds it: the ciphertext with its 16-byte tag, the 12-byte nonce, the id of the key.</summary>
public sealed record SealedValue(byte[] Ciphertext, byte[] Nonce, string KeyId);

/// <summary>Why a sealed value did not open.</summary>
public enum OpenFailure
{
    /// <summary>It opened.</summary>
    None,
    /// <summary>There is no master key at all.</summary>
    NoMasterKey,
    /// <summary>The key that sealed it is not in the keyring: add it to the previous keys.</summary>
    KeyNotInKeyring,
    /// <summary>The key is there, and it doesn't open: the row was altered, or copied from another row.</summary>
    DoesNotOpen,
}

/// <summary>
/// Seals values at rest: AES-256-GCM under the keyring's current key, a fresh 96-bit nonce per value, and associated
/// data that names the row the value belongs to. A ciphertext moved to another row, or altered, does not open.
/// </summary>
public sealed class Sealer(MasterKeys keys)
{
    private const int TagBytes = 16;
    private const int NonceBytes = 12;

    /// <summary>The keyring this sealer uses.</summary>
    public MasterKeys Keys => keys;

    /// <summary>Whether there is a master key, so values can be sealed.</summary>
    public bool On => keys.On;

    /// <summary>
    /// The associated data for a row: a version, then each part on its own line, e.g.
    /// <c>AssociatedData("relay-credential:v1", owner, integration, account, name)</c>. A part may not hold a line break,
    /// so no two rows share it.
    /// </summary>
    public static byte[] AssociatedData(string version, params ReadOnlySpan<string> parts)
    {
        var sb = new StringBuilder(version);
        foreach (var p in parts)
        {
            if (p.Contains('\n', StringComparison.Ordinal) || p.Contains('\r', StringComparison.Ordinal))
                throw new ArgumentException("A part of the associated data can't hold a line break.", nameof(parts));
            sb.Append('\n').Append(p);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Seals <paramref name="plaintext"/> under the current key. Throws when there is no master key.</summary>
    public SealedValue Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var key = keys.Current;
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var output = new byte[plaintext.Length + TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return new SealedValue(output, nonce, keys.CurrentId!);
    }

    /// <summary>Seals a string as UTF-8.</summary>
    public SealedValue Seal(string value, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plain = Encoding.UTF8.GetBytes(value);
        try { return Seal(plain, associatedData); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>The plaintext, or null when it does not open (<see cref="Why"/> says why).</summary>
    public byte[]? Open(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (keys.Find(value.KeyId) is not { } key || value.Ciphertext.Length < TagBytes || value.Nonce.Length != NonceBytes) return null;
        var plain = new byte[value.Ciphertext.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(value.Nonce, value.Ciphertext.AsSpan(0, plain.Length), value.Ciphertext.AsSpan(plain.Length), plain, associatedData);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>The string, or null when it does not open.</summary>
    public string? OpenText(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        if (Open(value, associatedData) is not { } plain) return null;
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Why <paramref name="value"/> would not open, for a message to the operator.</summary>
    public OpenFailure Why(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!keys.On) return OpenFailure.NoMasterKey;
        if (!keys.Has(value.KeyId)) return OpenFailure.KeyNotInKeyring;
        if (Open(value, associatedData) is not { } plain) return OpenFailure.DoesNotOpen;
        CryptographicOperations.ZeroMemory(plain);
        return OpenFailure.None;
    }
}
