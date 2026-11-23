using System.Diagnostics.CodeAnalysis;

namespace AgentKit.Channels;

/// <summary>
/// A Discord channel webhook's address, read once: its id (not secret) and its token (the credential). The one reading
/// of a webhook URL in this repository: <see cref="Discord.WebhookRefusal"/> and AgentKit.Social's <c>DiscordWebhook</c>
/// are both built on it. It takes <c>https://discord.com/api/webhooks/{id}/{token}</c> as the channel's Integrations
/// page hands it out, on <c>discord.com</c>, <c>discordapp.com</c>, <c>canary.</c> or <c>ptb.discord.com</c>, with or without
/// an API version (<c>/api/v10/webhooks/…</c>). Not a record, and <see cref="ToString"/> hides the token, so printing
/// one never writes the credential; no refusal repeats the URL.
/// </summary>
public sealed class DiscordWebhookAddress
{
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    /// <summary>The refusal for an address that isn't on Discord's https host.</summary>
    internal const string HostRefusal = "A Discord webhook URL starts with https://discord.com/api/webhooks/.";

    /// <summary>The refusal for a Discord address that isn't a webhook's.</summary>
    internal const string ShapeRefusal = "That isn't a Discord webhook URL: copy it from the channel's Integrations → Webhooks.";

    /// <summary>The versioned API every canonical address points at.</summary>
    public static readonly Uri Api = new("https://discord.com/api/v10/");

    private DiscordWebhookAddress(string id, string token)
    {
        Id = id;
        Token = token;
    }

    /// <summary>The webhook's id, a snowflake (not secret).</summary>
    public string Id { get; }

    /// <summary>The webhook's token: whoever has it can post as the webhook. Keep it out of logs and messages.</summary>
    public string Token { get; }

    /// <summary>The webhook on Discord's versioned API, <c>https://discord.com/api/v10/webhooks/{id}/{token}</c> (Get Webhook with Token).</summary>
    public Uri Uri => new(Api, $"webhooks/{Id}/{Token}");

    /// <summary>Execute Webhook with <c>?wait=true</c>, so Discord answers only once the message is posted, with the message.</summary>
    public Uri ExecuteUri => new(Api, $"webhooks/{Id}/{Token}?wait=true");

    /// <summary>
    /// Reads <paramref name="url"/>, or says why it isn't a Discord webhook URL: https on one of Discord's hosts with the
    /// default port and no user name; the path <c>/api/webhooks/{id}/{token}</c> or <c>/api/v{n}/webhooks/{id}/{token}</c>;
    /// an id of ASCII digits that fits a snowflake; a token that isn't empty. The refusal never repeats the URL.
    /// </summary>
    public static bool TryParse(Uri url, [NotNullWhen(true)] out DiscordWebhookAddress? address, [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(url);
        address = null;
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase)
            || url.UserInfo.Length > 0 || !url.IsDefaultPort)
        {
            refusal = HostRefusal;
            return false;
        }
        var path = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = path is ["api", "webhooks", ..] ? 2 : path is ["api", var version, "webhooks", ..] && IsVersion(version) ? 3 : -1;
        if (at < 0 || path.Length != at + 2 || !IsSnowflake(path[at]) || path[at + 1].Length == 0)
        {
            refusal = ShapeRefusal;
            return false;
        }
        address = new DiscordWebhookAddress(path[at], path[at + 1]);
        refusal = null;
        return true;
    }

    /// <summary>Reads <paramref name="url"/>; an <see cref="ArgumentException"/> that never repeats it when it isn't a Discord webhook URL.</summary>
    public static DiscordWebhookAddress Parse(Uri url) =>
        TryParse(url, out var address, out var refusal) ? address : throw new ArgumentException(refusal, nameof(url));

    /// <inheritdoc />
    public override string ToString() => $"Discord webhook {Id} (token hidden)";

    /// <summary><c>v</c> and one or more ASCII digits.</summary>
    private static bool IsVersion(string s) => s.Length > 1 && s[0] == 'v' && !s.AsSpan(1).ContainsAnyExceptInRange('0', '9');

    /// <summary>ASCII digits only (no sign, no spaces, which <see cref="ulong.TryParse(string?, out ulong)"/> would take) that fit 64 bits.</summary>
    private static bool IsSnowflake(string s) =>
        s.Length is > 0 and <= 20 && !s.AsSpan().ContainsAnyExceptInRange('0', '9') && ulong.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
}
