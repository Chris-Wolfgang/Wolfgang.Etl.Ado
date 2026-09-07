// Shadow-testing consumer — realistic DbClient workload for #130.
//
// Scenario: a paged extract-transform-load loop. 100k rows in a source
// SQLite table, paged out 1k at a time via server-side paging on the
// extractor, projected through a lightweight transform, and re-inserted
// into a destination table in batches of 100 via the loader's InsertBatchSize.
//
// The workload runs against SQLite in-memory so behaviour is deterministic
// across CI runners — the shadow-testing gate compares metrics, not
// wall-clock absolute values, so the SQLite path is fine as a stand-in for
// realistic ETL shapes even though production consumers hit SQL Server /
// Postgres / etc.
//
// Metrics printed to stdout as one JSON line at the end. shadow.yaml
// (follow-up PR) parses that line, diffs against a baseline recorded on
// gh-pages, and fails the run if latency or allocation regresses beyond a
// documented threshold.
//
// Refs #130.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Sqlite;
using Wolfgang.Etl.DbClient;
using Wolfgang.Etl.DbClient.Samples.ShadowConsumer;

// Configures through the deprecated property setters; migrating to the options constructors
// is follow-up work. Placed at the top of the file rather than before the namespace: these
// are top-level-statement programs, so the executable code precedes the namespace.
#pragma warning disable CS0618

const int totalRows = 100_000;
const int pageSize = 1_000;
const int batchSize = 100;

Console.Error.WriteLine($"[shadow] .NET {Environment.Version}  ServerGC={System.Runtime.GCSettings.IsServerGC}");
Console.Error.WriteLine($"[shadow] rows={totalRows} page={pageSize} batch={batchSize}");

using var src = new SqliteConnection("Data Source=:memory:");
using var dest = new SqliteConnection("Data Source=:memory:");
await src.OpenAsync();
await dest.OpenAsync();

using (var cmd = src.CreateCommand())
{
    cmd.CommandText = "CREATE TABLE widget (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL);";
    await cmd.ExecuteNonQueryAsync();
}

using (var cmd = dest.CreateCommand())
{
    cmd.CommandText = "CREATE TABLE widget_projected (id INTEGER PRIMARY KEY, upper_name TEXT NOT NULL, price REAL NOT NULL);";
    await cmd.ExecuteNonQueryAsync();
}
await SeedSourceAsync(src, totalRows);

// Baseline GC counters — everything after this point is measured.
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

var startAllocated = GC.GetTotalAllocatedBytes(precise: true);
var startGen0 = GC.CollectionCount(0);
var startGen1 = GC.CollectionCount(1);
var startGen2 = GC.CollectionCount(2);
var sw = Stopwatch.StartNew();

long extracted = 0;
long loaded = 0;

// One extractor walks the whole table: PageSize is the round-trip size and the extractor
// advances the offset itself. This used to be a caller-written for-loop rebuilding an extractor
// per page — when the sample and the tests both hand-roll the same loop, it belongs in the
// library. See docs/adr/0002-skip-max-and-paging.md.
var extractor = new DbExtractor<SourceWidget>
(
    src,
    "SELECT id AS Id, name AS Name, price AS Price FROM widget ORDER BY id"
)
{
    // The source here is SQLite. Paging syntax is dialect-specific and the library does not
    // guess one, so the dialect has to be named.
    PagingClauseTemplate = PagingClauseTemplates.Sqlite,
    PageSize = pageSize,
};

var loader = new DbLoader<DestWidget>
(
    dest,
    "INSERT INTO widget_projected (id, upper_name, price) VALUES (@Id, @UpperName, @Price)"
)
{
    InsertBatchSize = batchSize,
};

var page = new List<DestWidget>(pageSize);

await foreach (var s in extractor.ExtractAsync())
{
    page.Add(new DestWidget { Id = s.Id, UpperName = s.Name.ToUpperInvariant(), Price = s.Price });
    extracted++;

    // Load in batches of the same size, so the allocation profile stays comparable to the
    // per-page loop this replaced.
    if (page.Count == pageSize)
    {
        await loader.LoadAsync(AsAsync(page));
        loaded += page.Count;
        page.Clear();
    }
}

if (page.Count > 0)
{
    await loader.LoadAsync(AsAsync(page));
    loaded += page.Count;
}

sw.Stop();

var endAllocated = GC.GetTotalAllocatedBytes(precise: true);
var endGen0 = GC.CollectionCount(0);
var endGen1 = GC.CollectionCount(1);
var endGen2 = GC.CollectionCount(2);
var peakWs = Environment.WorkingSet;

var metrics = new
{
    schemaVersion = 1,
    runtime = Environment.Version.ToString(),
    serverGc = System.Runtime.GCSettings.IsServerGC,
    scenario = "paged-etl",
    rows = extracted,
    loaded,
    elapsedMs = sw.ElapsedMilliseconds,
    rowsPerSecond = extracted * 1000L / Math.Max(1L, sw.ElapsedMilliseconds),
    allocatedBytes = endAllocated - startAllocated,
    bytesPerRow = (endAllocated - startAllocated) / Math.Max(1L, extracted),
    gen0Collections = endGen0 - startGen0,
    gen1Collections = endGen1 - startGen1,
    gen2Collections = endGen2 - startGen2,
    peakWorkingSetBytes = peakWs,
};

// Human-readable summary to stderr; machine-readable JSON to stdout.
Console.Error.WriteLine($"[shadow] elapsed={sw.ElapsedMilliseconds}ms " +
    $"rows/s={metrics.rowsPerSecond:N0} " +
    $"alloc={metrics.allocatedBytes / 1024 / 1024}MB " +
    $"bytes/row={metrics.bytesPerRow:N0} " +
    $"gc={endGen0 - startGen0}/{endGen1 - startGen1}/{endGen2 - startGen2}");
Console.WriteLine(JsonSerializer.Serialize(metrics));


// -----------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------

static async Task SeedSourceAsync(SqliteConnection conn, int rowCount)
{
    using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "INSERT INTO widget (id, name, price) VALUES ($id, $name, $price)";
    var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
    var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
    var pPrice = cmd.CreateParameter(); pPrice.ParameterName = "$price"; cmd.Parameters.Add(pPrice);
    for (var i = 1; i <= rowCount; i++)
    {
        pId.Value = i;
        pName.Value = "widget-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        pPrice.Value = (i * 0.01) % 10.0;
        await cmd.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
}

static async IAsyncEnumerable<T> AsAsync<T>(IEnumerable<T> source)
{
    foreach (var item in source)
    {
        yield return item;
        if ((item is DestWidget dw) && (dw.Id & 0xFF) == 0)
        {
            await Task.Yield();
        }
    }
}


// -----------------------------------------------------------------
// Types (namespaced to satisfy MA0047)
// -----------------------------------------------------------------

// This file still configures through the deprecated property setters. Migrating it to the
// options constructors is follow-up work, tracked separately - the deprecation's purpose is
// to warn consumers, and the options constructors are covered by DbOptionsDefaultsTests.
// Several sites here assign after construction, so they cannot move to a constructor without
// restructuring the test.

namespace Wolfgang.Etl.DbClient.Samples.ShadowConsumer
{
    [UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
    internal sealed class SourceWidget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public double Price { get; set; }
    }

    [UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
    internal sealed class DestWidget
    {
        public int Id { get; set; }
        public string UpperName { get; set; } = "";
        public double Price { get; set; }
    }
}
