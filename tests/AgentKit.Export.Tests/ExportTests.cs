using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace AgentKit.Export.Tests;

/// <summary>Against a real Postgres: a scoped export comes back exactly, leaks nothing withheld, and the plan's gaps show.</summary>
public sealed class ExportTests : IAsyncLifetime
{
    private static string Admin => Environment.GetEnvironmentVariable("AGENTKIT_TEST_ADMIN_CONNECTION")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
    private readonly string _db = "agentkit_export_" + Guid.NewGuid().ToString("N")[..12];
    private string Cs => new NpgsqlConnectionStringBuilder(Admin) { Database = _db, Pooling = false }.ConnectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid A = Guid.Parse("01940000-0000-7000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("01940000-0000-7000-8000-00000000000b");

    private const string Schema = """
        CREATE TABLE blog (id uuid PRIMARY KEY, title text NOT NULL, settings jsonb NOT NULL DEFAULT '{}', secret_hash bytea);
        CREATE TABLE post (id uuid PRIMARY KEY, blog_id uuid NOT NULL REFERENCES blog (id), title text NOT NULL,
            at timestamptz NOT NULL, tags text[] NOT NULL, body bytea, score numeric, payload jsonb);
        CREATE TABLE session (id bigserial PRIMARY KEY, token text NOT NULL);
        """;

    private static readonly ExportPlan Plan = new("test-export/1",
    [
        ExportTable.Of("blog", "id title settings", scope: "id = @scope", withheld: ("secret_hash", "a credential")),
        ExportTable.Of("post", "id blog_id title at tags body score payload", orderBy: "at, id", scope: "blog_id = @scope"),
    ], new Dictionary<string, string> { ["session"] = "a sign-in: per instance" });

    public async ValueTask InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(Admin)) { await admin.OpenAsync(Ct); await Exec(admin, $"CREATE DATABASE {_db}"); }
        await using var c = await OpenAsync();
        await Exec(c, Schema);
        await Exec(c, $$"""
            INSERT INTO blog VALUES ('{{A}}', 'Mine', '{"theme":"midnight"}', '\xdeadbeef'), ('{{B}}', 'Theirs', '{}', NULL);
            INSERT INTO post VALUES
              ('01940000-0000-7000-8000-000000000101', '{{A}}', 'First "quoted" — ünïcode', '2026-11-23T10:00:00.123456+00', '{a,"b c"}', '\x00ff10', 1.25, '{"n":[1,2]}'),
              ('01940000-0000-7000-8000-000000000102', '{{A}}', 'Second', '2026-11-24T10:00:00+02', '{}', NULL, NULL, NULL),
              ('01940000-0000-7000-8000-000000000201', '{{B}}', 'Not mine', '2026-11-23T11:00:00+00', '{x}', NULL, NULL, NULL);
            INSERT INTO session (token) VALUES ('top-secret-token');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        await Exec(admin, $"DROP DATABASE IF EXISTS {_db} WITH (FORCE)");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(Ct);
        return c;
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<(byte[] Bytes, ExportDocument Doc)> ExportAsync(Guid scope)
    {
        await using var c = await OpenAsync();
        using var ms = new MemoryStream();
        await Plan.WriteAsync(c, ms, scope, new Dictionary<string, string> { ["exported_by"] = "test" }, Ct);
        var bytes = ms.ToArray();
        return (bytes, await ExportDocument.ReadAsync(new MemoryStream(bytes), Plan.Format, 1 << 20, Ct));
    }

    private static async Task<string> RowsJsonAsync(NpgsqlConnection c, string table, string where) =>
        await new NpgsqlCommand($"SELECT coalesce(json_agg(x ORDER BY x.id)::text, '[]') FROM (SELECT * FROM {table} WHERE {where}) x", c)
            .ExecuteScalarAsync(Ct) as string ?? "";

    [Fact]
    public async Task A_scoped_export_comes_back_exactly_and_holds_nothing_withheld_or_out_of_scope()
    {
        var (bytes, doc) = await ExportAsync(A);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal("test-export/1", doc.Format);
        Assert.Equal("test", doc.Header["exported_by"]);
        Assert.Equal(1, doc.Counts["blog"]);
        Assert.Equal(2, doc.Rows("post").Count);
        Assert.DoesNotContain("Not mine", text);
        Assert.DoesNotContain("Theirs", text);
        Assert.DoesNotContain("deadbeef", text);
        Assert.DoesNotContain("secret_hash", text);
        Assert.DoesNotContain("top-secret-token", text);
        Assert.False(doc.Tables.ContainsKey("session"));

        // Into an empty copy of the schema, row by row through Postgres's own conversion: identical.
        await using var c = await OpenAsync();
        var before = (await RowsJsonAsync(c, "blog", $"id = '{A}'"), await RowsJsonAsync(c, "post", $"blog_id = '{A}'"));
        await Exec(c, "CREATE SCHEMA copy; SET search_path = copy;" + Schema);
        foreach (var table in Plan.Tables)
            foreach (var row in doc.Rows(table.Name))
            {
                await using var insert = new NpgsqlCommand(table.InsertSql(), c);
                insert.Parameters.AddWithValue("row", NpgsqlTypes.NpgsqlDbType.Jsonb, row.ToJsonString());
                await insert.ExecuteNonQueryAsync(Ct);
            }
        var blogAfter = await RowsJsonAsync(c, "blog", $"id = '{A}'");
        Assert.Equal(before.Item1.Replace("\"secret_hash\":\"\\\\xdeadbeef\"", "\"secret_hash\":null", StringComparison.Ordinal), blogAfter);
        Assert.Equal(before.Item2, await RowsJsonAsync(c, "post", $"blog_id = '{A}'"));
    }

    [Fact]
    public async Task The_plan_shows_every_table_and_column_it_has_not_decided_about()
    {
        await using var c = await OpenAsync();
        Assert.Empty(await Plan.UndecidedAsync(c, ct: Ct));

        await Exec(c, "ALTER TABLE post ADD COLUMN api_key text; CREATE TABLE audit (id int); ALTER TABLE blog DROP COLUMN settings;");
        var gaps = await Plan.UndecidedAsync(c, ct: Ct);
        Assert.Contains("post.api_key: neither exported nor withheld", gaps);
        Assert.Contains("audit: neither exported nor left out", gaps);
        Assert.Contains("blog.settings: named in the plan, but no such column", gaps);
    }

    [Fact]
    public async Task Reading_refuses_another_format_a_cut_document_and_an_oversized_one()
    {
        var (bytes, _) = await ExportAsync(A);
        await Assert.ThrowsAsync<ExportFormatException>(() => ExportDocument.ReadAsync(new MemoryStream(bytes), "other/1", 1 << 20, Ct));
        await Assert.ThrowsAsync<ExportFormatException>(() => ExportDocument.ReadAsync(new MemoryStream(bytes), Plan.Format, bytes.Length - 1, Ct));

        var doc = JsonNode.Parse(bytes)!.AsObject();
        doc["tables"]!["post"]!.AsArray().RemoveAt(0);
        var cut = Encoding.UTF8.GetBytes(doc.ToJsonString());
        var e = await Assert.ThrowsAsync<ExportFormatException>(() => ExportDocument.ReadAsync(new MemoryStream(cut), Plan.Format, 1 << 20, Ct));
        Assert.Contains("says 2 rows and holds 1", e.Message);
        await Assert.ThrowsAsync<ExportFormatException>(() => ExportDocument.ReadAsync(new MemoryStream("nope"u8.ToArray()), Plan.Format, 1 << 20, Ct));
    }
}
