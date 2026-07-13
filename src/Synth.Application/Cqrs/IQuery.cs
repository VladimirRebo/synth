namespace Synth.Application.Cqrs;

/// <summary>
/// Marker for a query — a single read use case whose execution yields <typeparamref name="TResult"/>
/// with no observable side effects. The read-side twin of <see cref="ICommand{TResult}"/>; same
/// hand-rolled seam (issue #82), same explicit-DI resolution, no bus/mediator/pipeline. No query has
/// been migrated yet — this interface is introduced alongside the command side so the read path has a
/// home the moment the first query use case is carved out.
/// </summary>
/// <typeparam name="TResult">Value the query produces when handled.</typeparam>
public interface IQuery<TResult> { }

/// <summary>
/// Handles a single <typeparamref name="TQuery"/>, producing its <typeparamref name="TResult"/>.
/// Resolved directly from DI (one handler per query) and invoked via <see cref="HandleAsync"/>.
/// </summary>
/// <typeparam name="TQuery">The query this handler answers.</typeparam>
/// <typeparam name="TResult">The query's result type.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>Executes <paramref name="query"/> and returns its result.</summary>
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
