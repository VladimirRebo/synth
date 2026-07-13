namespace Synth.Application.Cqrs;

/// <summary>
/// Marker for a command — a single write/side-effecting use case whose execution yields
/// <typeparamref name="TResult"/>. One command type per use case; the matching
/// <see cref="ICommandHandler{TCommand, TResult}"/> carries the behavior. This is a deliberately
/// hand-rolled CQRS seam (issue #82): no MediatR, no bus, no pipeline behaviors — a handler is
/// registered explicitly in DI and taken as a constructor parameter wherever it's dispatched,
/// matching this project's explicit-registration style everywhere else.
/// </summary>
/// <typeparam name="TResult">Value the command produces when handled.</typeparam>
public interface ICommand<TResult> { }

/// <summary>
/// Handles a single <typeparamref name="TCommand"/>, producing its <typeparamref name="TResult"/>.
/// Resolved directly from DI (one handler per command) and invoked by calling
/// <see cref="HandleAsync"/> — there is no mediator between caller and handler.
/// </summary>
/// <typeparam name="TCommand">The command this handler executes.</typeparam>
/// <typeparam name="TResult">The command's result type.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Executes <paramref name="command"/> and returns its result.</summary>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
