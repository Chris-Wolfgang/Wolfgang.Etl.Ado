using System;
using System.Data;
using Microsoft.Data.Sqlite;
using Wolfgang.Etl.DbClient;
using Xunit;

// Constructs via the deprecated constructors. Migrating to the options overloads is
// follow-up work; the deprecation exists to warn consumers, and the options constructors
// are covered by DbOptionsDefaultsTests.
#pragma warning disable CS0618

namespace Wolfgang.Etl.DbClient.Tests.Unit;

/// <summary>
/// Guards the options records' defaults against the validation the underlying properties apply.
/// </summary>
/// <remarks>
/// <see cref="DbLoaderOptions.InsertBatchSize"/> and <see cref="DbLoaderOptions.BatchSize"/> both
/// default to <c>1</c> rather than the type default of <c>0</c>, because the properties they feed
/// reject anything below <c>1</c>. A record defaulting them to <c>0</c> would make every
/// options-constructed loader throw on construction — these tests fail if that regresses.
/// </remarks>
public class DbOptionsDefaultsTests
{
    private static SqliteConnection Connection() => new("Data Source=:memory:");


    [Fact]
    public void Loader_constructed_with_an_empty_options_record_does_not_throw()
    {
        using var conn = Connection();

        var sut = new DbLoader<PersonRecord>(conn, "INSERT INTO People (first_name) VALUES (@FirstName)", new DbLoaderOptions());

        Assert.Equal(1, sut.InsertBatchSize);
        Assert.Equal(1, sut.BatchSize);
        Assert.Equal(0, sut.MaxErrorCount);
        Assert.Equal(0, sut.BatchCommitSize);
        Assert.Equal(CommandType.Text, sut.CommandType);
        Assert.Equal(RowErrorHandling.Abort, sut.ErrorHandling);
    }


    [Fact]
    public void Loader_constructed_with_null_options_matches_the_no_options_constructor()
    {
        using var conn = Connection();

        var withNull = new DbLoader<PersonRecord>(conn, "INSERT INTO People (first_name) VALUES (@FirstName)", (DbLoaderOptions?)null);
        var without = new DbLoader<PersonRecord>(conn, "INSERT INTO People (first_name) VALUES (@FirstName)");

        Assert.Equal(without.InsertBatchSize, withNull.InsertBatchSize);
        Assert.Equal(without.BatchSize, withNull.BatchSize);
        Assert.Equal(without.CommandType, withNull.CommandType);
        Assert.Equal(without.ErrorHandling, withNull.ErrorHandling);
    }


    [Fact]
    public void Extractor_constructed_with_an_empty_options_record_does_not_throw()
    {
        using var conn = Connection();

        var sut = new DbExtractor<PersonRecord>(conn, "SELECT 1", new DbExtractorOptions());

        Assert.Null(sut.CommandTimeout);
        Assert.Equal(CommandType.Text, sut.CommandType);
        // No dialect is assumed; see PagingClauseTemplates.None.
        Assert.Null(sut.PagingClauseTemplate);
        Assert.False(sut.ManageConnection);
    }


    [Fact]
    public void Extractor_options_are_applied()
    {
        using var conn = Connection();

        var sut = new DbExtractor<PersonRecord>
        (
            conn,
            "SELECT 1",
            new DbExtractorOptions
            {
                CommandTimeout = TimeSpan.FromSeconds(30),
                ManageConnection = true,
                PageSize = 100
            }
        );

        Assert.Equal(TimeSpan.FromSeconds(30), sut.CommandTimeout);
        Assert.True(sut.ManageConnection);
        Assert.Equal(100, sut.PageSize);
    }
}
