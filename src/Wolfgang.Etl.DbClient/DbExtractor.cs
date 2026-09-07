using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolfgang.Etl.Abstractions;

namespace Wolfgang.Etl.DbClient;

/// <summary>
/// Extracts records from a database query as an asynchronous stream.
/// Uses Dapper for column-to-property mapping, supporting <c>[Column]</c> attribute
/// and convention-based name matching.
/// </summary>
/// <typeparam name="TRecord">
/// The POCO type representing a single row. Properties are mapped from result set
/// columns by name or <c>[Column("name")]</c> attribute.
/// </typeparam>
/// <remarks>
/// <para>
/// The caller owns the <see cref="DbConnection"/> lifetime — the extractor does not
/// open, close, or dispose it. The connection must be open before calling
/// <c>ExtractAsync</c>.
/// </para>
/// <para>
/// An optional <see cref="DbTransaction"/> can be provided for isolation level control.
/// The extractor never commits or rolls back the transaction.
/// </para>
/// <para>
/// <b>Thread safety.</b> A <see cref="DbExtractor{TRecord}"/> instance is not safe for
/// concurrent <c>ExtractAsync</c> calls. Internal state (stopwatch, total-count snapshot,
/// progress-counter increments) assumes a single extraction in flight. Build a separate
/// instance per concurrent extraction.
/// </para>
/// <para>
/// Command timeout uses the Dapper/ADO.NET default (typically 30 seconds).
/// A dedicated <c>CommandTimeout</c> property is planned (see GitHub issue #25).
/// </para>
/// </remarks>
public class DbExtractor<TRecord> : ExtractorBase<TRecord, DbReport>
    where TRecord : notnull
{
    // ------------------------------------------------------------------
    // Fields
    // ------------------------------------------------------------------

    // _connection is not `readonly` because the DbProviderFactory ctor overload
    // creates the connection itself; the caller-supplied-DbConnection ctors set
    // it once and never re-assign. _ownsConnection tracks whether ExtractWorkerAsync
    // is responsible for OpenAsync + Dispose.
    private readonly DbConnection _connection;
    private readonly bool _ownsConnection;
    private readonly string _commandText;

    // Defensive snapshot of the caller's parameter dictionary. Copying at
    // construction time guarantees the data query, the default total-count
    // query, and debug logging all see the same values, even if the caller
    // mutates the dictionary they passed in after construction.
    private readonly IDictionary<string, object>? _parameters;

    // Cached Dapper parameter wrapper. Built once at construction from the
    // defensive snapshot and reused across the data query and the default
    // total-count query. Debug logging still reads from _parameters (the
    // dictionary form) — both come from the same snapshot, so they cannot
    // diverge. Dapper treats input-parameter DynamicParameters as read-only
    // during execution, so sharing is safe across this type's documented
    // single-use lifetime.
    // Either the library's own dictionary handling (plain values only) or EtlParameterSet when
    // the caller supplied EtlParameter / DbParameter values. Typed as the interface so both fit.
    private readonly SqlMapper.IDynamicParameters? _dynamicParameters;
    private readonly DbTransaction? _transaction;
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch = new();
    private int? _totalItemCount;


    private int? _pageSize;



    // ------------------------------------------------------------------
    // Static initializer
    // ------------------------------------------------------------------

    static DbExtractor()
    {
        ColumnAttributeTypeMapper.Register<TRecord>();
    }



    // ------------------------------------------------------------------
    // Constructors
    // ------------------------------------------------------------------

    /// <summary>
    /// Initializes a new <see cref="DbExtractor{TRecord}"/> with a SQL command.
    /// </summary>
    /// <param name="connection">An open <see cref="DbConnection"/>. The caller owns its lifetime.</param>
    /// <param name="commandText">The SQL query to execute.</param>
    /// <param name="transaction">An optional <see cref="DbTransaction"/> for isolation control.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> or <paramref name="commandText"/> is null.
    /// </exception>
    [Obsolete("Use the constructor that takes DbExtractorOptions. This constructor will be removed in a future release.")]
    public DbExtractor
    (
        DbConnection connection,
        string commandText,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
        : this
        (
            connection ?? throw new ArgumentNullException(nameof(connection)),
            commandText ?? throw new ArgumentNullException(nameof(commandText)),
            transaction,
            ownsConnection: false,
            logger
        )
    {
    }



    /// <summary>
    /// Initializes a new <see cref="DbExtractor{TRecord}"/> with a parameterized SQL command.
    /// </summary>
    /// <param name="connection">An open <see cref="DbConnection"/>. The caller owns its lifetime.</param>
    /// <param name="commandText">The SQL query to execute.</param>
    /// <param name="parameters">
    /// Named parameters for the query. A defensive copy is taken at construction time,
    /// so mutations to the supplied dictionary after construction do not affect the
    /// executed query or the values reported in debug logs.
    /// </param>
    /// <param name="transaction">An optional <see cref="DbTransaction"/> for isolation control.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/>, <paramref name="commandText"/>, or <paramref name="parameters"/> is null.
    /// </exception>
    [Obsolete("Use the constructor that takes DbExtractorOptions. This constructor will be removed in a future release.")]
    public DbExtractor
    (
        DbConnection connection,
        string commandText,
        IDictionary<string, object> parameters,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
        : this
        (
            connection ?? throw new ArgumentNullException(nameof(connection)),
            commandText ?? throw new ArgumentNullException(nameof(commandText)),
            transaction,
            ownsConnection: false,
            logger
        )
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        // Defensive copy — see the field-level comment on _parameters.
        _parameters = new Dictionary<string, object>(parameters, StringComparer.Ordinal);
        _dynamicParameters = EtlParameterSet.IsNeededFor(_parameters)
            ? new EtlParameterSet(_parameters)
            : new DynamicParameters(_parameters);
    }



    /// <summary>
    /// Initializes a new <see cref="DbExtractor{TRecord}"/> that auto-generates
    /// a SELECT statement from <c>[Table]</c> and <c>[Column]</c> attributes on
    /// <typeparamref name="TRecord"/>.
    /// </summary>
    /// <param name="connection">An open <see cref="DbConnection"/>. The caller owns its lifetime.</param>
    /// <param name="transaction">An optional <see cref="DbTransaction"/> for isolation control.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRecord"/> does not have a <c>[Table]</c> attribute.
    /// </exception>
    [Obsolete("Use the constructor that takes DbExtractorOptions. This constructor will be removed in a future release.")]
    public DbExtractor
    (
        DbConnection connection,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
        : this
        (
            connection ?? throw new ArgumentNullException(nameof(connection)),
            DbCommandBuilder.BuildSelect<TRecord>(),
            transaction,
            ownsConnection: false,
            logger
        )
    {
    }



    /// <summary>
    /// Initializes a new <see cref="DbExtractor{TRecord}"/> that owns the
    /// connection's lifetime. The connection is created from the supplied
    /// <see cref="DbProviderFactory"/>, opened lazily before extraction begins,
    /// and disposed when extraction completes (or throws).
    /// </summary>
    /// <param name="factory">
    /// The provider-specific factory (e.g. <c>Microsoft.Data.SqlClient
    /// .SqlClientFactory.Instance</c>, <c>Npgsql.NpgsqlFactory.Instance</c>).
    /// </param>
    /// <param name="connectionString">The provider-specific connection string.</param>
    /// <param name="commandText">The SQL query to execute.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="factory"/>, <paramref name="connectionString"/>, or
    /// <paramref name="commandText"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="factory"/> returned a null connection from
    /// <see cref="DbProviderFactory.CreateConnection"/>.
    /// </exception>
    [Obsolete("Use the constructor that takes DbExtractorOptions. This constructor will be removed in a future release.")]
    public DbExtractor
    (
        DbProviderFactory factory,
        string connectionString,
        string commandText,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
        : this
        (
            CreateOwnedConnection(factory, connectionString, commandText),
            commandText,
            transaction: null,
            ownsConnection: true,
            logger
        )
    {
    }




    /// <summary>
    /// Initializes a new instance of the <see cref="DbExtractor{TRecord}"/> class configured from an
    /// options record.
    /// </summary>
    /// <param name="connection">The connection to read from.</param>
    /// <param name="commandText">The command to execute.</param>
    /// <param name="transaction">An optional ambient transaction.</param>
    /// <param name="options">The configuration to apply. When <c>null</c>, the documented defaults apply.</param>
    /// <param name="logger">
    /// An optional logger instance for diagnostic output. When <c>null</c> — or omitted —
    /// <see cref="NullLogger.Instance"/> is used and logging is disabled.
    /// </param>
    public DbExtractor
    (
        DbConnection connection,
        string commandText,
        DbExtractorOptions? options,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
#pragma warning disable CS0618 // Chains into the deprecated ctor deliberately: it is the single initialization path.
        : this(connection, commandText, transaction, logger)
    {
        ApplyOptions(options);
    }
#pragma warning restore CS0618



    /// <summary>
    /// Initializes a new instance of the <see cref="DbExtractor{TRecord}"/> class configured from an
    /// options record.
    /// </summary>
    /// <param name="connection">The connection to read from.</param>
    /// <param name="commandText">The command to execute.</param>
    /// <param name="parameters">The command parameters.</param>
    /// <param name="transaction">An optional ambient transaction.</param>
    /// <param name="options">The configuration to apply. When <c>null</c>, the documented defaults apply.</param>
    /// <param name="logger">
    /// An optional logger instance for diagnostic output. When <c>null</c> — or omitted —
    /// <see cref="NullLogger.Instance"/> is used and logging is disabled.
    /// </param>
    public DbExtractor
    (
        DbConnection connection,
        string commandText,
        IDictionary<string, object> parameters,
        DbExtractorOptions? options,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
#pragma warning disable CS0618 // Chains into the deprecated ctor deliberately: it is the single initialization path.
        : this(connection, commandText, parameters, transaction, logger)
    {
        ApplyOptions(options);
    }
#pragma warning restore CS0618



    /// <summary>
    /// Initializes a new instance of the <see cref="DbExtractor{TRecord}"/> class configured from an
    /// options record.
    /// </summary>
    /// <param name="connection">The connection to read from.</param>
    /// <param name="transaction">An optional ambient transaction.</param>
    /// <param name="options">The configuration to apply. When <c>null</c>, the documented defaults apply.</param>
    /// <param name="logger">
    /// An optional logger instance for diagnostic output. When <c>null</c> — or omitted —
    /// <see cref="NullLogger.Instance"/> is used and logging is disabled.
    /// </param>
    public DbExtractor
    (
        DbConnection connection,
        DbExtractorOptions? options,
        DbTransaction? transaction = null,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
#pragma warning disable CS0618 // Chains into the deprecated ctor deliberately: it is the single initialization path.
        : this(connection, transaction, logger)
    {
        ApplyOptions(options);
    }
#pragma warning restore CS0618



    /// <summary>
    /// Initializes a new instance of the <see cref="DbExtractor{TRecord}"/> class configured from an
    /// options record.
    /// </summary>
    /// <param name="factory">The provider factory used to create the connection.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="commandText">The command to execute.</param>
    /// <param name="options">The configuration to apply. When <c>null</c>, the documented defaults apply.</param>
    /// <param name="logger">
    /// An optional logger instance for diagnostic output. When <c>null</c> — or omitted —
    /// <see cref="NullLogger.Instance"/> is used and logging is disabled.
    /// </param>
    public DbExtractor
    (
        DbProviderFactory factory,
        string connectionString,
        string commandText,
        DbExtractorOptions? options,
        ILogger<DbExtractor<TRecord>>? logger = null
    )
#pragma warning disable CS0618 // Chains into the deprecated ctor deliberately: it is the single initialization path.
        : this(factory, connectionString, commandText, logger)
    {
        ApplyOptions(options);
    }
#pragma warning restore CS0618

    /// <summary>
    /// Validates the provider-factory arguments and produces the connection this extractor owns.
    /// </summary>
    /// <remarks>
    /// The validation lives here rather than in the constructor body because a constructor's
    /// <c>this(...)</c> arguments are evaluated before its body runs. Keeping the checks in this
    /// order preserves both the original <c>ParamName</c> for each argument and the guarantee that
    /// no connection is created when any argument is null.
    /// </remarks>
    /// <param name="factory">The provider factory used to create the connection.</param>
    /// <param name="connectionString">The connection string applied to the new connection.</param>
    /// <param name="commandText">The command text, validated here to preserve argument order.</param>
    /// <returns>A new <see cref="DbConnection"/> owned by this extractor.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="factory"/>, <paramref name="connectionString"/> or
    /// <paramref name="commandText"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="factory"/> produced a <c>null</c> connection.
    /// </exception>
    private static DbConnection CreateOwnedConnection
    (
        DbProviderFactory factory,
        string connectionString,
        string commandText
    )
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
        if (commandText == null) throw new ArgumentNullException(nameof(commandText));

        var conn = factory.CreateConnection()
            ?? throw new InvalidOperationException
            (
                $"{factory.GetType().FullName}.CreateConnection() returned null. " +
                "The provider factory does not produce DbConnection instances."
            );
        conn.ConnectionString = connectionString;
        return conn;
    }



    /// <summary>
    /// The single initialization path. Every other constructor chains into this one, so the shared
    /// fields are assigned in exactly one place and cannot drift between input shapes.
    /// </summary>
    private DbExtractor
    (
        DbConnection connection,
        string commandText,
        DbTransaction? transaction,
        bool ownsConnection,
        ILogger? logger
    )
    {
        _connection = connection;
        _commandText = commandText;
        _transaction = transaction;
        _ownsConnection = ownsConnection;
        _logger = logger ?? NullLogger.Instance;
    }



    // ------------------------------------------------------------------
    // Properties
    // ------------------------------------------------------------------

    /// <summary>
    /// The SQL command text being executed.
    /// </summary>
    public string CommandText => _commandText;



    /// <summary>
    /// How long each command (the extraction query and the
    /// <see cref="TotalCountQuery"/>) may execute before timing out. <c>null</c>
    /// (the default) means "use the ADO.NET provider's default", which is
    /// typically 30 seconds.
    /// </summary>
    /// <remarks>
    /// Maps onto Dapper's <c>commandTimeout</c> parameter (an <c>int?</c> count
    /// of seconds). Fractional seconds in the supplied <see cref="TimeSpan"/>
    /// are truncated. A negative <see cref="TimeSpan"/> is rejected.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned value is negative.
    /// </exception>
    public TimeSpan? CommandTimeout
    {
        get => _commandTimeout;
        [Obsolete("Configure CommandTimeout through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")]
        set
        {
            if (value.HasValue && value.Value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException
                (
                    nameof(value),
                    value,
                    "CommandTimeout cannot be negative. Use null to fall back to the ADO.NET default."
                );
            }
            _commandTimeout = value;
        }
    }

    private TimeSpan? _commandTimeout;

    // Dapper's commandTimeout parameter is `int?` seconds. Centralized here so
    // every call site uses the same conversion (and so future "0 = infinite"
    // semantics, if needed, only have to flip in one place).
    private int? CommandTimeoutSeconds => _commandTimeout.HasValue
        ? (int)_commandTimeout.Value.TotalSeconds
        : null;



    /// <summary>
    /// How <see cref="CommandText"/> is interpreted by the ADO.NET provider.
    /// Defaults to <see cref="CommandType.Text"/> (a SQL statement). Set to
    /// <see cref="CommandType.StoredProcedure"/> to invoke a stored procedure
    /// by name; <see cref="CommandText"/> then holds the procedure name.
    /// </summary>
    /// <remarks>
    /// <see cref="CommandType.TableDirect"/> is supported by very few providers
    /// (notably OleDb). It's accepted on this property — Dapper passes it
    /// through — but most consumers should stick to <c>Text</c> or
    /// <c>StoredProcedure</c>.
    /// </remarks>
    public CommandType CommandType { get; [Obsolete("Configure CommandType through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; } = CommandType.Text;



    /// <summary>
    /// When <see langword="true"/>, the extractor opens the connection before
    /// the first command runs and closes it after the enumeration ends. The
    /// connection is NOT disposed — it's returned to the pool for reuse,
    /// which plays better with connection-pool lifetime in web apps and
    /// hosted services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default <see langword="false"/> preserves the v0.4.0 behavior: the
    /// caller is responsible for opening the connection before calling
    /// <c>ExtractAsync</c>.
    /// </para>
    /// <para>
    /// Ignored on the owned-connection ctor path (the
    /// <c>(DbProviderFactory, connectionString, …)</c> overload). That path
    /// always manages and disposes the connection because it created it.
    /// </para>
    /// <para>
    /// If the connection is already open when <c>ExtractAsync</c> starts,
    /// it's left open — the extractor only closes connections it itself
    /// opened.
    /// </para>
    /// </remarks>
    public bool ManageConnection { get; [Obsolete("Configure ManageConnection through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; }



    /// <summary>
    /// When <see langword="true"/>, the extractor calls
    /// <see cref="DbSchemaValidator.ValidateAsync{TRecord}(System.Data.Common.DbConnection, System.Threading.CancellationToken)"/>
    /// before the first row is fetched. If the mapped
    /// <c>[Table]</c>/<c>[Column]</c> names don't match the database,
    /// the extractor throws <see cref="InvalidOperationException"/>
    /// before touching production data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default: <see langword="false"/>. Opting in adds a single
    /// zero-row round-trip at the top of each <c>ExtractAsync</c>
    /// call — negligible compared to a real extract, but not free.
    /// Use it in a first-run smoke path or a health check, not
    /// inside every loop iteration on a per-message pipeline.
    /// </para>
    /// <para>
    /// Refs <see href="https://github.com/Chris-Wolfgang/Etl-DbClient/issues/20">#20</see>.
    /// </para>
    /// </remarks>
    public bool ValidateSchemaOnStart { get; [Obsolete("Configure ValidateSchemaOnStart through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; }



    /// <summary>
    /// Optional override for the parameter set passed to Dapper. Setting this
    /// property takes precedence over any <c>IDictionary&lt;string,object&gt;</c>
    /// supplied via the constructor — useful when the command is a stored
    /// procedure with <c>OUT</c> / <c>INOUT</c> parameters that need to be
    /// declared with <see cref="ParameterDirection"/> values Dapper can't
    /// infer from a plain dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caller-owned: the extractor never clones it. After
    /// <c>ExtractAsync</c> completes, read output values via
    /// <c>Parameters.Get&lt;T&gt;("@name")</c>.
    /// </para>
    /// <para>
    /// Example:
    /// <code>
    /// var p = new DynamicParameters();
    /// p.Add("@CustomerId", 42);
    /// p.Add("@TotalCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
    /// var extractor = new DbExtractor&lt;Order&gt;(conn, "usp_GetOrdersForCustomer")
    /// {
    ///     CommandType = CommandType.StoredProcedure,
    ///     Parameters = p
    /// };
    /// var orders = await extractor.ExtractAsync().ToListAsync();
    /// var total = p.Get&lt;int&gt;("@TotalCount");
    /// </code>
    /// </para>
    /// </remarks>
    public DynamicParameters? Parameters { get; [Obsolete("Configure Parameters through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; }



    /// <summary>
    /// Rows per round-trip. Setting this makes the extractor walk the result set one page at a
    /// time; leaving it unset issues a single query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging is <b>transport tuning</b>, not a row filter. <c>SkipItemCount</c> and
    /// <c>MaximumItemCount</c> decide which rows are yielded, and they yield the same rows
    /// whether or not this is set. What changes is the number of round-trips and how much work
    /// the server does per query.
    /// </para>
    /// <para>
    /// Requires <see cref="PagingClauseTemplate"/>, because paging syntax is dialect-specific and
    /// no portable form exists. A page size without a template throws
    /// <see cref="InvalidOperationException"/> rather than silently running unpaged.
    /// </para>
    /// <para>
    /// <b>Paging costs more total server work, not less.</b> <c>OFFSET n</c> is not a seek — most
    /// engines produce the rows in order and discard the first <c>n</c> — so a page at offset
    /// <c>n</c> costs O(n + pageSize), and walking a table of <c>N</c> rows scans roughly
    /// <c>N² / (2 × pageSize)</c> rows in total. A larger page reduces that linearly. What paging
    /// buys is bounded per-query work, shorter transactions and resumability, not less work.
    /// </para>
    /// <para>
    /// <b>Page contents drift under concurrent writes.</b> Rows inserted or deleted between pages
    /// shift the window, so rows can be missed or returned twice even with an <c>ORDER BY</c>.
    /// SQL Server requires an <c>ORDER BY</c> for paging to be deterministic at all; the other
    /// engines do not require one, but you should still supply one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The specified value is less than 1.</exception>
    public int? PageSize
    {
        get => _pageSize;
        set
        {
            if (value.HasValue && value.Value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "PageSize cannot be less than 1.");
            }

            _pageSize = value;
        }
    }



    /// <summary>Rows to skip before the first yielded row, expressed as a server-side offset.</summary>
    /// <remarks>
    /// Superseded by <c>SkipItemCount</c>, which this property forwards to — the two were
    /// always the same idea, so there is one value rather than two that can disagree. When a
    /// paging template is set the skip is pushed into the query's offset, so the skipped rows are
    /// never fetched.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The specified value does not fit in an <see cref="int"/>.
    /// </exception>
    [Obsolete("Use SkipItemCount instead. ServerOffset forwards to it and will be removed in a future release.")]
    public long? ServerOffset
    {
        get => SkipItemCount;
        set => SkipItemCount = value.HasValue ? ToRowCount(value.Value, nameof(ServerOffset)) : 0;
    }



    /// <summary>Rows per round-trip.</summary>
    /// <remarks>
    /// Superseded by <see cref="PageSize"/>, which this property forwards to. The name changed
    /// because the meaning did: this is the size of each round-trip, not a cap on the total number
    /// of rows returned. Use <c>MaximumItemCount</c> for the total.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The specified value does not fit in an <see cref="int"/>.
    /// </exception>
    [Obsolete("Use PageSize instead. ServerLimit forwards to it and will be removed in a future release. Note the meaning changed: this is rows per round-trip, not a cap on the total — use MaximumItemCount for that.")]
    public long? ServerLimit
    {
        get => PageSize;
        set => PageSize = value.HasValue ? ToRowCount(value.Value, nameof(ServerLimit)) : (int?)null;
    }



    /// <summary>
    /// Narrows a <see cref="long"/> row count from an obsolete property to the <see cref="int"/>
    /// the base class uses, refusing to truncate silently.
    /// </summary>
    /// <param name="value">The row count to narrow.</param>
    /// <param name="propertyName">The obsolete property the value came from, for the message.</param>
    /// <returns>The value as an <see cref="int"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> does not fit in an <see cref="int"/>.
    /// </exception>
    private static int ToRowCount(long value, string propertyName)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException
            (
                nameof(value),
                value,
                $"{propertyName} does not fit in an Int32 and cannot be forwarded. Row counts are " +
                "Int32-wide; see Chris-Wolfgang/ETL-Abstractions#454."
            );
        }

        return (int)value;
    }




    /// <summary>
    /// SQL fragment appended to the command text when both
    /// <see cref="ServerOffset"/> and <see cref="ServerLimit"/> are set.
    /// Bound as Dapper parameters <c>@PageOffset</c> and <c>@PageLimit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see cref="PagingClauseTemplates.None"/>: no dialect is assumed, because
    /// there is no portable paging syntax. Activating paging without choosing a template throws.
    /// </para>
    /// <para>
    /// For SQL Server, set to <c>OFFSET @PageOffset ROWS FETCH NEXT @PageLimit ROWS ONLY</c>
    /// (and ensure the base SQL ends with an <c>ORDER BY</c>).
    /// </para>
    /// <para>
    /// Prefer the presets on <see cref="PagingClauseTemplates"/> over writing the clause by hand.
    /// </para>
    /// </remarks>
    public string? PagingClauseTemplate { get; [Obsolete("Configure PagingClauseTemplate through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; } = PagingClauseTemplates.None;



    /// <summary>
    /// When non-null, this function is invoked before extraction begins to determine
    /// the total record count, which is then reported via <see cref="Report.TotalItemCount"/>.
    /// Assign <see cref="DefaultTotalCountQuery"/> to use the library's built-in
    /// <c>SELECT COUNT(*)</c> subquery, or supply a custom function for a more efficient
    /// query. Defaults to <c>null</c> (total count is not fetched).
    /// </summary>
    public Func<CancellationToken, Task<int>>? TotalCountQuery { get; [Obsolete("Configure TotalCountQuery through DbExtractorOptions passed to the constructor instead. This setter will be removed in a future release.")] set; }



    /// <summary>
    /// The default total count implementation. Wraps <see cref="CommandText"/> in
    /// <c>SELECT COUNT(*) FROM (...) AS _count</c> and executes it using the same
    /// connection, parameters, and transaction as the extraction query.
    /// Assign this to <see cref="TotalCountQuery"/> to enable the built-in behavior.
    /// </summary>
    /// <remarks>
    /// Trailing semicolons are stripped automatically. If the command text contains
    /// an <c>ORDER BY</c> clause, some database providers (e.g. SQL Server) may reject
    /// it inside a derived table. Use a custom <see cref="TotalCountQuery"/> in that case.
    /// </remarks>
    public Func<CancellationToken, Task<int>> DefaultTotalCountQuery => ExecuteDefaultTotalCountQueryAsync;



    /// <summary>
    /// Runs the configured <see cref="TotalCountQuery"/> (or the built-in
    /// <see cref="DefaultTotalCountQuery"/> if none is assigned) and returns
    /// the result. Useful when the caller wants the total count without
    /// actually streaming the rows — for example, sizing a progress bar
    /// before kicking off the extract.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the count query.</param>
    /// <returns>The row count reported by the underlying query.</returns>
    /// <remarks>
    /// <para>
    /// Doesn't mutate any state on the extractor — does not touch
    /// <c>DbReport.TotalItemCount</c>, the stopwatch, or any of the
    /// progress counters. Safe to call any number of times before, during
    /// (different cancellation token), or after an <c>ExtractAsync</c>.
    /// </para>
    /// <para>
    /// Opens the connection on the owned-connection ctor path before running
    /// the query and disposes it after — same lifecycle as a full extraction.
    /// </para>
    /// </remarks>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var query = TotalCountQuery ?? DefaultTotalCountQuery;
        var needsOpen = (_ownsConnection || ManageConnection) && _connection.State != ConnectionState.Open;

        if (!needsOpen)
        {
            return await query(cancellationToken).ConfigureAwait(false);
        }

        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await query(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_ownsConnection)
            {
#if NET5_0_OR_GREATER
                await _connection.DisposeAsync().ConfigureAwait(false);
#else
                _connection.Dispose();
#endif
            }
            else
            {
#if NET5_0_OR_GREATER
                await _connection.CloseAsync().ConfigureAwait(false);
#else
                _connection.Close();
                await Task.CompletedTask.ConfigureAwait(false);
#endif
            }
        }
    }



    /// <inheritdoc/>
    protected override DbReport CreateProgressReport()
    {
        return new DbReport
        (
            CurrentItemCount,
            CurrentSkippedItemCount,
            _commandText,
            _stopwatch.ElapsedMilliseconds,
            _totalItemCount
        );
    }



    /// <summary>
    /// Returns a snapshot progress report. Visible to the test assembly via InternalsVisibleTo.
    /// </summary>
    internal DbReport GetProgressReport() => CreateProgressReport();



    /// <inheritdoc/>
#pragma warning disable MA0051
    protected override async IAsyncEnumerable<TRecord> ExtractWorkerAsync([EnumeratorCancellation] CancellationToken token)
#pragma warning restore MA0051
    {
        _stopwatch.Restart();
        _totalItemCount = null;
        LogExtractionStarted();

        // Owned-connection ctor path: open before the first query, dispose after.
        // ManageConnection=true path: open before the first query, CLOSE (don't
        // dispose) after — connection returns to the pool, caller keeps it.
        // try/finally in an async iterator is OK in C# 8+; the iterator runtime
        // routes break/exception through the finally on Dispose.
        var openedHere = false;
        if ((_ownsConnection || ManageConnection) && _connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(token).ConfigureAwait(false);
            openedHere = true;
        }

        try
        {
            if (ValidateSchemaOnStart)
            {
                await DbSchemaValidator.ValidateAsync<TRecord>(_connection, token).ConfigureAwait(false);
            }

            var param = Parameters ?? _dynamicParameters;

            // The TEMPLATE is the switch. It names the dialect, and since there is no portable
            // paging syntax there is nothing to emit without one. PageSize then decides whether
            // that clause drives a single query or a walk across pages.
            var templateChosen = !string.IsNullOrWhiteSpace(PagingClauseTemplate);

            EnsurePagingConfigurationValid(templateChosen);

            // A template with nothing to bound is not worth a clause: it would send
            // "FETCH NEXT 2147483647", which nothing here has verified every engine accepts.
            var paging = templateChosen
                && (SkipItemCount > 0 || MaximumItemCount != int.MaxValue || PageSize.HasValue);

            // With a template in hand the skip belongs in the query's offset, so the rows are
            // never fetched at all. The client-side skip below must then not run, or they would
            // be skipped twice.
            var serverSideSkip = paging && SkipItemCount > 0;

            if (paging)
            {
                // Runs ONCE, before the first page. Per page it would fail on page two against
                // paging's own parameter names, which page one just added.
                EnsurePagingParametersNotAlreadySupplied();
                param ??= new DynamicParameters();
            }
            else if (SkipItemCount > 0)
            {
                LogSkipWithoutPaging();
            }

            if (TotalCountQuery != null)
            {
                _totalItemCount = await TotalCountQuery(token).ConfigureAwait(false);
            }

            if (serverSideSkip)
            {
                // The rows genuinely were skipped, just not by us. Leaving the counter at zero
                // would silently change an observable the moment the skip moved server-side.
                for (var skipped = 0; skipped < SkipItemCount; skipped++)
                {
                    IncrementCurrentSkippedItemCount();
                }
            }

            var commandText = paging ? _commandText + " " + PagingClauseTemplate : _commandText;

            // rowsReceived counts rows the READER produced, including ones that failed to parse.
            // CurrentItemCount counts rows YIELDED. The offset and the short-page test run off
            // the first; the maximum clamp runs off the second. Conflating them is the whole bug:
            // advancing the offset by rows yielded re-fetches every row that failed to parse and
            // duplicates the good rows behind them, and testing the short page against rows
            // yielded ends the walk at the first page containing one.
            long rowsReceived = 0;
            long rowIndex = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                long requestedLimit = 0;

                if (paging)
                {
                    // MaximumItemCount defaults to int.MaxValue, so this is "all the rest" when
                    // the caller set no maximum. Computed in long: the offset can exceed int once
                    // a skip is added even though both operands fit.
                    var remaining = (long)MaximumItemCount - CurrentItemCount;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    requestedLimit = PageSize.HasValue
                        ? Math.Min((long)PageSize.Value, remaining)
                        : remaining;

                    SetPagingParameters(param!, (long)SkipItemCount + rowsReceived, requestedLimit);
                }

                // The reader is driven with a manual ReadAsync loop, and row mapping
                // goes through a standalone Dapper row-parser delegate, rather than
                // Dapper's own QueryUnbufferedAsync<T> IAsyncEnumerable. That matters:
                // QueryUnbufferedAsync's read-and-map loop lives inside ONE compiler-
                // generated async-iterator state machine. When mapping throws mid-loop,
                // the iterator's finally block runs and the state machine transitions
                // to "finished" — so even catching the exception at the call site,
                // the NEXT MoveNextAsync just returns false. Skip would silently
                // truncate the result set after the first bad row instead of
                // continuing past it. Reading via our own loop keeps ReadAsync's
                // cursor alive across a caught mapping failure, so Skip actually
                // skips-and-continues.
                var command = new CommandDefinition(commandText, param, _transaction, CommandTimeoutSeconds, CommandType, cancellationToken: token);

                long rowsThisPage = 0;

                using (var reader = await _connection.ExecuteReaderAsync(command).ConfigureAwait(false))
                {
                    var parseRow = reader.GetRowParser<TRecord>();

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();

                        // Counted before anything can reject the row, so a row that fails to parse
                        // still advances the offset. It occupied a position in the source and the
                        // next page must start after it.
                        rowIndex++;
                        rowsThisPage++;

                        TRecord record;
                        try
                        {
                            record = parseRow(reader);
                        }
                        catch (System.Data.DataException ex)
                        {
                            // Scoped to DataException — the type Dapper wraps row-materialization
                            // failures in (e.g. "Error parsing column N") — so ErrorPolicy only ever
                            // sees per-row failures. Catching every exception type here would also
                            // catch connection-level failures (a dropped connection, a syntax error
                            // surfacing lazily); those aren't per-row, and ReadAsync would likely keep
                            // throwing the same fault on every subsequent call, so routing them
                            // through ItemErrorAction.Skip would spin the loop instead of terminating.
                            var action = HandleItemError
                            (
                                new ItemErrorContext
                                (
                                    rowIndex,
                                    ex,
                                    rawContent: null
                                )
                            );
                            if (action == ItemErrorAction.Abort)
                            {
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                            }
                            // ItemErrorAction.Skip — HandleItemError already incremented
                            // the error-item counter on the base. Log and continue.
                            LogDebugRowErrorSkipped(rowIndex, ex);
                            continue;
                        }

                        // Only when the database did not do the skipping for us.
                        if (!serverSideSkip && rowIndex <= SkipItemCount)
                        {
                            IncrementCurrentSkippedItemCount();
                            LogDebugRowSkipped(rowIndex);
                            continue;
                        }

                        if (CurrentItemCount >= MaximumItemCount)
                        {
                            LogDebugMaxReached();
                            LogExtractionCompleted();
                            yield break;
                        }

                        LogDebugRowExtracted(rowIndex);
                        IncrementCurrentItemCount();
                        yield return record;
                    }
                }

                if (!paging)
                {
                    break;
                }

                rowsReceived += rowsThisPage;

                // A page that came back short of what it asked for is the last one. Compared
                // against rows RECEIVED so a page full of unparseable rows does not read as
                // exhaustion.
                if (rowsThisPage < requestedLimit)
                {
                    break;
                }
            }

            LogExtractionCompleted();
        }
        finally
        {
            if (_ownsConnection)
            {
#if NET5_0_OR_GREATER
                await _connection.DisposeAsync().ConfigureAwait(false);
#else
                _connection.Dispose();
#endif
            }
            else if (openedHere)
            {
                // ManageConnection path — close (do NOT dispose; caller owns it).
#if NET5_0_OR_GREATER
                await _connection.CloseAsync().ConfigureAwait(false);
#else
                _connection.Close();
                await Task.CompletedTask.ConfigureAwait(false);
#endif
            }
        }
    }



    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private Task<int> ExecuteDefaultTotalCountQueryAsync(CancellationToken token)
    {
        var sanitized = SanitizeCommandTextForCount(_commandText);
        var countSql = $"SELECT COUNT(*) FROM ({sanitized}) AS _count";
        var param = Parameters ?? _dynamicParameters;
        return _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(countSql, param, _transaction, CommandTimeoutSeconds, cancellationToken: token));
    }

    /// <summary>
    /// Verifies the paging knobs are coherent before the first query goes out.
    /// </summary>
    /// <param name="templateChosen">Whether a paging dialect has been named.</param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="PageSize"/> is set but <see cref="PagingClauseTemplate"/> is
    /// <see cref="PagingClauseTemplates.None"/>.
    /// </exception>
    private void EnsurePagingConfigurationValid(bool templateChosen)
    {
        if (!PageSize.HasValue || templateChosen)
        {
            return;
        }

        throw new InvalidOperationException
        (
            "PageSize was set without PagingClauseTemplate, so no paging clause can be emitted — " +
            "paging syntax is dialect-specific and no portable form exists. Choose a preset from " +
            "PagingClauseTemplates (for example PagingClauseTemplates.SqlServer, .PostgreSql, " +
            ".MySql, .Sqlite, .Oracle or .Db2), or supply your own clause referencing @PageOffset " +
            "and @PageLimit. To run unpaged instead, clear PageSize."
        );
    }



    /// <summary>
    /// Rejects a caller-supplied parameter whose name server-side paging also generates.
    /// </summary>
    /// <remarks>
    /// It cannot run per page: page one adds the paging parameters, so page two would find them
    /// already present and reject the query the extractor itself configured. It runs once, before
    /// the first page.
    /// <para>
    /// <c>EtlParameterSet</c> can detect a collision itself, but the <c>DynamicParameters</c>
    /// branch cannot — its <c>Add</c> silently overwrites, so the caller's value would disappear
    /// without a word.
    /// </para>
    /// <para>
    /// Two routes can carry a caller's parameters and both are checked: the constructor
    /// dictionary, and the obsolete <see cref="Parameters"/> property — which takes
    /// <em>precedence</em> over the dictionary where the parameter set is resolved, so checking
    /// only the dictionary would miss it entirely. Dapper stores names without the leading
    /// <c>@</c>, hence both spellings are compared.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A generated paging parameter name was already supplied by the caller.
    /// </exception>
    private void EnsurePagingParametersNotAlreadySupplied()
    {
        foreach (var generated in new[] { "@PageOffset", "@PageLimit" })
        {
            // ContainsKey resolves against _parameters' own comparer, which is deliberately
            // StringComparer.Ordinal, so it would miss "@pagelimit" and the leading-@ variants.
            // Scan explicitly with the collision-safe comparison instead.
            var suppliedByDictionary = false;
            if (_parameters is not null)
            {
                foreach (var key in _parameters.Keys)
                {
                    if (ParameterName.Matches(key, generated))
                    {
                        suppliedByDictionary = true;
                        break;
                    }
                }
            }

            var suppliedByProperty = false;
            var names = Parameters?.ParameterNames;
            if (names is not null)
            {
                foreach (var name in names)
                {
                    if (ParameterName.Matches(name, generated))
                    {
                        suppliedByProperty = true;
                        break;
                    }
                }
            }

            if (suppliedByDictionary || suppliedByProperty)
            {
                throw new InvalidOperationException
                (
                    $"Parameter '{generated}' was supplied by the caller and is also generated by " +
                    "server-side paging, so it cannot be applied twice. Either stop supplying " +
                    $"'{generated}' and let paging provide it, or clear PagingClauseTemplate " +
                    "and page through the command text yourself."
                );
            }
        }
    }


    /// <summary>
    /// Points the paging parameters at one page. Called once per round-trip, so it overwrites
    /// rather than accumulates: both parameter shapes replace an existing value by name.
    /// </summary>
    /// <param name="param">The parameter set carrying the query's parameters.</param>
    /// <param name="offset">Rows for the database to pass over before the first returned row.</param>
    /// <param name="limit">Rows this round-trip should return at most.</param>
    private static void SetPagingParameters(SqlMapper.IDynamicParameters param, long offset, long limit)
    {
        // Both parameter shapes accept additions, by different methods.
        switch (param)
        {
            case EtlParameterSet set:
                set.Add("@PageOffset", offset);
                set.Add("@PageLimit", limit);
                break;

            default:
                var dynamic = (DynamicParameters)param;
                dynamic.Add("@PageOffset", offset);
                dynamic.Add("@PageLimit", limit);
                break;
        }
    }



    /// <summary>
    /// Strips trailing semicolons from the command text so it can be safely
    /// wrapped in a <c>SELECT COUNT(*) FROM (...)</c> subquery.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The command text is empty or whitespace-only.
    /// </exception>
    private static string SanitizeCommandTextForCount(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new InvalidOperationException
            (
                "The default total count query requires a non-empty command text. " +
                "Provide a custom TotalCountQuery when the extractor command text cannot be wrapped safely."
            );
        }

        // Strip any trailing run of semicolons and *all* whitespace — including
        // non-breaking space and other Unicode whitespace that a hard-coded char
        // list would miss. Loop on TrimEnd() / TrimEnd(';') until both passes
        // become no-ops, so interleaved cases like "... FROM People; ; ;" (or
        // "; ;") fully collapse.
        var result = commandText;
        while (true)
        {
            var trimmed = result.TrimEnd().TrimEnd(';');
            if (trimmed.Length == result.Length)
            {
                return trimmed.TrimEnd();
            }

            result = trimmed;
        }
    }



    // ------------------------------------------------------------------
    // Logging helpers
    // ------------------------------------------------------------------

    private void LogExtractionStarted()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation
            (
                "Extraction started for {RecordType}. CommandText={CommandText}, " +
                "SkipItemCount={SkipItemCount}, MaximumItemCount={MaximumItemCount}",
                typeof(TRecord).Name,
                _commandText,
                SkipItemCount,
                MaximumItemCount
            );
        }

        if (_logger.IsEnabled(LogLevel.Debug) && _parameters != null)
        {
            foreach (var kvp in _parameters)
            {
                _logger.LogDebug
                (
                    "Parameter @{Name} = {Value}",
                    kvp.Key,
                    kvp.Value
                );
            }
        }
    }



    private void LogExtractionCompleted()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation
            (
                "Extraction completed for {RecordType}: {ItemCount} items extracted, " +
                "{SkippedCount} skipped in {ElapsedMs}ms",
                typeof(TRecord).Name,
                CurrentItemCount,
                CurrentSkippedItemCount,
                _stopwatch.ElapsedMilliseconds
            );
        }
    }



    private void LogDebugRowSkipped(long rowIndex)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug
            (
                "Skipping row {RowIndex} ({SkippedCount}/{SkipItemCount})",
                rowIndex,
                CurrentSkippedItemCount,
                SkipItemCount
            );
        }
    }



    private void LogSkipWithoutPaging()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning
            (
                "SkipItemCount={SkipItemCount} is being applied client-side: the database returns " +
                "those rows and they are discarded on arrival. Set PagingClauseTemplate to your " +
                "dialect (PagingClauseTemplates.SqlServer, .PostgreSql, .MySql, .Sqlite, .Oracle " +
                "or .Db2) to push the skip into the query's offset so they are never fetched",
                SkipItemCount
            );
        }
    }



    private void LogDebugMaxReached()
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug
            (
                "MaximumItemCount ({MaximumItemCount}) reached, stopping extraction",
                MaximumItemCount
            );
        }
    }



    private void LogDebugRowExtracted(long rowIndex)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug
            (
                "Extracted row {RowIndex} (item #{ItemCount})",
                rowIndex,
                CurrentItemCount + 1
            );
        }
    }



    private void LogDebugRowErrorSkipped(long rowIndex, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug
            (
                exception,
                "Row {RowIndex} skipped by ErrorPolicy: {ExceptionMessage}",
                rowIndex,
                exception.Message
            );
        }
    }

    /// <summary>
    /// Copies <paramref name="options"/> onto this instance. A <c>null</c> options object leaves
    /// every property at its default.
    /// </summary>
    /// <param name="options">The configuration to apply, or <c>null</c>.</param>
    private void ApplyOptions(DbExtractorOptions? options)
    {
#pragma warning disable CS0618 // ApplyOptions is the supported replacement for these setters.
        if (options is null)
        {
            return;
        }

        CommandTimeout = options.CommandTimeout;
        CommandType = options.CommandType;
        ManageConnection = options.ManageConnection;
        ValidateSchemaOnStart = options.ValidateSchemaOnStart;
        PagingClauseTemplate = options.PagingClauseTemplate;

        // PageSize wins over the obsolete ServerLimit; ServerOffset lands on SkipItemCount,
        // which is the same idea and now the only place it lives.
        PageSize = options.PageSize
            ?? (options.ServerLimit.HasValue ? ToRowCount(options.ServerLimit.Value, nameof(DbExtractorOptions.ServerLimit)) : (int?)null);

        if (options.ServerOffset.HasValue)
        {
            SkipItemCount = ToRowCount(options.ServerOffset.Value, nameof(DbExtractorOptions.ServerOffset));
        }
        TotalCountQuery = options.TotalCountQuery;
#pragma warning restore CS0618
    }
}
