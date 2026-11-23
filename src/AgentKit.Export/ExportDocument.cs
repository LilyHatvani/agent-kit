using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Export;

/// <summary>A document read back: its header fields, the counts it promised, and its rows by table.</summary>
public sealed class ExportDocument
{
    private ExportDocument(string format, IReadOnlyDictionary<string, string> header, IReadOnlyDictionary<string, long> counts,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> tables) =>
        (Format, Header, Counts, Tables) = (format, header, counts, tables);

    /// <summary>The document's format, e.g. <c>agent-blog-export/1</c>.</summary>
    public string Format { get; }

    /// <summary>The header's string fields besides the format (who exported it, from where, when).</summary>
    public IReadOnlyDictionary<string, string> Header { get; }

    /// <summary>How many rows of each table the document says it holds.</summary>
    public IReadOnlyDictionary<string, long> Counts { get; }

    /// <summary>The rows, by table, in document order within each.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> Tables { get; }

    /// <summary>A table's rows, or none.</summary>
    public IReadOnlyList<JsonObject> Rows(string table) => Tables.TryGetValue(table, out var rows) ? rows : [];

    /// <summary>
    /// Reads a document, refusing one of another format, one larger than <paramref name="maxBytes"/>, or one whose rows
    /// don't match its own counts (cut short or altered).
    /// </summary>
    public static async Task<ExportDocument> ReadAsync(Stream input, string format, long maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (limited.Length + read > maxBytes) throw new ExportFormatException($"The export is larger than {maxBytes} bytes.");
            limited.Write(buffer, 0, read);
        }
        limited.Position = 0;
        JsonNode? root;
        try { root = await JsonNode.ParseAsync(limited, cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException e) { throw new ExportFormatException("That isn't an export: it isn't JSON.", e); }
        if (root is not JsonObject doc || doc["format"]?.GetValueKind() != JsonValueKind.String)
            throw new ExportFormatException("That isn't an export: it names no format.");
        var found = doc["format"]!.GetValue<string>();
        if (found != format) throw new ExportFormatException($"That export is {found}; this reads {format}.");

        var header = doc.Where(p => p.Key is not ("format" or "counts" or "tables") && p.Value?.GetValueKind() == JsonValueKind.String)
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        var counts = (doc["counts"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<long>(), StringComparer.Ordinal);
        var tables = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.Ordinal);
        foreach (var (table, rows) in doc["tables"] as JsonObject ?? [])
        {
            var list = (rows as JsonArray ?? []).Select(r => r as JsonObject ?? throw new ExportFormatException($"{table}: a row isn't an object.")).ToList();
            if (!counts.TryGetValue(table, out var expected) || expected != list.Count)
                throw new ExportFormatException($"{table}: the export says {(counts.TryGetValue(table, out var c) ? c : 0)} rows and holds {list.Count}.");
            tables[table] = list;
        }
        foreach (var (table, count) in counts.Where(c => c.Value > 0 && !tables.ContainsKey(c.Key)))
            throw new ExportFormatException($"{table}: the export says {count} rows and holds none.");
        return new ExportDocument(found, header, counts, tables);
    }
}

/// <summary>A document that can't be read as the expected export; the message says why, in words for the person.</summary>
public sealed class ExportFormatException : Exception
{
    /// <inheritdoc />
    public ExportFormatException() { }

    /// <inheritdoc />
    public ExportFormatException(string message) : base(message) { }

    /// <inheritdoc />
    public ExportFormatException(string message, Exception inner) : base(message, inner) { }
}
