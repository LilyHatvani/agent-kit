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

    /// <summary>
    /// The preview of <see cref="Link"/>, used only when the post has no <see cref="Images"/>. Networks that build their
    /// own preview from the page ignore it.
    /// </summary>
    public LinkCard? Card { get; init; }

    /// <summary>
    /// Pictures posted as the network's own media (not a link card), in order. Each network's count, size and format
    /// limits are checked by <see cref="ISocialChannel.Compose"/>, which refuses what doesn't fit rather than dropping it.
    /// With pictures, the link goes in the text and no card is sent.
    /// </summary>
    public IReadOnlyList<SocialImage> Images { get; init; } = [];

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

/// <summary>A picture posted as native media.</summary>
public sealed record SocialImage
{
    /// <summary>The picture's bytes.</summary>
    public required ReadOnlyMemory<byte> Bytes { get; init; }

    /// <summary><c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or <c>image/webp</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>What the picture shows, for people who can't see it. Required: a picture without it is refused.</summary>
    public required string Alt { get; init; }

    /// <summary>The picture's width in pixels, when the host knows it (Bluesky lays the post out by its aspect ratio).</summary>
    public int? Width { get; init; }

    /// <summary>The picture's height in pixels, when the host knows it.</summary>
    public int? Height { get; init; }
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

    /// <summary>The card's accent colour as 0xRRGGBB, where a network draws one (Discord's embed bar); null for none.</summary>
    public int? Accent { get; init; }

    /// <summary>A line under the card, where a network shows one (Discord's embed footer, at most 2048 characters).</summary>
    public string? Footer { get; init; }

    /// <summary>When the linked thing was published, where a network shows it on the card (Discord's embed timestamp).</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}
