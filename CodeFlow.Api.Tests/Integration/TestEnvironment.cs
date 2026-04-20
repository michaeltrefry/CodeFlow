using System.Runtime.CompilerServices;

namespace CodeFlow.Api.Tests.Integration;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Seed()
    {
        // Must run before Program.cs executes under WebApplicationFactory — WebApplication.CreateBuilder
        // reads configuration synchronously at construction time, before test-side config hooks run.
        Environment.SetEnvironmentVariable("Secrets__MasterKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    }
}
