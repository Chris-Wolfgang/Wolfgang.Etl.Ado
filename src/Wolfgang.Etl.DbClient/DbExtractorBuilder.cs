using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Wolfgang.Etl.Abstractions;

namespace Wolfgang.Etl.DbClient;

/// <summary>
/// Default <see cref="IDbExtractorBuilder{T}"/> implementation. Configuration
/// setters mutate the wrapped <see cref="DbExtractor{TRecord}"/>; pipeline
/// operators materialize a fresh <see cref="IEtlPipeline{T}"/> from the
/// configured extractor and delegate to it.
/// </summary>
/// <remarks>
/// The <see cref="EtlPipelineSourceExtensions.From{T, TProgress}"/> materialization
/// wraps <c>extractor.ExtractAsync(token)</c> in a delegate read at enumeration
/// time, so setters called on this builder still take effect even after a
/// pipeline operator has consumed the builder — the extractor instance is the
/// single source of truth throughout.
/// </remarks>
internal sealed class DbExtractorBuilder<T> : IDbExtractorBuilder<T>
    where T : notnull
{
    private readonly EtlPipeline _pipeline;
    private readonly DbExtractor<T> _extractor;



    internal DbExtractorBuilder(EtlPipeline pipeline, DbExtractor<T> extractor)
    {
        _pipeline = pipeline;
        _extractor = extractor;
    }



    // -----------------------------------------------------------------
    // Fluent setters
    // -----------------------------------------------------------------

#pragma warning disable CS0618
    // This builder configures an extractor/loader that already exists - it is handed the
    // instance in its constructor, so there is no constructor of its own to route options
    // through. Writing the (now deprecated) setters is the only available path until the
    // builder is reshaped to construct the instance itself.
    public IDbExtractorBuilder<T> CommandType(CommandType commandType)
    {
        _extractor.CommandType = commandType;
        return this;
    }



    public IDbExtractorBuilder<T> ManageConnection(bool manage)
    {
        _extractor.ManageConnection = manage;
        return this;
    }



    public IDbExtractorBuilder<T> Parameters(DynamicParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        _extractor.Parameters = parameters;
        return this;
    }



    public IDbExtractorBuilder<T> ServerOffset(long? offset)
    {
        _extractor.ServerOffset = offset;
        return this;
    }



    public IDbExtractorBuilder<T> PageSize(int? pageSize)
    {
        _extractor.PageSize = pageSize;
        return this;
    }



    /// <inheritdoc/>
    public IDbExtractorBuilder<T> SkipItemCount(int skip)
    {
        _extractor.SkipItemCount = skip;
        return this;
    }



    /// <inheritdoc/>
    public IDbExtractorBuilder<T> MaximumItemCount(int maximum)
    {
        _extractor.MaximumItemCount = maximum;
        return this;
    }



    /// <inheritdoc/>
    public IDbExtractorBuilder<T> ServerLimit(long? limit)
    {
        _extractor.ServerLimit = limit;
        return this;
    }



    public IDbExtractorBuilder<T> PagingClauseTemplate(string? template)
    {
        // null is meaningful here: it is PagingClauseTemplates.None, "no dialect chosen".
        // Rejecting it would make .PagingClauseTemplate(PagingClauseTemplates.None) throw.
        _extractor.PagingClauseTemplate = template;
        return this;
    }



    public IDbExtractorBuilder<T> TotalCountQuery(Func<CancellationToken, Task<int>> countQuery)
    {
        if (countQuery is null)
        {
            throw new ArgumentNullException(nameof(countQuery));
        }

        _extractor.TotalCountQuery = countQuery;
        return this;
    }



    // -----------------------------------------------------------------
    // IEtlPipeline<T> — delegate through a materialized pipeline. The
    // extractor is passed by reference so any further setter calls on
    // this builder still influence enumeration (see class remarks).
    // -----------------------------------------------------------------


#pragma warning restore CS0618


    public IEtlPipeline<TOut> Through<TOut>(ITransformAsync<T, TOut> transformer)
        where TOut : notnull
        => Materialize().Through(transformer);



    public IEtlPipeline<TOut> Through<TOut>(ITransformWithCancellationAsync<T, TOut> transformer)
        where TOut : notnull
        => Materialize().Through(transformer);



    public IEtlPipeline<TOut> Through<TOut>(Func<IAsyncEnumerable<T>, IAsyncEnumerable<TOut>> stage)
        where TOut : notnull
        => Materialize().Through(stage);



    public IEtlPipeline<TOut> Through<TOut>(Func<IAsyncEnumerable<T>, CancellationToken, IAsyncEnumerable<TOut>> stage)
        where TOut : notnull
        => Materialize().Through(stage);



    public IEtlPipelineSink To<TProgress>(LoaderBase<T, TProgress> loader)
        where TProgress : notnull
        => Materialize().To(loader);



    public IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken token = default)
        => Materialize().AsAsyncEnumerable(token);



    /// <summary>
    /// The extractor being configured. Internal so the <c>DbLoader</c>
    /// terminator extensions can materialize the pipeline through the same
    /// extractor when a caller chains <c>DbExtractor(...).DbLoader(...)</c>
    /// without an intermediate operator.
    /// </summary>
    internal DbExtractor<T> Extractor => _extractor;



    private IEtlPipeline<T> Materialize()
    {
        return _pipeline.From(_extractor);
    }
}
