using Dapper;
using Microsoft.Data.Sqlite;
using Wolfgang.Etl.ErrorPolicies;
using Wolfgang.Etl.TestKit.Xunit;
using Xunit;

// This file still configures through the deprecated property setters. Migrating it to the
// options constructors is follow-up work, tracked separately - the deprecation's purpose is
// to warn consumers, and the options constructors are covered by DbOptionsDefaultsTests.
// Several sites here assign after construction, so they cannot move to a constructor without
// restructuring the test.
#pragma warning disable CS0618

namespace Wolfgang.Etl.DbClient.Tests.Unit;

public class DbExtractorTests
    : ExtractorBaseContractTests<
        DbExtractor<ContractRecord>,
        ContractRecord,
        DbReport>
{
    // ------------------------------------------------------------------
    // Contract test factory methods
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<ContractRecord> ExpectedItems = new[]
    {
        new ContractRecord { Name = "Item1", Value = 10 },
        new ContractRecord { Name = "Item2", Value = 20 },
        new ContractRecord { Name = "Item3", Value = 30 },
        new ContractRecord { Name = "Item4", Value = 40 },
        new ContractRecord { Name = "Item5", Value = 50 },
    };



    /// <inheritdoc/>
    protected override DbExtractor<ContractRecord> CreateSut(int itemCount)
    {
        var conn = TestDb.CreateContractConnection(itemCount);
        return new DbExtractor<ContractRecord>
        (
            conn,
            "SELECT Name, Value FROM ContractItems ORDER BY Value"
        );
    }



    /// <inheritdoc/>
    protected override IReadOnlyList<ContractRecord> CreateExpectedItems() => ExpectedItems;


    // ------------------------------------------------------------------
    // Constructor validation
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_when_connection_is_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(null!, "SELECT 1")
        );
    }



    [Fact]
    public void Constructor_when_commandText_is_null_throws_ArgumentNullException()
    {
        using var conn = TestDb.CreateConnection();
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(conn, (string)null!)
        );
    }



    [Fact]
    public void Constructor_with_parameters_when_parameters_is_null_throws_ArgumentNullException()
    {
        using var conn = TestDb.CreateConnection();
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(conn, "SELECT 1", (Dictionary<string, object>)null!)
        );
    }



    // ------------------------------------------------------------------
    // Basic extraction
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_returns_all_rows()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id"
        );

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal("First1", results[0].FirstName);
        Assert.Equal("Last3", results[2].LastName);
        Assert.Equal(22, results[1].Age);
    }



    [Fact]
    public async Task ExtractAsync_with_empty_result_set_returns_no_items()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(0);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People"
        );

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Empty(results);
    }



    // ------------------------------------------------------------------
    // Parameterized queries
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_with_parameters_filters_correctly()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync();
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People WHERE age > @MinAge",
            new Dictionary<string, object>(StringComparer.Ordinal) { { "MinAge", 23 } }
        );

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Age > 23));
    }



    // ------------------------------------------------------------------
    // Auto-generated SELECT from attributes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_with_auto_generated_select_returns_all_rows()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>(conn);

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
    }



    [Fact]
    public void CommandText_with_auto_generated_select_contains_table_name()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn);

        Assert.Contains("People", extractor.CommandText, StringComparison.Ordinal);
    }



    // ------------------------------------------------------------------
    // SkipItemCount / MaximumItemCount
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_when_SkipItemCount_is_set_skips_rows()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync();
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id"
        );
        extractor.SkipItemCount = 2;

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal("First3", results[0].FirstName);
    }



    [Fact]
    public async Task ExtractAsync_when_MaximumItemCount_is_set_stops_early()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync();
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id"
        );
        extractor.MaximumItemCount = 2;

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(2, results.Count);
    }



    // ------------------------------------------------------------------
    // Transaction support
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_with_transaction_uses_provided_transaction()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
#if NETFRAMEWORK
        using var transaction = conn.BeginTransaction();
#else
        using var transaction = await conn.BeginTransactionAsync();
