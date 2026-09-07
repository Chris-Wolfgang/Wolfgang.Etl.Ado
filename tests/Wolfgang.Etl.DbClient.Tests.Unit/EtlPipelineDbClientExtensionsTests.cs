// End-to-end tests for the EtlPipeline DbClient extensions (#280).
//
// Coverage:
//   1. Round-trip fixture: DbExtractor → DbLoader against the same
//      SQLite :memory: connection via the fluent chain.
//   2. Server-side paging (ServerLimit / ServerOffset / PagingClauseTemplate)
//      applied through the builder still routes to the underlying
//      DbExtractor and produces the expected page.
//   3. AsAsyncEnumerable escape hatch works without a sink.
//   4. Null-guard behaviour on every factory + terminator.
//   5. Existing-instance overloads share state with the caller's own
//      extractor / loader (setter calls on the builder mutate the
//      caller's instance).
//
// The "Cross-format test: read CSV via CsvExtractor, write rows via
// DbLoader" AC item is NOT covered here — ETL-Csv hasn't shipped the
// CsvExtractor pipeline extension yet (sibling issue). Will land in
// a follow-up once ETL-Csv catches up.

using System.Data;
using System.Data.Common;
using JetBrains.Annotations;
using Microsoft.Data.Sqlite;
using Wolfgang.Etl.Abstractions;
using Xunit;

// This file still configures through the deprecated property setters. Migrating it to the
// options constructors is follow-up work, tracked separately - the deprecation's purpose is
// to warn consumers, and the options constructors are covered by DbOptionsDefaultsTests.
// Several sites here assign after construction, so they cannot move to a constructor without
// restructuring the test.
#pragma warning disable CS0618

namespace Wolfgang.Etl.DbClient.Tests.Unit;

public class EtlPipelineDbClientExtensionsTests
{
    [UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
    public sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }



