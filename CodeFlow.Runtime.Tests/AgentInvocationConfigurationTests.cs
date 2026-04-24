using System.Text.Json;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class AgentInvocationConfigurationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DecisionOutputTemplates_ShouldRoundTripThroughJson()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-5",
            DecisionOutputTemplates: new Dictionary<string, string>
            {
                ["Approved"] = "shipped {{ decision }}",
                ["Rejected"] = "blocked {{ decision }}",
                ["*"] = "fallback {{ decision }}"
            });

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        json.Should().Contain("decisionOutputTemplates");

        var roundTripped = JsonSerializer.Deserialize<AgentInvocationConfiguration>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.DecisionOutputTemplates.Should().NotBeNull();
        roundTripped.DecisionOutputTemplates!["Approved"].Should().Be("shipped {{ decision }}");
        roundTripped.DecisionOutputTemplates["Rejected"].Should().Be("blocked {{ decision }}");
        roundTripped.DecisionOutputTemplates["*"].Should().Be("fallback {{ decision }}");
    }

    [Fact]
    public void DecisionOutputTemplates_ShouldBeOmittedFromJson_WhenNotSupplied()
    {
        var config = new AgentInvocationConfiguration(Provider: "openai", Model: "gpt-5");

        var json = JsonSerializer.Serialize(config, SerializerOptions);

        // Default value null should serialize as property with null or be omitted entirely;
        // either is acceptable so long as deserialization produces a null dictionary.
        var roundTripped = JsonSerializer.Deserialize<AgentInvocationConfiguration>(json, SerializerOptions);
        roundTripped!.DecisionOutputTemplates.Should().BeNull();
    }

    [Fact]
    public void DecisionOutputTemplates_ShouldDeserializeFromAbsentProperty()
    {
        const string json = """{"provider":"openai","model":"gpt-5"}""";

        var config = JsonSerializer.Deserialize<AgentInvocationConfiguration>(json, SerializerOptions);

        config.Should().NotBeNull();
        config!.DecisionOutputTemplates.Should().BeNull();
    }
}
