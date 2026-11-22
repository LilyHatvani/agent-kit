using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AgentKit.Secrets;

/// <summary>
/// Where a host keeps its keyring and how its keys are named.
/// </summary>
/// <param name="Section">The configuration section, e.g. <c>Relay:Secrets</c>. It holds <c>MasterKey</c> and <c>PreviousKeys</c>.</param>
/// <param name="IdLabel">
/// The label hashed in front of a key to name it, e.g. <c>relay-master-key-id:</c>. Rows store the id, so a host never
/// changes its label once a value is sealed.
/// </param>
/// <param name="HowToMakeOne">How the host's operator gets a key, said in every error about a malformed one.</param>
public sealed record MasterKeyOptions(string Section, string IdLabel, string HowToMakeOne = "openssl rand -hex 32 prints one")
{
    /// <summary>The environment variable of the current key, e.g. <c>Relay__Secrets__MasterKey</c>.</summary>
    public string Variable => Section.Replace(":", "__", StringComparison.Ordinal) + "__MasterKey";

    /// <summary>The environment variable of the older keys, comma-separated.</summary>
    public string PreviousVariable => Section.Replace(":", "__", StringComparison.Ordinal) + "__PreviousKeys";
}

/// <summary>
/// The keyring: <c>MasterKey</c>, 32 random bytes (base64 or 64 hex digits), seals every new value; <c>PreviousKeys</c>,
/// comma-separated keys in the same form, only open what an older key sealed until the host re-seals it under the
/// current one. Each key is named by its id, a hash of the key, so a row says which key sealed it without the database
/// holding anything that opens it. No master key: <see cref="On"/> is false and sealing is refused. A malformed key
/// throws at start, so a service never runs with a key it can't use.
/// </summary>
public sealed class MasterKeys
{
    private readonly Dictionary<string, byte[]> _keys;

    private MasterKeys(MasterKeyOptions options, string? currentId, Dictionary<string, byte[]> keys)
    {
        Options = options;
        CurrentId = currentId;
        _keys = keys;
    }

    /// <summary>Where the keyring was read from and how its keys are named.</summary>
    public MasterKeyOptions Options { get; }

    /// <summary>The id of the key that seals new values, or null when there is no master key.</summary>
    public string? CurrentId { get; }

    /// <summary>Whether there is a master key, so values can be sealed.</summary>
    public bool On => CurrentId is not null;

    /// <summary>Every key id in the keyring, the current one first.</summary>
    public IReadOnlyList<string> Ids => [.. _keys.Keys.OrderBy(k => k == CurrentId ? 0 : 1)];

    /// <summary>Whether a key with this id is in the keyring.</summary>
    public bool Has(string keyId) => _keys.ContainsKey(keyId);

    internal byte[] Current => _keys[CurrentId ?? throw new InvalidOperationException($"No master key: set {Options.Variable}.")];

    internal byte[]? Find(string keyId) => _keys.GetValueOrDefault(keyId);

    /// <summary>Reads the keyring from <paramref name="config"/> under <see cref="MasterKeyOptions.Section"/>.</summary>
    public static MasterKeys From(IConfiguration config, MasterKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        var s = config.GetSection(options.Section);
        var master = s["MasterKey"] is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : null;
        var previous = (s["PreviousKeys"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (master is null)
        {
            if (previous.Length > 0)
                throw new InvalidOperationException($"{options.PreviousVariable} is set without {options.Variable}: set the current key too.");
            return new MasterKeys(options, null, []);
        }
        var keys = new Dictionary<string, byte[]>();
        var current = Parse(master, options.Variable, options);
        var currentId = IdOf(current, options.IdLabel);
        keys[currentId] = current;
        foreach (var p in previous)
        {
            var key = Parse(p, options.PreviousVariable, options);
            keys.TryAdd(IdOf(key, options.IdLabel), key);
        }
        return new MasterKeys(options, currentId, keys);
    }

    /// <summary>A fresh master key, base64.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary><c>k</c> plus 16 hex digits of SHA-256 over the label and the key: names the key, opens nothing.</summary>
    public static string IdOf(byte[] key, string idLabel) =>
        "k" + Convert.ToHexStringLower(SHA256.HashData([.. Encoding.ASCII.GetBytes(idLabel), .. key]))[..16];

    /// <summary>
    /// Base64 of 32 bytes, or 64 hex digits (what a host's secret generator such as Railway's <c>secret(64, hex)</c> or
    /// <c>openssl rand -hex 32</c> gives). The two never collide: 32 bytes in base64 are 44 characters.
    /// </summary>
    private static byte[] Parse(string value, string variable, MasterKeyOptions options)
    {
        var message = $"{variable} must be 32 random bytes, as base64 or 64 hex digits ({options.HowToMakeOne})";
        byte[] key;
        try { key = value.Length == 64 ? Convert.FromHexString(value) : Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidOperationException(message + "."); }
        return key.Length == 32 ? key : throw new InvalidOperationException($"{message}; this one is {key.Length} bytes.");
    }
}
