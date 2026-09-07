using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Dapper;

namespace Wolfgang.Etl.DbClient;

/// <summary>
/// Binds a caller-supplied parameter dictionary onto a command, dispatching on each value's type.
/// </summary>
/// <remarks>
/// This is the only place in the package aware of the underlying data-access library's parameter
/// hooks. Swapping that library means rewriting this class and nothing else — no public type
/// changes, because <see cref="EtlParameter"/> and the dictionary are BCL-only.
/// <para>
/// Implements the post-execution callback as well as parameter creation: output values are written
/// back once the command completes, which parameter creation alone cannot do.
/// </para>
/// </remarks>
internal sealed class EtlParameterSet : SqlMapper.IDynamicParameters, SqlMapper.IParameterCallbacks
{
    private readonly IDictionary<string, object> _source;
    private readonly List<KeyValuePair<EtlParameter, IDbDataParameter>> _writeBack = new();

    // Parameters the extractor adds itself (server paging). Kept separate so the caller's own
    // dictionary is never mutated - it is their object, and they may reuse it.
    private readonly Dictionary<string, object> _added = new(StringComparer.Ordinal);


    /// <summary>Initializes a new instance binding <paramref name="source"/>.</summary>
    /// <param name="source">The caller's parameter dictionary.</param>
    internal EtlParameterSet(IDictionary<string, object> source) => _source = source;


    /// <summary>
    /// True when <paramref name="source"/> contains a value needing this binder rather than the
    /// library's own dictionary handling.
    /// </summary>
    /// <param name="source">The dictionary to inspect.</param>
    /// <returns><c>true</c> when any value is an <see cref="EtlParameter"/> or <see cref="DbParameter"/>.</returns>
    internal static bool IsNeededFor(IDictionary<string, object> source)
    {
        foreach (var value in source.Values)
        {
            if (value is EtlParameter or DbParameter)
            {
                return true;
            }
        }

        return false;
    }


    /// <inheritdoc/>
    public void AddParameters(IDbCommand command, SqlMapper.Identity identity)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        foreach (var entry in Entries())
        {
            switch (entry.Value)
            {
                // The caller's own provider parameter: attach the instance itself, so the provider
                // writes any output value straight back into the object they still hold.
                case DbParameter supplied:
                    command.Parameters.Add(supplied);
                    break;

                case EtlParameter described:
                    command.Parameters.Add(Materialize(command, entry.Key, described));
                    break;

                default:
                    var plain = command.CreateParameter();
                    plain.ParameterName = entry.Key;
                    plain.Value = entry.Value ?? (object)DBNull.Value;
                    command.Parameters.Add(plain);
                    break;
            }
        }
    }


    /// <inheritdoc/>
    /// <remarks>
    /// Runs after the command completes. Only <see cref="EtlParameter"/> values need this — a
    /// caller-supplied <see cref="DbParameter"/> is the command's own parameter, so the provider
    /// has already populated it.
    /// </remarks>
    public void OnCompleted()
    {
        foreach (var pair in _writeBack)
        {
            pair.Key.SetValue(pair.Value.Value);
        }
    }


    /// <summary>Adds a parameter the extractor supplies itself, without touching the caller's dictionary.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The parameter value.</param>
    /// <exception cref="InvalidOperationException">
    /// The caller's dictionary already contains <paramref name="name"/>.
    /// </exception>
    /// <remarks>
    /// A collision is rejected rather than resolved. Emitting both does NOT reliably fail:
    /// SQL Server, PostgreSQL and MySQL resolve parameter names case-insensitively and quietly
    /// use whichever arrived last, so the query returns wrong rows with no error at all. Silently
    /// preferring one here would mean the caller's value or the extractor's paging is discarded
    /// just as quietly. Names are matched conservatively — case-insensitive, leading <c>@</c>
    /// optional — so <c>@pagelimit</c> collides with <c>@PageLimit</c>. That is stricter than
    /// SQLite, which alone treats the two as distinct; see <see cref="ParameterName"/> for why
    /// the stricter rule is preferred. A caller who
    /// supplies this name AND configures the feature that generates it has expressed two
    /// conflicting intentions, and only they can say which was meant.
    /// </remarks>
    internal void Add(string name, object value)
    {
        if (ContainsMatching(name))
        {
            throw new InvalidOperationException
            (
                $"Parameter '{name}' was supplied in the parameters dictionary and is also " +
                "generated automatically, so it cannot be applied twice. Server-side paging " +
                $"generates '{name}' when PagingClauseTemplate is set. Either remove " +
                $"'{name}' from the dictionary and let paging supply it, or clear " +
                "PagingClauseTemplate and page through the command text yourself."
            );
        }

        _added[name] = value;
    }



    private bool ContainsMatching(string name)
    {
        foreach (var key in _source.Keys)
        {
            if (ParameterName.Matches(key, name))
            {
                return true;
            }
        }

        return false;
    }


    private IEnumerable<KeyValuePair<string, object>> Entries()
    {
        foreach (var entry in _source)
        {
            yield return entry;
        }

        foreach (var entry in _added)
        {
            yield return entry;
        }
    }


    private IDbDataParameter Materialize(IDbCommand command, string name, EtlParameter described)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;

        if (described.DbType.HasValue)
        {
            parameter.DbType = described.DbType.Value;
        }

        // Assigning Direction can throw for providers that reject it (Microsoft.Data.Sqlite rejects
        // Output outright). That is deliberate: the provider's own message names the problem.
        parameter.Direction = described.Direction;

        if (described.Size.HasValue)
        {
            parameter.Size = described.Size.Value;
        }

        parameter.Value = described.BoxedValue ?? DBNull.Value;

        if (described.Direction != ParameterDirection.Input)
        {
            _writeBack.Add(new KeyValuePair<EtlParameter, IDbDataParameter>(described, parameter));
        }

        return parameter;
    }
}