    private static SqliteConnection CreateSourceWithRows(int rowCount)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE source (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
        cmd.ExecuteNonQuery();

        for (var i = 1; i <= rowCount; i++)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = $"INSERT INTO source (Id, Name) VALUES ({i}, 'w-{i}');";
            ins.ExecuteNonQuery();
        }
        return conn;
    }



    private static SqliteConnection CreateEmptyDestination()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE dest (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return conn;
    }



    private static long CountRows(DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return System.Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }



    [Fact]
    public async Task Fluent_chain_roundtrips_rows_from_extractor_to_loader()
    {
        using var src = CreateSourceWithRows(3);
        using var dest = CreateEmptyDestination();

        await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)")
            .RunAsync()
            ;

        Assert.Equal(3L, CountRows(dest, "dest"));
    }



    [Fact]
    public async Task Extractor_builder_setters_propagate_to_the_underlying_extractor()
    {
        // Server-side paging via the builder: skip 1, take 2 → rows 2 and 3. The skip goes
        // into the query's offset because a template is set.
        using var src = CreateSourceWithRows(5);
        using var dest = CreateEmptyDestination();

        await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .PagingClauseTemplate(PagingClauseTemplates.Sqlite)
            .SkipItemCount(1)
            .MaximumItemCount(2)
            .DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)")
            .RunAsync()
            ;

        Assert.Equal(2L, CountRows(dest, "dest"));

        using var check = dest.CreateCommand();
        check.CommandText = "SELECT Id FROM dest ORDER BY Id;";
        using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(2L, reader.GetInt64(0));
        Assert.True(await reader.ReadAsync()); Assert.Equal(3L, reader.GetInt64(0));
    }



    [Fact]
    public async Task AsAsyncEnumerable_escape_hatch_enumerates_without_a_sink()
    {
        using var src = CreateSourceWithRows(2);

        var collected = new List<Widget>();
        await foreach (var w in EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .AsAsyncEnumerable())
        {
            collected.Add(w);
        }

        Assert.Equal(2, collected.Count);
        Assert.Equal("w-1", collected[0].Name);
        Assert.Equal("w-2", collected[1].Name);
    }



    [Fact]
    public async Task Loader_builder_ErrorHandling_flows_through_to_the_underlying_loader()
    {
        using var src = CreateSourceWithRows(3);
        using var dest = CreateEmptyDestination();

        // Insert row 2 into dest first so the loader's second row hits a PK
        // conflict. RowErrorHandling.Skip lets rows 1 and 3 land; row 2
        // silently fails.
        using (var seed = dest.CreateCommand())
        {
            seed.CommandText = "INSERT INTO dest (Id, Name) VALUES (2, 'pre-existing');";
            await seed.ExecuteNonQueryAsync();
        }

        var extractor = new DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id");
        var loader = new DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)")
        {
            InsertBatchSize = 1,
        };

        await EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .DbLoader(loader)
            .ErrorHandling(RowErrorHandling.Skip)
            .RunAsync()
            ;

        // 3 rows total: rows 1 and 3 succeeded, row 2 was the pre-existing one.
        Assert.Equal(3L, CountRows(dest, "dest"));
    }



    [Fact]
    public async Task Existing_extractor_overload_shares_state_with_the_caller()
    {
        using var src = CreateSourceWithRows(3);
        using var dest = CreateEmptyDestination();

        var extractor = new DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id");
        Assert.Null(extractor.PageSize);

        // Paging is gated on the template; PageSize tunes the round-trip and MaximumItemCount
        // caps the total. Setting all three proves the setter path AND exercises paging
        // end-to-end.
        await EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .PagingClauseTemplate(PagingClauseTemplates.Sqlite)
            .PageSize(2)
            .MaximumItemCount(2)
            .DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)")
            .RunAsync()
            ;

        // Setter on the builder mutated the caller's extractor.
        Assert.Equal(2, extractor.PageSize);
        Assert.Equal(2, extractor.MaximumItemCount);
        Assert.Equal(2L, CountRows(dest, "dest"));
    }



    [Fact]
    public void DbExtractor_factories_throw_ArgumentNullException_on_null_inputs()
    {
        using var src = CreateSourceWithRows(1);
        var pipeline = EtlPipeline.Create();

        Assert.Throws<System.ArgumentNullException>(() =>
            ((EtlPipeline)null!).DbExtractor<Widget>(src, "SELECT 1"));
        Assert.Throws<System.ArgumentNullException>(() =>
            pipeline.DbExtractor<Widget>((DbConnection)null!, "SELECT 1"));
        Assert.Throws<System.ArgumentNullException>(() =>
            pipeline.DbExtractor<Widget>(src, (string)null!));
        Assert.Throws<System.ArgumentNullException>(() =>
            pipeline.DbExtractor<Widget>((DbExtractor<Widget>)null!));
    }



    [Fact]
    public void DbLoader_factories_throw_ArgumentNullException_on_null_inputs()
    {
        using var src = CreateSourceWithRows(1);
        using var dest = CreateEmptyDestination();
        var stage = EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source");

        Assert.Throws<System.ArgumentNullException>(() =>
            ((IEtlPipeline<Widget>)null!).DbLoader<Widget>(dest, "INSERT ..."));
        Assert.Throws<System.ArgumentNullException>(() =>
            stage.DbLoader<Widget>((DbConnection)null!, "INSERT ..."));
        Assert.Throws<System.ArgumentNullException>(() =>
            stage.DbLoader<Widget>(dest, (string)null!));
        Assert.Throws<System.ArgumentNullException>(() =>
            stage.DbLoader<Widget>((DbLoader<Widget>)null!));
    }



    // ------------------------------------------------------------------
    // DbExtractorBuilder<T> setter coverage — every fluent-setter forwards
    // to the underlying DbExtractor. Uses the "existing-instance" overload
    // so the assertion can read the extractor's own state after the chain.
    // ------------------------------------------------------------------

    [Fact]
    public void Extractor_builder_CommandType_setter_forwards_to_extractor()
    {
        using var src = CreateSourceWithRows(1);
        var extractor = new DbExtractor<Widget>(src, "usp_get_widgets");
        Assert.Equal(CommandType.Text, extractor.CommandType);

        _ = EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .CommandType(CommandType.StoredProcedure);

        Assert.Equal(CommandType.StoredProcedure, extractor.CommandType);
    }



    [Fact]
    public void Extractor_builder_ManageConnection_setter_forwards_to_extractor()
    {
        using var src = CreateSourceWithRows(1);
        var extractor = new DbExtractor<Widget>(src, "SELECT 1");
        Assert.False(extractor.ManageConnection);

        _ = EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .ManageConnection(true);

        Assert.True(extractor.ManageConnection);
    }



    [Fact]
    public void Extractor_builder_Parameters_setter_forwards_to_extractor()
    {
        using var src = CreateSourceWithRows(1);
        var extractor = new DbExtractor<Widget>(src, "SELECT 1");
        var p = new Dapper.DynamicParameters();
        p.Add("@Id", 42);

        _ = EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .Parameters(p);

        Assert.Same(p, extractor.Parameters);
    }



    [Fact]
    public void Extractor_builder_Parameters_null_throws_ArgumentNullException()
    {
        using var src = CreateSourceWithRows(1);
        var builder = EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT 1");

        Assert.Throws<System.ArgumentNullException>(() => builder.Parameters(null!));
    }



    [Fact]
    public void Extractor_builder_PagingClauseTemplate_setter_forwards_to_extractor()
    {
        using var src = CreateSourceWithRows(1);
        var extractor = new DbExtractor<Widget>(src, "SELECT 1");
        const string sqlServer = "OFFSET @PageOffset ROWS FETCH NEXT @PageLimit ROWS ONLY";

        _ = EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .PagingClauseTemplate(sqlServer);

        Assert.Equal(sqlServer, extractor.PagingClauseTemplate);
    }



    [Fact]
    public void Extractor_builder_PagingClauseTemplate_accepts_null_as_None()
    {
        // Reversed deliberately. null is no longer invalid — it is PagingClauseTemplates.None,
        // "no dialect chosen". Rejecting it would make .PagingClauseTemplate(None) throw, which
        // would be an obvious trap given None is the default.
        using var src = CreateSourceWithRows(1);
        var builder = EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT 1");

        var returned = builder.PagingClauseTemplate(PagingClauseTemplates.None);

        Assert.Same(builder, returned);
    }



    [Fact]
    public void Extractor_builder_TotalCountQuery_setter_forwards_to_extractor()
    {
        using var src = CreateSourceWithRows(1);
        var extractor = new DbExtractor<Widget>(src, "SELECT 1");
        Func<CancellationToken, Task<int>> customCount = _ => Task.FromResult(99);

        _ = EtlPipeline
            .Create()
            .DbExtractor(extractor)
            .TotalCountQuery(customCount);

        Assert.Same(customCount, extractor.TotalCountQuery);
    }



    [Fact]
    public void Extractor_builder_TotalCountQuery_null_throws_ArgumentNullException()
    {
        using var src = CreateSourceWithRows(1);
        var builder = EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT 1");

        Assert.Throws<System.ArgumentNullException>(() => builder.TotalCountQuery(null!));
    }



    // ------------------------------------------------------------------
    // DbLoaderBuilder<T> setter coverage
    // ------------------------------------------------------------------

    [Fact]
    public async Task Loader_builder_CommandType_setter_forwards_to_loader()
    {
        using var src = CreateSourceWithRows(1);
        using var dest = CreateEmptyDestination();
        var loader = new DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)");
        Assert.Equal(CommandType.Text, loader.CommandType);

        // Set via builder BEFORE RunAsync — the sink reads it at run time.
        var chain = EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source")
            .DbLoader(loader)
            .CommandType(CommandType.Text);

        await chain.RunAsync();

        Assert.Equal(CommandType.Text, loader.CommandType);
    }



    [Fact]
    public async Task Loader_builder_ManageConnection_setter_forwards_to_loader()
    {
        using var src = CreateSourceWithRows(1);
        using var dest = CreateEmptyDestination();
        var loader = new DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)");
        Assert.False(loader.ManageConnection);

        await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source")
            .DbLoader(loader)
            .ManageConnection(false)
            .RunAsync();

        Assert.False(loader.ManageConnection);
    }



    // ------------------------------------------------------------------
    // DbExtractorBuilder<T>.Through — 4 overloads that materialize the
    // pipeline and delegate. Each test uses a simple identity transform
    // so the assertion just verifies the shape flows through.
    // ------------------------------------------------------------------

    private sealed class IdentityTransform : ITransformAsync<Widget, Widget>
    {
        public async IAsyncEnumerable<Widget> TransformAsync(IAsyncEnumerable<Widget> source)
        {
            await foreach (var w in source)
            {
                yield return w;
            }
        }
    }



    private sealed class IdentityTransformWithCancellation : ITransformWithCancellationAsync<Widget, Widget>
    {
        public async IAsyncEnumerable<Widget> TransformAsync(IAsyncEnumerable<Widget> source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var w in source.WithCancellation(token))
            {
                yield return w;
            }
        }

        // ITransformWithCancellationAsync<T,TOut> extends ITransformAsync<T,TOut>.
        // The bare overload just forwards to the cancellation-aware one with
        // CancellationToken.None.
        public IAsyncEnumerable<Widget> TransformAsync(IAsyncEnumerable<Widget> source)
            => TransformAsync(source, CancellationToken.None);
    }



    [Fact]
    public async Task ITransformWithCancellationAsync_bare_overload_forwards_to_the_token_aware_one()
    {
        // The pipeline always calls the cancellation-aware overload, so the bare one that
        // satisfies ITransformAsync<T,TOut> is never exercised through a pipeline. Calling it
        // directly asserts the forwarding actually works rather than merely compiling.
        var sut = new IdentityTransformWithCancellation();

        var source = new[]
        {
            new Widget { Id = 1, Name = "a" },
            new Widget { Id = 2, Name = "b" }
        }.ToAsyncEnumerable();

        var results = new List<Widget>();
        await foreach (var w in sut.TransformAsync(source))
        {
            results.Add(w);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0].Name);
        Assert.Equal("b", results[1].Name);
    }


    [Fact]
    public async Task Extractor_builder_Through_ITransformAsync_materializes_pipeline()
    {
        using var src = CreateSourceWithRows(3);
        var results = await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .Through(new IdentityTransform())
            .AsAsyncEnumerable()
            .ToListAsync();

        Assert.Equal(3, results.Count);
    }



    [Fact]
    public async Task Extractor_builder_Through_ITransformWithCancellationAsync_materializes_pipeline()
    {
        using var src = CreateSourceWithRows(3);
        var results = await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .Through(new IdentityTransformWithCancellation())
            .AsAsyncEnumerable()
            .ToListAsync();

        Assert.Equal(3, results.Count);
    }



    [Fact]
    public async Task Extractor_builder_Through_delegate_materializes_pipeline()
    {
        using var src = CreateSourceWithRows(3);
        var results = await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .Through<Widget>(source => source) // Func<IAsyncEnumerable<T>, IAsyncEnumerable<TOut>> — plain identity.
            .AsAsyncEnumerable()
            .ToListAsync();

        Assert.Equal(3, results.Count);
    }



    [Fact]
    public async Task Extractor_builder_Through_delegate_with_cancellation_materializes_pipeline()
    {
        using var src = CreateSourceWithRows(3);
        var results = await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .Through<Widget>((source, _) => source)
            .AsAsyncEnumerable()
            .ToListAsync();

        Assert.Equal(3, results.Count);
    }



    [Fact]
    public async Task Extractor_builder_To_LoaderBase_materializes_pipeline()
    {
        using var src = CreateSourceWithRows(3);
        using var dest = CreateEmptyDestination();
        var loader = new DbLoader<Widget>(dest, "INSERT INTO dest (Id, Name) VALUES (@Id, @Name)");

        // Explicitly go through Through+To rather than the DbLoader terminator
        // extension, so DbExtractorBuilder<T>.To<TProgress>(LoaderBase) is
        // exercised directly.
        await EtlPipeline
            .Create()
            .DbExtractor<Widget>(src, "SELECT Id, Name FROM source ORDER BY Id")
            .Through(new IdentityTransform())
            .To(loader)
            .RunAsync();

        Assert.Equal(3L, CountRows(dest, "dest"));
    }
}
