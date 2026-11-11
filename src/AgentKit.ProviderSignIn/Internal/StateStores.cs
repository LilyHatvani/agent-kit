using System.Collections.Concurrent;
using Npgsql;

namespace AgentKit.ProviderSignIn.Internal;

/// <summary>Sign-ins in flight in this process: lost on restart, not shared between replicas.</summary>
internal sealed class InMemoryStateStore : IProviderSignInStateStore
{
    private readonly ConcurrentDictionary<string, ProviderSignInState> _states = new(StringComparer.Ordinal);

    public Task SaveAsync(ProviderSignInState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var (key, old) in _states)
            if (old.ExpiresAt <= now) _states.TryRemove(key, out _);
        if (!_states.TryAdd(state.Key, state)) throw new InvalidOperationException("A sign-in state collided with one in flight.");
        return Task.CompletedTask;
    }

    public Task<ProviderSignInState?> TakeAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_states.TryRemove(key, out var state) ? state : null);
}

/// <summary>Sign-ins in flight in <c>provider_signin_state</c> (<see cref="ProviderSignInSchema"/>).</summary>
internal sealed class PostgresStateStore(string connectionString) : IProviderSignInStateStore
{
    public async Task SaveAsync(ProviderSignInState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(conn);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM provider_signin_state WHERE expires_at <= $1") { Parameters = { new() { Value = now } } });
        batch.BatchCommands.Add(new NpgsqlBatchCommand("""
            INSERT INTO provider_signin_state (key, provider, purpose, data, return_to, code_verifier, nonce, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """)
        {
            Parameters =
            {
                new() { Value = state.Key },
                new() { Value = state.Provider },
                new() { Value = state.Purpose },
                new() { Value = (object?)state.Data ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                new() { Value = (object?)state.ReturnTo ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                new() { Value = state.CodeVerifier },
                new() { Value = state.Nonce },
                new() { Value = state.CreatedAt },
                new() { Value = state.ExpiresAt },
            },
        });
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProviderSignInState?> TakeAsync(string key, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM provider_signin_state WHERE key = $1
            RETURNING key, provider, purpose, data, return_to, code_verifier, nonce, created_at, expires_at
            """, conn) { Parameters = { new() { Value = key } } };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ProviderSignInState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }
}
