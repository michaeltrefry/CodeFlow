namespace CodeFlow.Api.Tests.Integration;

/// <summary>
/// xunit collection definition that shares ONE <see cref="CodeFlowApiFactory"/> across
/// every integration test class tagged with <c>[Collection("CodeFlowApi")]</c>. Replaces
/// the per-class <c>IClassFixture&lt;CodeFlowApiFactory&gt;</c> pattern (sc-699 Phase A):
/// MariaDB and RabbitMQ Testcontainers spin up exactly once per test-assembly run rather
/// than once per test class.
///
/// Tests inside the collection run **serially** by default. The integration tests already
/// use Guid keys for entities they create, so cross-class state leaks are not a structural
/// concern; classes that need a guaranteed clean slate (e.g. seeder tests asserting on
/// absolute row counts) should reset the relevant tables in their constructor or
/// <c>InitializeAsync</c> rather than relying on a fresh container. Phase B re-introduces
/// parallelism within the collection via per-test database names.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CodeFlowApiCollection : ICollectionFixture<CodeFlowApiFactory>
{
    public const string Name = "CodeFlowApi";

    // No body — xunit uses this class purely as a fixture-scope marker. The
    // [CollectionDefinition] attribute + the ICollectionFixture<TFixture> interface are the
    // entire contract.
}
