using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Wolfgang.Etl.DbClient.Tests.Unit;

/// <summary>
/// Exercises <see cref="EtlParameterSet"/> directly rather than through <see cref="DbExtractor{TRecord}"/>.
/// </summary>
/// <remarks>
/// Several paths here are unreachable via the extractor. Its own guard rejects a caller-supplied
/// paging name before <c>Add</c> is called, and the output-parameter write-back only runs when
/// Dapper invokes <c>OnCompleted</c>. Driving the type directly is the only way to cover them —
/// which is why the Windows coverage gate found them below threshold while the Linux run, whose
/// integration suite reaches some of these, did not.
/// </remarks>
public class EtlParameterSetDirectTests
{
    private static IDbDataParameter Param(IDbCommand command, int index)
    {
        // IDataParameterCollection's indexer is object?, so the cast needs the forgiveness.
        return (IDbDataParameter)command.Parameters[index]!;
    }



    private static IDbCommand NewCommand()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn.CreateCommand();
    }



    [Fact]
    public void AddParameters_when_command_is_null_throws()
    {
        var sut = new EtlParameterSet(new Dictionary<string, object>());

        Assert.Throws<ArgumentNullException>(() => sut.AddParameters(null!, null!));
    }



    [Fact]
    public void AddParameters_applies_DbType_and_Size_from_the_described_parameter()
    {
        var source = new Dictionary<string, object>
        {
            ["@name"] = new EtlParameter<string> { Value = "abc", DbType = DbType.AnsiString, Size = 64 }
        };
        var sut = new EtlParameterSet(source);

        using var command = NewCommand();
        sut.AddParameters(command, null!);

        var p = Param(command, 0);
        Assert.Equal(DbType.AnsiString, p.DbType);
        Assert.Equal(64, p.Size);
        Assert.Equal("abc", p.Value);
    }



    [Fact]
    public void A_null_valued_parameter_is_sent_as_DBNull()
    {
        var source = new Dictionary<string, object> { ["@name"] = new EtlParameter<string>() };
        var sut = new EtlParameterSet(source);

        using var command = NewCommand();
        sut.AddParameters(command, null!);

        Assert.Equal(DBNull.Value, Param(command, 0).Value);
    }



    [Fact]
    public void An_output_parameter_is_written_back_on_completion()
    {
        // The whole point of EtlParameter: a stored procedure's OUT value reaching the caller.
        // Driven through FakeDbCommand because Microsoft.Data.Sqlite rejects both Output and
        // InputOutput outright — verified, it throws "The ParameterDirection 'InputOutput' is not
        // supported." The write-back itself is provider-independent.
        var described = new EtlParameter<int> { Value = 0, Direction = ParameterDirection.InputOutput };
        var source = new Dictionary<string, object> { ["@count"] = described };
        var sut = new EtlParameterSet(source);

        using var command = new FakeDbCommand();
        sut.AddParameters(command, null!);

        // Simulate what the provider does once the command has executed.
        Param(command, 0).Value = 42;
        sut.OnCompleted();

        Assert.Equal(42, described.Value);
    }



    [Fact]
    public void An_input_parameter_is_not_registered_for_write_back()
    {
        var described = new EtlParameter<int> { Value = 7 };
        var source = new Dictionary<string, object> { ["@n"] = described };
        var sut = new EtlParameterSet(source);

        using var command = NewCommand();
        sut.AddParameters(command, null!);
        Param(command, 0).Value = 999;
        sut.OnCompleted();

        // Still 7 — Input parameters must not be written back over.
        Assert.Equal(7, described.Value);
    }



    [Fact]
    public void Add_rejects_a_name_the_caller_already_supplied()
    {
        // Unreachable through DbExtractor, whose own guard fires first. This is the second line
        // of defence, and the only way to reach it is directly.
        var sut = new EtlParameterSet(new Dictionary<string, object> { ["@PageOffset"] = 1 });

        var ex = Assert.Throws<InvalidOperationException>(() => sut.Add("@PageOffset", 0L));

        Assert.Contains("@PageOffset", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PagingClauseTemplate", ex.Message, StringComparison.Ordinal);
    }



    [Fact]
    public void Add_accepts_a_name_that_does_not_collide()
    {
        var sut = new EtlParameterSet(new Dictionary<string, object> { ["@other"] = 1 });

        sut.Add("@PageLimit", 10L);

        using var command = NewCommand();
        sut.AddParameters(command, null!);

        Assert.Equal(2, command.Parameters.Count);
    }
}
