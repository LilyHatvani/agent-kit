namespace AgentKit.Social;

/// <summary>
/// One post for one channel. The host writes it for its network (length, tone, tags); the channel adds the link and the
/// tags in its network's way and refuses, never truncates, a text that doesn't fit (<see cref="ISocialChannel.Compose"/>).
/// </summary>
public sealed record SocialPost
{
    /// <summary>The words of the post, without the link and the tags; the channel adds those.</summary>
    public required string Text { get; init; }

    /// <summary>The link the post shares. Added after the text unless the text already holds it.</summary>
    public Uri? Link { get; init; }

    /// <summary>The preview of <see cref="Link"/>. Networks that build their own preview from the page ignore it.</summary>
    public LinkCard? Card { get; init; }

    /// <summary>Hashtags, with or without <c>#</c>. Kept to letters, digits and <c>_</c>; one already in the text isn't added twice.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Names this post on this channel, the same on every retry: e.g. <c>blog-post:42:channel:7</c>. A retry with the same
    /// key never posts twice where the network allows it to be checked (<see cref="ISocialChannel.Idempotency"/>).
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The language of <see cref="Text"/>, a BCP 47 / ISO 639-1 code such as <c>en</c>; null to leave it unsaid.</summary>
    public string? Language { get; init; }
}

/// <summary>The preview card of a post's link: what a page's Open Graph tags say.</summary>
public sealed record LinkCard
{
    /// <summary>The page's title.</summary>
    public required string Title { get; init; }

    /// <summary>The page's description; empty for none.</summary>
    public string Description { get; init; } = "";

    /// <summary>The card picture's address (the page's <c>og:image</c>). Fetched only through a guard that connects to public addresses.</summary>
    public Uri? ImageUrl { get; init; }

    /// <summary>The card picture itself, when the host has it; used instead of <see cref="ImageUrl"/>.</summary>
    public ReadOnlyMemory<byte> Image { get; init; }

    /// <summary>The media type of <see cref="Image"/> (<c>image/png</c>…); null to tell it from the bytes.</summary>
    public string? ImageContentType { get; init; }

    /// <summary>What the picture shows, for networks that carry alt text.</summary>
    public string? ImageAlt { get; init; }
}
