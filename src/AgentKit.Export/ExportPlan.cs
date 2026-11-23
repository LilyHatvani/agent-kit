using System.Text.Json;
using Npgsql;

namespace AgentKit.Export;

/// <summary>
/// One exported table: the columns written, in order, and the columns deliberately left out, each with why. Every
/// column of the live table is in exactly one of the two (<see cref="ExportPlan.UndecidedAsync"/>), so a column added
/// later is neither exported nor dropped until someone decides.
/// </summary>
/// <param name="Name">The table.</param>
/// <param name="Columns">Columns written, in this order.</param>
/// <param name="Withheld">Columns left out, each with the reason.</param>
/// <param name="OrderBy">The rows' order in the document, an SQL expression list.</param>
/// <param name="Scope">An SQL condition choosing the rows (it may use the scope parameter as <c>@scope</c>); null: every row.</param>
public sealed record ExportTable(string Name, IReadOnlyList<string> Columns, IReadOnlyDictionary<string, string> Withheld,
    string OrderBy, string? Scope)
{
    /// <summary>A table from a space-separated column list.</summary>
    public static ExportTable Of(string name, string columns, string orderBy = "id", string? scope = null,
        params (string Column, string Why)[] withheld) =>
        new(name, columns.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            withheld.ToDictionary(w => w.Column, w => w.Why, StringComparer.Ordinal), orderBy, scope);

    internal string ColumnList => string.Join(", ", Columns.Select(Quote));

    /// <summary>
    /// <c>INSERT</c> of one exported row, given as the JSON object the document holds (parameter <c>@row</c>, jsonb):
    /// Postgres converts each value to its column's type, so what <c>row_to_json</c> wrote comes back exactly
    /// (timestamps, uuids, bytea, arrays, jsonb). Columns not exported take their defaults.
    /// </summary>
    public string InsertSql(string? onConflict = null) =>
        $"INSERT INTO {Quote(Name)} ({ColumnList}) SELECT {ColumnList} FROM jsonb_populate_record(NULL::{Quote(Name)}, @row::jsonb)"
        + (onConflict is null ? "" : " " + onConflict);

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// What a service exports: a format name (versioned, e.g. <c>agent-blog-export/1</c>), its tables in document order,
/// and the tables deliberately not exported, each with why.
/// </summary>
public sealed record ExportPlan(string Format, IReadOnlyList<ExportTable> Tables, IReadOnlyDictionary<string, string> NotExported)
{
    /// <summary>Flush to the output once this much is buffered, so a long history never sits in memory.</summary>
    private const int FlushAt = 64 * 1024;

    /// <summary>
    /// Everything the plan hasn't decided about the live schema: a table neither exported nor left out, a column of an
    /// exported table neither exported nor withheld, and a name the plan mentions that the schema doesn't have. Empty
    /// means the plan covers the schema exactly; a service checks it in a test, so a migration can't add a column that
    /// leaks (or vanishes) by default.
    /// </summary>
    public async Task<IReadOnlyList<string>> UndecidedAsync(NpgsqlConnection connection, string schema = "public", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var live = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.table_name, c.column_name FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema = @schema AND t.table_type = 'BASE TABLE' ORDER BY c.table_name, c.ordinal_position
            """, connection))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                if (!live.TryGetValue(table, out var columns)) live[table] = columns = [];
                columns.Add(reader.GetString(1));
            }
        }

        var problems = new List<string>();
        var exported = Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var (table, columns) in live)
        {
            if (exported.TryGetValue(table, out var t))
            {
                foreach (var c in columns.Where(c => !t.Columns.Contains(c) && !t.Withheld.ContainsKey(c)))
                    problems.Add($"{table}.{c}: neither exported nor withheld");
            }
            else if (!NotExported.ContainsKey(table))
            {
                problems.Add($"{table}: neither exported nor left out");
            }
        }
        foreach (var t in Tables)
        {
            if (!live.TryGetValue(t.Name, out var columns)) { problems.Add($"{t.Name}: exported, but no such table"); continue; }
            foreach (var c in t.Columns.Concat(t.Withheld.Keys).Where(c => !columns.Contains(c)))
                problems.Add($"{t.Name}.{c}: named in the plan, but no such column");
            foreach (var c in t.Columns.Where(t.Withheld.ContainsKey))
                problems.Add($"{t.Name}.{c}: both exported and withheld");
        }
        problems.AddRange(NotExported.Keys.Where(k => !live.ContainsKey(k)).Select(k => $"{k}: left out, but no such table"));
        return problems;
    }

    /// <summary>
    /// Writes the document <c>{ format, …header, counts, tables }</c> to <paramref name="output"/>, every table from one
    /// read-only REPEATABLE READ snapshot, rows streamed as they're read. <paramref name="scope"/> is the value of
    /// <c>@scope</c> in the tables' conditions (one blog's id, say). The counts are read first, in the same snapshot, so
    /// a reader can check it got everything.
    /// </summary>
    public async Task WriteAsync(NpgsqlConnection connection, Stream output, object? scope,
        IReadOnlyDictionary<string, string>? header = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(output);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await ro.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var counts = new List<(string Table, long Count)>();
        foreach (var t in Tables)
        {
            await using var cmd = Command($"SELECT count(*) FROM {ExportTable.Quote(t.Name)}{Where(t)}", connection, tx, scope);
            counts.Add((t.Name, (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!));
        }

        await using var w = new Utf8JsonWriter(output);
        w.WriteStartObject();
        w.WriteString("format", Format);
        foreach (var (key, value) in header ?? new Dictionary<string, string>()) w.WriteString(key, value);
        w.WriteStartObject("counts");
        foreach (var (table, count) in counts) w.WriteNumber(table, count);
        w.WriteEndObject();
        w.WriteStartObject("tables");
        foreach (var t in Tables)
        {
            w.WriteStartArray(t.Name);
            await using var cmd = Command(
                $"SELECT row_to_json(x)::text FROM (SELECT {t.ColumnList} FROM {ExportTable.Quote(t.Name)}{Where(t)} ORDER BY {t.OrderBy}) x",
                connection, tx, scope);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                w.WriteRawValue(reader.GetString(0), skipInputValidation: true);
                if (w.BytesPending >= FlushAt) await w.FlushAsync(ct).ConfigureAwait(false);
            }
            w.WriteEndArray();
            await w.FlushAsync(ct).ConfigureAwait(false);
        }
        w.WriteEndObject();
        w.WriteEndObject();
        await w.FlushAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string Where(ExportTable t) => t.Scope is null ? "" : $" WHERE {t.Scope}";

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction tx, object? scope)
    {
        var cmd = new NpgsqlCommand(sql, connection, tx);
        if (sql.Contains("@scope", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("scope", scope ?? DBNull.Value);
        return cmd;
    }
}
