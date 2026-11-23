# AgentKit.Export

"Export everything" for a Postgres-backed service, so leaving is never a project.

- **An allowlist per table.** `ExportTable.Of("post", "id blog_id slug …", scope: "blog_id = @scope", ("secret_hash", "a credential"))`:
  every column is exported or withheld with a reason; every table is exported or left out with one (`ExportPlan.NotExported`).
- **Checked against the live schema.** `plan.UndecidedAsync(connection)` lists every table or column the plan hasn't
  decided about, and every name it mentions that's gone. Put it in a test: a migration can't add a column that leaks,
  or a table that's silently missing, by default.
- **One document, one snapshot.** `plan.WriteAsync(connection, stream, scope, header)` writes
  `{ format, …header, counts, tables }` from a single read-only REPEATABLE READ transaction, rows streamed as read
  (`row_to_json`), counts first so a reader can check it got everything.
- **Read back.** `ExportDocument.ReadAsync(stream, format, maxBytes)` refuses another format, an oversized file and rows
  that don't match the counts. `table.InsertSql()` inserts a row through `jsonb_populate_record`, so every value comes
  back as exactly its column's type. What to remap (ids, owners) is the service's call; the rows are `JsonObject`s.

First written for Agent Blog (export a blog, import it into a self-hosted copy) after Relay's `Export.cs`, whose
allowlist and coverage check it generalises.
