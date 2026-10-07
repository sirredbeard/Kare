# Persistence, context, and cache

Checked 2026-10-01.

Use PostgreSQL with pgvector on NVMe.

Use Npgsql through EF Core for relational work and `Pgvector.EntityFrameworkCore` when its EF Core major version matches Kare.

PostgreSQL gives Kare concurrent writes, JSONB, full-text search, vector search, HNSW, constraints, and a direct restore path to Azure Database for PostgreSQL.

SQLite is simpler, however one writer is a poor fit for simultaneous session, cache, context, metrics, and accounting writes.

A document database adds cost and another query model without solving anything PostgreSQL cannot handle here.

Store:

Session and turn identity.

Raw local turn text when retention policy permits it.

Content-addressed source chunks.

Compacted checkpoints.

Embeddings and embedding model version.

Cache keys, expiration, validation, and provenance.

Route, token, latency, and cost accounting.

Model, runtime, tokenizer, skill, and instruction versions.

Do not store model files, compiled graphs, or every repository file in PostgreSQL. Keep large immutable files on NVMe and store hashes and metadata.

Cache deterministic artifacts first:

Tokenized immutable prefixes.

Embeddings.

File and symbol summaries keyed by content hash.

Skills and instructions keyed by version.

Compiled accelerator artifacts.

Validated command results with short expiration.

Final agent answers and patches should be off by default. A prompt match is not a safe cache key.

Compaction must preserve decisions, constraints, file and commit identifiers, failed approaches, unresolved questions, and source links.

Backup:

Create encrypted `pg_dump` archives.

Upload them to a private Azure Storage container.

Test restore into Azure Database for PostgreSQL Flexible Server with `CREATE EXTENSION vector`.

Use Azure logical replication only if the recovery-point target justifies a continuously running cloud database.

Azure PostgreSQL point-in-time backups only cover data already in Azure. They do not back up the board.

Sources:

https://github.com/pgvector/pgvector-dotnet

https://learn.microsoft.com/en-us/azure/postgresql/extensions/how-to-use-pgvector

https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore

https://learn.microsoft.com/en-us/ef/core/providers/

## Checked 2026-10-02, EF Core 11 package reality

Two open questions from the plan are now answered.

`Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0-rc.1.1 exists. The EF Core 11 provider is available, so the PostgreSQL plan is not blocked on waiting for a provider.

`Pgvector.EntityFrameworkCore` 0.3.0 only targets `net8.0` and depends on `Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.1. It cannot be used with the EF Core 11 provider without a downgrade. The plan's caution was correct.

Decision: reference the plain `Pgvector` type package for the vector CLR type, use Npgsql EF Core 11 for everything else, and run ANN queries through raw parameterized SQL. Raw SQL for the ANN query is not a workaround to feel bad about. The index hint, operator class, and `ef_search` tuning are all things you end up writing by hand anyway.