#endif
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id",
            transaction
        );

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
    }



    // ------------------------------------------------------------------
    // Progress report
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetProgressReport_returns_DbReport_with_counts()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People"
        );

        await extractor.ExtractAsync().ToListAsync();

        var report = extractor.GetProgressReport();

        Assert.Equal(3, report.CurrentItemCount);
        Assert.Contains("People", report.CommandText, StringComparison.Ordinal);
        Assert.True(report.ElapsedMilliseconds >= 0);
    }



    // ------------------------------------------------------------------
    // TotalCountQuery
    // ------------------------------------------------------------------

    [Fact]
    public async Task TotalCountQuery_when_null_TotalItemCount_is_null()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People"
        );

        await extractor.ExtractAsync().ToListAsync();

        Assert.Null(extractor.GetProgressReport().TotalItemCount);
    }



    [Fact]
    public async Task TotalCountQuery_using_default_TotalItemCount_equals_row_count()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People"
        );
        extractor.TotalCountQuery = extractor.DefaultTotalCountQuery;

        await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, extractor.GetProgressReport().TotalItemCount);
    }



    [Fact]
    public async Task TotalCountQuery_using_default_with_parameterized_query_returns_filtered_count()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync();
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People WHERE age > @MinAge",
            new Dictionary<string, object>(StringComparer.Ordinal) { { "MinAge", 23 } }
        );
        extractor.TotalCountQuery = extractor.DefaultTotalCountQuery;

        await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(2, extractor.GetProgressReport().TotalItemCount);
    }



    [Fact]
    public async Task TotalCountQuery_using_custom_func_returns_custom_count()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People"
        );
        // Don't capture `conn` in the lambda — ReSharper's AccessToDisposedClosure
        // flag is technically correct: the using-scope could outlive the closure.
        // The test only needs to prove a custom TotalCountQuery's return value is
        // surfaced through GetProgressReport().TotalItemCount, so return a constant.
        extractor.TotalCountQuery = _ => Task.FromResult(3);

        await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, extractor.GetProgressReport().TotalItemCount);
    }



    // ------------------------------------------------------------------
    // TotalCountQuery — SanitizeCommandTextForCount edge cases (review #15)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT id, first_name, last_name, age FROM People;")]
    [InlineData("SELECT id, first_name, last_name, age FROM People;;")]
    [InlineData("SELECT id, first_name, last_name, age FROM People; ; ;  ")]
    public async Task DefaultTotalCountQuery_strips_trailing_semicolons_and_whitespace(string commandText)
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>(conn, commandText);
        extractor.TotalCountQuery = extractor.DefaultTotalCountQuery;

        await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, extractor.GetProgressReport().TotalItemCount);
    }



    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public async Task DefaultTotalCountQuery_throws_when_command_text_is_blank(string blank)
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(1);
        var extractor = new DbExtractor<PersonRecord>(conn, blank);
        extractor.TotalCountQuery = extractor.DefaultTotalCountQuery;

        await Assert.ThrowsAsync<InvalidOperationException>
        (
            async () => await extractor.ExtractAsync().ToListAsync()
        );
    }



    // ------------------------------------------------------------------
    // CommandTimeout (#25)
    // ------------------------------------------------------------------

    [Fact]
    public void CommandTimeout_defaults_to_null()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT 1");

        Assert.Null(extractor.CommandTimeout);
    }



    [Fact]
    public void CommandTimeout_set_and_get_roundtrips()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT 1");

        extractor.CommandTimeout = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.FromMinutes(5), extractor.CommandTimeout);
    }



    [Fact]
    public void CommandTimeout_when_set_to_negative_throws_ArgumentOutOfRangeException()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT 1");

        Assert.Throws<ArgumentOutOfRangeException>
        (
            () => extractor.CommandTimeout = TimeSpan.FromSeconds(-1)
        );
    }



    // ------------------------------------------------------------------
    // CommandType (#26)
    // ------------------------------------------------------------------

    [Fact]
    public void CommandType_defaults_to_Text()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT 1");

        Assert.Equal(System.Data.CommandType.Text, extractor.CommandType);
    }



    [Fact]
    public void CommandType_set_and_get_roundtrips()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "usp_GetPeople");

        extractor.CommandType = System.Data.CommandType.StoredProcedure;

        Assert.Equal(System.Data.CommandType.StoredProcedure, extractor.CommandType);
    }



    [Fact]
    public async Task CommandType_Text_still_executes_normal_query()
    {
        // Explicitly setting to Text (the default) should be a no-op regression check.
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id"
        )
        {
            CommandType = System.Data.CommandType.Text
        };

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
    }



    // ------------------------------------------------------------------
    // DbProviderFactory ctor (#28)
    // ------------------------------------------------------------------

    [Fact]
    public void DbProviderFactory_ctor_when_factory_is_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(null!, "Data Source=:memory:", "SELECT 1")
        );
    }



    [Fact]
    public void DbProviderFactory_ctor_when_connectionString_is_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(Microsoft.Data.Sqlite.SqliteFactory.Instance, null!, "SELECT 1")
        );
    }



    [Fact]
    public void DbProviderFactory_ctor_when_commandText_is_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>
        (
            () => new DbExtractor<PersonRecord>(Microsoft.Data.Sqlite.SqliteFactory.Instance, "Data Source=:memory:", null!)
        );
    }



    [Fact]
    public async Task DbProviderFactory_ctor_extractor_opens_and_disposes_connection()
    {
        // Owned-connection path: connection is created from SqliteFactory, opened
        // on first use inside ExtractWorkerAsync, and disposed when the iterator
        // completes. Smoke-test that the round-trip succeeds for an empty query
        // (in-memory SQLite, fresh schema, returns 0 rows).
        var extractor = new DbExtractor<PersonRecord>
        (
            Microsoft.Data.Sqlite.SqliteFactory.Instance,
            "Data Source=:memory:",
            "SELECT 1 AS id, 'x' AS first_name, 'y' AS last_name, 30 AS age WHERE 0=1"
        );

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Empty(results);
    }



    [Fact]
    public async Task CommandTimeout_does_not_break_extraction_against_in_memory_db()
    {
        // SQLite in-memory ignores commandTimeout but the call path must still
        // succeed when a non-null timeout is supplied. Guards against accidentally
        // routing through a code path that doesn't pass the timeout cleanly.
        using var conn = await TestDb.CreateConnectionWithDataAsync(3);
        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT id, first_name, last_name, age FROM People ORDER BY id"
        )
        {
            CommandTimeout = TimeSpan.FromMinutes(2)
        };

        var results = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, results.Count);
    }



    // ------------------------------------------------------------------
    // CountAsync (#32)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CountAsync_uses_default_count_query_and_returns_row_count()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 7);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        var count = await extractor.CountAsync();

        Assert.Equal(7, count);
    }



    [Fact]
    public async Task CountAsync_with_custom_TotalCountQuery_returns_that_value()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 4);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People")
        {
            TotalCountQuery = _ => Task.FromResult(42)
        };

        var count = await extractor.CountAsync();

        Assert.Equal(42, count);
    }



    // ------------------------------------------------------------------
    // Server-side paging (#33)
    // ------------------------------------------------------------------

    [Fact]
    public void Paging_defaults_disable_server_side_paging()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        Assert.Null(extractor.PageSize);
        // No dialect is assumed; see PagingClauseTemplates.None.
        Assert.Null(extractor.PagingClauseTemplate);
    }



    [Fact]
    public async Task ExtractAsync_with_a_template_and_no_PageSize_pushes_the_maximum_into_one_query()
    {
        // Template without a page size is the single-query mode: the maximum goes into the
        // clause, so the database never produces the other 15 rows.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            MaximumItemCount = 5
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(5, records.Count);
        Assert.Equal("First1", records[0].FirstName);
        Assert.Equal("First5", records[4].FirstName);
    }



    [Fact]
    public async Task ExtractAsync_with_a_template_pushes_SkipItemCount_into_the_offset()
    {
        // The identity of the first row is the offset assertion: a skip applied client-side or
        // not at all would start at First1, and a doubled skip would start at First21.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            SkipItemCount = 10,
            MaximumItemCount = 5
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(5, records.Count);
        Assert.Equal("First11", records[0].FirstName);
        Assert.Equal("First15", records[4].FirstName);
    }



    [Fact]
    public async Task ExtractAsync_when_the_skip_is_server_side_still_reports_it_as_skipped()
    {
        // The rows were skipped, just not by us. Leaving the counter at zero would silently
        // change an observable the moment the skip moved into the query.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            SkipItemCount = 4,
            MaximumItemCount = 2
        };

        await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(4, extractor.CurrentSkippedItemCount);
    }



    [Fact]
    public async Task ExtractAsync_with_PageSize_walks_every_page_from_one_extractor()
    {
        // The ergonomics gap this closes: one extractor, no caller-written loop.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            PageSize = 3
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(20, records.Count);
        Assert.Equal("First1", records[0].FirstName);
        Assert.Equal("First20", records[19].FirstName);

        // Every row exactly once — a mis-advanced offset would repeat or drop some.
        Assert.Equal(20, records.Select(r => r.FirstName).Distinct(StringComparer.Ordinal).Count());
    }



    [Fact]
    public async Task ExtractAsync_with_PageSize_and_a_maximum_stops_at_the_maximum()
    {
        // Page size 4 with a maximum of 10 is 4 + 4 + 2: the last page asks for only what is
        // still needed rather than a full page that would be partly discarded.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            PageSize = 4,
            MaximumItemCount = 10
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(10, records.Count);
        Assert.Equal("First10", records[9].FirstName);
    }



    [Fact]
    public async Task ExtractAsync_with_PageSize_and_a_skip_walks_from_the_skip_to_the_end()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite,
            SkipItemCount = 15,
            PageSize = 2
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(5, records.Count);
        Assert.Equal("First16", records[0].FirstName);
        Assert.Equal("First20", records[4].FirstName);
    }



    [Fact]
    public async Task ExtractAsync_when_PageSize_is_set_without_a_template_throws_and_names_the_fix()
    {
        // The whole point of defaulting to None: without this the caller gets a raw provider
        // syntax error ("Incorrect syntax near 'LIMIT'" on SQL Server) instead of being told
        // what to do. Silently running unpaged would be worse still — a surprise full-table
        // scan that looks correct in development.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 5);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People ORDER BY id")
        {
            PageSize = 2
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in extractor.ExtractAsync()) { }
        });

        Assert.Contains("PagingClauseTemplate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PagingClauseTemplates", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PageSize", ex.Message, StringComparison.Ordinal);
    }



    [Fact]
    public async Task ExtractAsync_when_paging_is_inactive_does_not_require_a_template()
    {
        // None is only an error when paging is actually switched on. Leaving it at the default
        // while not paging must stay silent, or every non-paging caller would break.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 5);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People ORDER BY id");

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(5, records.Count);
        Assert.Null(extractor.PagingClauseTemplate);
    }



    [Fact]
    public async Task ExtractAsync_with_a_template_but_nothing_to_bound_appends_no_clause()
    {
        // Nothing to page: no skip, no maximum, no page size. Appending the clause would mean
        // sending "LIMIT 2147483647", which nothing here has verified every engine accepts.
        // If a clause were emitted with a bad limit this query would fail rather than return 5.
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 5);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            PagingClauseTemplate = PagingClauseTemplates.Sqlite
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(5, records.Count);
    }



    [Fact]
    public async Task ExtractAsync_without_a_template_still_applies_Skip_and_Max_client_side()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 20);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People ORDER BY id")
        {
            SkipItemCount = 5,
            MaximumItemCount = 3
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, records.Count);
        Assert.Equal("First6", records[0].FirstName);
        Assert.Equal(5, extractor.CurrentSkippedItemCount);
    }



    [Fact]
    public void ServerOffset_forwards_to_SkipItemCount()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        extractor.ServerOffset = 7;
        Assert.Equal(7, extractor.SkipItemCount);

        extractor.SkipItemCount = 9;
        Assert.Equal(9L, extractor.ServerOffset);
    }



    [Fact]
    public void ServerLimit_forwards_to_PageSize()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        extractor.ServerLimit = 25;
        Assert.Equal(25, extractor.PageSize);

        extractor.PageSize = 40;
        Assert.Equal(40L, extractor.ServerLimit);
    }



    [Fact]
    public void ServerOffset_that_does_not_fit_in_an_int_throws_rather_than_truncating()
    {
        // Row counts are Int32-wide on the base; a silent truncation here would turn an offset
        // of 4294967296 into 0 and quietly return the wrong rows.
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ServerOffset = (long)int.MaxValue + 1);
    }



    [Fact]
    public void PageSize_below_one_throws()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.PageSize = 0);
    }



    // ------------------------------------------------------------------
    // Parameters property override (#27)
    // ------------------------------------------------------------------

    [Fact]
    public void Parameters_defaults_to_null()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        Assert.Null(extractor.Parameters);
    }



    [Fact]
    public async Task ExtractAsync_when_Parameters_is_set_uses_those_for_the_query()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 10);

        // Build a DynamicParameters with a single input parameter; bind it
        // into a parameterized WHERE.
        var p = new DynamicParameters();
        p.Add("@Age", 25);

        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People WHERE age >= @Age"
        )
        {
            Parameters = p
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        // Seed rows have ages 21..30 (20 + i, i=1..10), so age >= 25 matches 6.
        Assert.Equal(6, records.Count);
        Assert.All(records, r => Assert.True(r.Age >= 25));
    }



    [Fact]
    public async Task ExtractAsync_Parameters_takes_precedence_over_constructor_dictionary()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 10);

        var dictParams = new Dictionary<string, object>(StringComparer.Ordinal) { ["@Age"] = 28 };
        var p = new DynamicParameters();
        p.Add("@Age", 22); // overrides the dictionary value

        var extractor = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People WHERE age >= @Age",
            dictParams
        )
        {
            Parameters = p
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        // Ages 22..30 = 9 rows (overrode dict's 28→22). If the dictionary
        // had won we'd see 3 rows (28,29,30).
        Assert.Equal(9, records.Count);
    }



    // ------------------------------------------------------------------
    // ManageConnection (#31)
    // ------------------------------------------------------------------

    [Fact]
    public void ManageConnection_defaults_to_false()
    {
        using var conn = TestDb.CreateConnection();
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        Assert.False(extractor.ManageConnection);
    }



    [Fact]
    public async Task ExtractAsync_when_ManageConnection_is_true_opens_a_closed_connection_and_closes_it_after()
    {
        // Shared-cache in-memory SQLite so a Close()→Open() cycle preserves
        // schema + data (plain :memory: drops it).
        var connString = $"Data Source=mc_extractor_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keeper = new Microsoft.Data.Sqlite.SqliteConnection(connString);
        await keeper.OpenAsync();
        await TestDb.CreateEmptyTableAsync(keeper);
        using (var seed = keeper.CreateCommand())
        {
            seed.CommandText = "INSERT INTO People (first_name, last_name, age) VALUES ('Ada','Lovelace',36),('Alan','Turing',41),('Grace','Hopper',85)";
            await seed.ExecuteNonQueryAsync();
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People")
        {
            ManageConnection = true
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        Assert.Equal(3, records.Count);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }



    [Fact]
    public async Task ExtractAsync_when_ManageConnection_is_true_leaves_already_open_connections_open()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 3);
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, last_name AS LastName, age AS Age FROM People")
        {
            ManageConnection = true
        };

        var records = await extractor.ExtractAsync().ToListAsync();

        // We only close what we opened. The caller had it open; it stays open.
        Assert.Equal(3, records.Count);
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }



    [Fact]
    public async Task CountAsync_does_not_affect_progress_state()
    {
        using var conn = await TestDb.CreateConnectionWithDataAsync(rowCount: 5);

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName FROM People");

        var count = await extractor.CountAsync();

        // CountAsync runs the count query but does not touch the progress
        // counters — those advance only when ExtractAsync streams rows.
        Assert.Equal(5, count);
        Assert.Equal(0, extractor.CurrentItemCount);
    }



    // ------------------------------------------------------------------
    // ErrorPolicy
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExtractAsync_when_ErrorPolicy_is_Skip_swallows_bad_row_and_continues()
    {
        using var conn = TestDb.CreateConnection();
        using (var seed = conn.CreateCommand())
        {
            // A non-numeric age fails Dapper's conversion to PersonRecord.Age
            // (int) while materializing that row. A NULL age does NOT
            // reproduce this — Dapper coerces DBNull to default(int) silently
            // rather than throwing.
            seed.CommandText = @"
                CREATE TABLE People (first_name TEXT, age INTEGER);
                INSERT INTO People (first_name, age) VALUES ('Ada', 30);
                INSERT INTO People (first_name, age) VALUES ('Bad', 'oops');
                INSERT INTO People (first_name, age) VALUES ('Zoe', 40);";
            await seed.ExecuteNonQueryAsync();
        }

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, age AS Age FROM People")
        {
            ErrorPolicy = ItemErrorPolicy.Skip
        };

        var results = await extractor.ExtractAsync().ToListAsync();

        // Both good rows survive, including the one AFTER the bad row — proves
        // Skip actually continues extraction instead of silently truncating at
        // the first error. (Regression guard: an earlier implementation drove
        // the read loop through Dapper's QueryUnbufferedAsync<T> IAsyncEnumerable,
        // whose single async-iterator state machine finishes on any exception —
        // 'Zoe' never appeared because the enumerator was already done.)
        Assert.Equal(2, results.Count);
        Assert.Equal("Ada", results[0].FirstName);
        Assert.Equal("Zoe", results[1].FirstName);
        Assert.Equal(1, extractor.CurrentErrorItemCount);
    }



    [Fact]
    public async Task ExtractAsync_when_ErrorPolicy_is_default_aborts_on_bad_row()
    {
        using var conn = TestDb.CreateConnection();
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                CREATE TABLE People (first_name TEXT, age INTEGER);
                INSERT INTO People (first_name, age) VALUES ('Ada', 30);
                INSERT INTO People (first_name, age) VALUES ('Bad', 'oops');";
            await seed.ExecuteNonQueryAsync();
        }

        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT first_name AS FirstName, age AS Age FROM People");

        // No ErrorPolicy set — default is Abort, so pre-existing behavior
        // (the row's exception propagates) is preserved bit-for-bit.
        await Assert.ThrowsAnyAsync<Exception>
        (
            async () => await extractor.ExtractAsync().ToListAsync()
        );
    }



    [Fact]
    public async Task ExtractAsync_when_ErrorPolicy_is_Skip_still_propagates_non_row_failures()
    {
        using var conn = TestDb.CreateConnection();
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE People (first_name TEXT);";
            await seed.ExecuteNonQueryAsync();
        }

        // A bad column name fails at command execution, not row materialization —
        // SQLite throws its own provider exception (SqliteException), never a
        // Dapper DataException. ErrorPolicy only ever catches DataException, so
        // this must propagate even under Skip — routing a connection/execution-level
        // fault through ItemErrorAction would otherwise spin the read loop instead
        // of terminating (MoveNextAsync would keep failing the same way forever).
        var extractor = new DbExtractor<PersonRecord>(conn, "SELECT nonexistent_column FROM People")
        {
            ErrorPolicy = ItemErrorPolicy.Skip
        };

        await Assert.ThrowsAsync<SqliteException>
        (
            async () => await extractor.ExtractAsync().ToListAsync()
        );
    }
}
