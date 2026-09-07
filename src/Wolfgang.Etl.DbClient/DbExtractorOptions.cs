using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Wolfgang.Etl.DbClient;

/// <summary>
/// Options for the <see cref="DbExtractor{TRecord}"/> constructors.
/// </summary>
/// <remarks>
/// Supplied ahead of the optional transaction and logger. When the whole options object is
/// <see langword="null"/>, or an individual property is left unset, the documented defaults below
/// apply — defaults live on the property initializers here rather than in constructor bodies, so no
/// constructor can accidentally diverge from them.
/// <para>
/// The record is not generic: none of these settings depends on the record type being extracted.
/// </para>
/// <para>
/// This record deliberately exposes no <c>Parameters</c> property. Dapper's
/// <c>DynamicParameters</c> is a third-party type, and keeping it off the public surface is what
/// allows the ORM to be swapped later without a breaking change for consumers. Supply parameters
/// through the constructor overload that takes an <c>IDictionary&lt;string, object&gt;</c>.
/// </para>
/// </remarks>
public sealed record DbExtractorOptions
{
    /// <summary>
    /// Gets the command timeout. Defaults to <see langword="null"/>, meaning the provider's default.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }



    /// <summary>
    /// Gets how the command text is interpreted. Defaults to <see cref="System.Data.CommandType.Text"/>.
    /// </summary>
    public CommandType CommandType { get; init; } = CommandType.Text;



    /// <summary>
    /// Gets a value indicating whether the extractor opens and closes the connection itself.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool ManageConnection { get; init; }



    /// <summary>
    /// Gets a value indicating whether the result-set schema is validated before the first row.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool ValidateSchemaOnStart { get; init; }




    /// <summary>
    /// Rows per round-trip. Setting this makes the extractor walk the result set one page at a
    /// time; leaving it unset issues a single query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging is transport tuning, not a row filter: <c>SkipItemCount</c> and
    /// <c>MaximumItemCount</c> decide which rows are yielded and yield the same rows either way.
    /// What changes is the number of round-trips and the work the server does per query.
    /// </para>
    /// <para>
    /// Requires <see cref="PagingClauseTemplate"/> — paging syntax is dialect-specific and no
    /// portable form exists, so a page size without a template throws.
    /// </para>
    /// <para>
    /// Paging costs more total server work, not less: <c>OFFSET n</c> is not a seek, so walking a
    /// table of <c>N</c> rows scans roughly <c>N² / (2 × pageSize)</c> rows. What it buys is
    /// bounded per-query work, shorter transactions and resumability. See
    /// <see cref="DbExtractor{TRecord}.PageSize"/> for the full cost note.
    /// </para>
    /// </remarks>
    public int? PageSize { get; init; }



    /// <summary>Rows to skip before the first yielded row, expressed as a server-side offset.</summary>
    /// <remarks>
    /// Superseded by <c>SkipItemCount</c>, which this is applied to — the two were always the same
    /// idea. When a paging template is set the skip is pushed into the query's offset, so the
    /// skipped rows are never fetched.
    /// </remarks>
    [Obsolete("Use SkipItemCount on the extractor instead. ServerOffset is applied to it and will be removed in a future release.")]
    public long? ServerOffset { get; init; }



    /// <summary>Rows per round-trip.</summary>
    /// <remarks>
    /// Superseded by <see cref="PageSize"/>, which takes precedence when both are set. The name
    /// changed because the meaning did: this is the size of each round-trip, not a cap on the
    /// total number of rows returned — use <c>MaximumItemCount</c> for the total.
    /// </remarks>
    [Obsolete("Use PageSize instead. ServerLimit is applied to it and will be removed in a future release. Note the meaning changed: this is rows per round-trip, not a cap on the total — use MaximumItemCount for that.")]
    public long? ServerLimit { get; init; }



    /// <summary>
    /// Gets the dialect-specific paging clause appended when server paging is active.
    /// Defaults to <see cref="PagingClauseTemplates.None"/> — no dialect chosen.
    /// </summary>
    /// <remarks>
    /// <b>This is dialect-specific, not standard SQL.</b> The default is the PostgreSQL / MySQL /
    /// SQLite form; <b>SQL Server rejects <c>LIMIT</c></b> and needs the SQL:2008
    /// <c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c> form, as do Oracle 12c+ and Db2. Leaving the
    /// default in place against those engines produces a runtime syntax error.
    /// <para>
    /// Use <see cref="PagingClauseTemplates"/> rather than writing the clause by hand:
    /// <c>PagingClauseTemplate = PagingClauseTemplates.SqlServer</c>. Any string is accepted, so a
    /// dialect with no preset can be supplied directly.
    /// </para>
    /// <para>
    /// The <c>OFFSET … FETCH</c> form additionally requires an <c>ORDER BY</c> at the end of the
    /// command text <i>you supply</i> — the paging clause is appended after it, so the finished
    /// statement ends with the paging clause, not the <c>ORDER BY</c>. SQL Server and Oracle both
    /// reject the form otherwise.
    /// </para>
    /// <para>
    /// A custom template must reference both <c>@PageOffset</c> and <c>@PageLimit</c> — those are
    /// the parameter names supplied when <see cref="ServerOffset"/> and <see cref="ServerLimit"/>
    /// are set.
    /// </para>
    /// </remarks>
    public string? PagingClauseTemplate { get; init; } = PagingClauseTemplates.None;



    /// <summary>
    /// Gets a callback returning the total row count, used to populate progress reports.
    /// Defaults to <see langword="null"/>, meaning the total is not reported.
    /// </summary>
    public Func<CancellationToken, Task<int>>? TotalCountQuery { get; init; }
}
