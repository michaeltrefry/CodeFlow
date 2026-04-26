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
}
