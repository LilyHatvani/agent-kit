using System.Collections.Concurrent;

namespace AgentKit.Social;

/// <summary>Where an attempt stands.</summary>
public enum SocialAttemptState
{
    /// <summary>Begun and maybe sent; the answer hasn't been recorded.</summary>
    Pending,

    /// <summary>Up on the network.</summary>
    Posted,

    /// <summary>Refused before anything was created; a new attempt may start.</summary>
    Failed,
}

/// <summary>One attempt at one post, as the host's ledger stores it.</summary>
public sealed record SocialAttemptRecord
{
    /// <summary>The post's <see cref="SocialPost.IdempotencyKey"/>, the ledger's key.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The channel's <see cref="ISocialChannel.Id"/>.</summary>
    public required string ChannelId { get; init; }

    /// <summary>The attempt's <see cref="SocialAttempt.Token"/>.</summary>
    public required string Token { get; init; }

    /// <summary>When the attempt began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Where it stands.</summary>
    public SocialAttemptState State { get; init; }

    /// <summary>The network's id once posted.</summary>
    public string? PostId { get; init; }

    /// <summary>The post's address once posted.</summary>
    public Uri? PostUrl { get; init; }
}

/// <summary>
/// The host's record of attempts, so a retry after a lost answer finds the earlier try. Back it with the host's own
/// table, keyed by <see cref="SocialAttemptRecord.IdempotencyKey"/>.
/// </summary>
public interface ISocialPostLedger
{
    /// <summary>
    /// Stores <paramref name="attempt"/> unless the key already has a <see cref="SocialAttemptState.Pending"/> or
    /// <see cref="SocialAttemptState.Posted"/> record; a <see cref="SocialAttemptState.Failed"/> one is replaced. Returns the
    /// record stored now, which is the earlier one when there was one. Must be atomic (an insert with a conflict clause),
    /// so two retries racing on one key agree on one attempt.
    /// </summary>
    Task<SocialAttemptRecord> BeginAsync(SocialAttemptRecord attempt, CancellationToken cancellationToken);

    /// <summary>Records how an attempt ended (by its key).</summary>
    Task SaveAsync(SocialAttemptRecord record, CancellationToken cancellationToken);
}

/// <summary>A ledger in this process's memory: for tests and a single instance that may forget on restart.</summary>
public sealed class InMemorySocialPostLedger : ISocialPostLedger
{
    private readonly ConcurrentDictionary<string, SocialAttemptRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<SocialAttemptRecord> BeginAsync(SocialAttemptRecord attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (_records.TryGetValue(attempt.IdempotencyKey, out var existing) && existing.State != SocialAttemptState.Failed)
                return Task.FromResult(existing);
            _records[attempt.IdempotencyKey] = attempt;
            return Task.FromResult(attempt);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(SocialAttemptRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) _records[record.IdempotencyKey] = record;
        return Task.CompletedTask;
    }

    /// <summary>The record for a key, if any.</summary>
    public SocialAttemptRecord? Find(string idempotencyKey) => _records.GetValueOrDefault(idempotencyKey);
}

/// <summary>
/// Posts through a ledger, so the same post is never sent twice where the network allows it to be checked: a key that
/// is up answers with the earlier post and sends nothing; a key whose answer was lost resumes its attempt (Bluesky
/// reads the record back, Mastodon and a Discord bot resend inside the network's duplicate window); where nothing can
/// be checked the result is <see cref="SocialFailureKind.Unconfirmed"/> until a person asks to resend.
/// </summary>
public sealed class SocialPublisher(ISocialPostLedger ledger)
{
    /// <summary>
    /// Posts <paramref name="post"/> to <paramref name="channel"/> once. <paramref name="resendUnconfirmed"/>: a person
    /// checked the network after an <see cref="SocialFailureKind.Unconfirmed"/> and found nothing, so send again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key was used for another channel.</exception>
    public async Task<SocialPostResult> PostAsync(ISocialChannel channel, SocialPost post, bool resendUnconfirmed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(post);
        var composed = channel.Compose(post);
        if (!composed.Fits) return SocialPostResult.Failed(SocialFailureKind.Rejected, string.Join(" ", composed.Problems));
        // Before the row: a call cancelled before anything is sent leaves no pending attempt to be "unconfirmed" later.
        cancellationToken.ThrowIfCancellationRequested();

        var fresh = channel.BeginAttempt(post);
        var record = await ledger.BeginAsync(new SocialAttemptRecord
        {
            IdempotencyKey = post.IdempotencyKey,
            ChannelId = channel.Id,
            Token = fresh.Token,
            StartedAt = fresh.StartedAt,
            State = SocialAttemptState.Pending,
        }, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(record.ChannelId, channel.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"The idempotency key {post.IdempotencyKey} belongs to channel {record.ChannelId}, not {channel.Id}.");
        if (record.State == SocialAttemptState.Posted)
            return SocialPostResult.Success(record.PostId ?? "", record.PostUrl, alreadyPosted: true);

        // Every attempt's token is new (BeginAttempt), so an earlier record is one whose token isn't ours.
        var resuming = !string.Equals(record.Token, fresh.Token, StringComparison.Ordinal);
        var attempt = new SocialAttempt(record.Token, record.StartedAt) { Resuming = resuming, ResendUnconfirmed = resendUnconfirmed };
        var result = await channel.SendAsync(post, attempt, cancellationToken).ConfigureAwait(false);

        // The answer is recorded even if the caller gave up meanwhile: the post is up either way.
        if (result.Posted)
        {
            await ledger.SaveAsync(record with { State = SocialAttemptState.Posted, PostId = result.PostId, PostUrl = result.PostUrl },
                CancellationToken.None).ConfigureAwait(false);
        }
        else if (!resuming && result.Failure is { MayHavePosted: false, Kind: not SocialFailureKind.Unconfirmed })
        {
            // Nothing was created by the only try there was, so the next call starts afresh. A resumed attempt stays
            // pending whatever this try met: the earlier one may still be up.
            await ledger.SaveAsync(record with { State = SocialAttemptState.Failed }, CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }
}
