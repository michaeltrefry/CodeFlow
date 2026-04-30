namespace CodeFlow.Runtime.Authority.Admission;

/// <summary>
/// Result of an admission validator run. <see cref="Accepted{T}"/> carries the minted
/// admitted handoff value; <see cref="Rejected{T}"/> carries structured rejection
/// evidence. Consumers pattern-match on the two cases — the closed hierarchy makes the
/// "forgot to handle the rejected case" footgun visible at compile time.
///
/// The protection that pairs admitted-handoff types with their validators lives on the
/// admitted type (internal constructors, validator-only minting). The wrapper itself is
/// freely constructible so test fakes and re-mint paths can shape an <see cref="Admission{T}"/>
/// without reflection.
/// </summary>
public abstract record Admission<T> where T : class
{
    private protected Admission() { }

    /// <summary>True when this admission produced a value; false when it produced a rejection.</summary>
    public bool IsAccepted => this is Accepted<T>;

    public static Admission<T> Accept(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Accepted<T>(value);
    }

    public static Admission<T> Reject(Rejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return new Rejected<T>(rejection);
    }
}

/// <summary>The validator minted an admitted value. Consumers operate on <see cref="Value"/>.</summary>
public sealed record Accepted<T>(T Value) : Admission<T> where T : class;

/// <summary>The validator refused to mint an admitted value. <see cref="Reason"/> carries the evidence.</summary>
public sealed record Rejected<T>(Rejection Reason) : Admission<T> where T : class;
