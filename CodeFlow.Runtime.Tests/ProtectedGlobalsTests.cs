using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ProtectedGlobalsTests
{
    [Fact]
    public void ReservedKeys_ContainsWorkDir()
    {
        ProtectedGlobals.ReservedKeys.Should().Contain("workDir");
        ProtectedGlobals.IsReserved("workDir").Should().BeTrue();
    }

    [Fact]
    public void ReservedKeys_ContainsTraceId()
    {
        ProtectedGlobals.ReservedKeys.Should().Contain("traceId");
        ProtectedGlobals.IsReserved("traceId").Should().BeTrue();
    }

    [Fact]
    public void IsReserved_FalseForUserKey()
    {
        // Sanity check the registry doesn't accidentally swallow author-defined globals.
        ProtectedGlobals.IsReserved("requestKind").Should().BeFalse();
        ProtectedGlobals.IsReserved("workdir").Should().BeFalse(); // case-sensitive
    }
}
