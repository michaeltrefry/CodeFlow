namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Validates a raw request <typeparamref name="TInput"/> and either mints an admitted handoff
/// value <typeparamref name="TAdmitted"/> or returns a <see cref="Rejection"/>. Implementations
/// are deliberately small and side-effect-free — every concern that needs DB or filesystem
/// state is split into a separate validator so the closed-form contract stays trivial to
/// reason about and re-mint.
///
/// Re-mint discipline: callers persist the source request alongside the admitted value's
/// evidence; on a fresh process they call <see cref="Validate"/> again with the persisted
/// request. The same input that produced an admitted value yesterday must produce an
/// equivalent admitted value today (modulo wall-clock fields). Validators must therefore
/// be deterministic for a given input + injected dependencies.
/// </summary>
public interface IAdmissionValidator<TInput, TAdmitted> where TAdmitted : class
{
    Admission<TAdmitted> Validate(TInput input);
}

/// <summary>
/// Async variant for validators that need to read persisted state (DB rows, artifact
/// stores, remote services) before deciding whether to admit. Sync validators implement
/// <see cref="IAdmissionValidator{TInput, TAdmitted}"/>; only reach for the async variant
/// when state lookup is unavoidable, since async paths complicate re-mint.
/// </summary>
public interface IAsyncAdmissionValidator<TInput, TAdmitted> where TAdmitted : class
{
    Task<Admission<TAdmitted>> ValidateAsync(TInput input, CancellationToken cancellationToken = default);
}
