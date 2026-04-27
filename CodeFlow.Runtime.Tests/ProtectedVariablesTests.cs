using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ProtectedVariablesTests
{
    [Fact]
    public void ReservedKeys_ContainsWorkDir()
    {
        ProtectedVariables.ReservedKeys.Should().Contain("workDir");
        ProtectedVariables.IsReserved("workDir").Should().BeTrue();
    }

    [Fact]
    public void ReservedKeys_ContainsTraceId()
    {
        ProtectedVariables.ReservedKeys.Should().Contain("traceId");
        ProtectedVariables.IsReserved("traceId").Should().BeTrue();
    }

    [Fact]
    public void IsReserved_FalseForUserKey()
    {
        // Sanity check the registry doesn't accidentally swallow author-defined variables.
        ProtectedVariables.IsReserved("requestKind").Should().BeFalse();
        ProtectedVariables.IsReserved("workdir").Should().BeFalse(); // case-sensitive
    }

    [Fact]
    public void ReservedNamespaces_RejectsLoopPrefix()
    {
        // P3: the framework owns __loop.* — agents calling `setWorkflow('__loop.foo', ...)`
        // are rejected, so the runtime's rejection-history bookkeeping cannot be clobbered.
        ProtectedVariables.IsReserved("__loop.rejectionHistory").Should().BeTrue();
        ProtectedVariables.IsReserved("__loop.anyFutureKey").Should().BeTrue();
    }

    [Fact]
    public void ReservedNamespaces_DoesNotRejectBareNamespaceWithoutDot()
    {
        // The check requires a `.` after the namespace prefix — a bare key named like the
        // namespace itself isn't reserved, so authors who happen to choose `__loop` as a
        // top-level key get a clear "you cannot pick this name" error from a different validator
        // (port-coupling / V4) rather than this guard.
        ProtectedVariables.IsReserved("__loop").Should().BeFalse();
    }

    [Fact]
    public void ReservedNamespaces_DoesNotRejectUnrelatedDoubleUnderscore()
    {
        // Authors might prefix their own conventions with double-underscore — only the
        // explicit `__loop` namespace is reserved.
        ProtectedVariables.IsReserved("__user.foo").Should().BeFalse();
        ProtectedVariables.IsReserved("__internal.bar").Should().BeFalse();
    }
}
