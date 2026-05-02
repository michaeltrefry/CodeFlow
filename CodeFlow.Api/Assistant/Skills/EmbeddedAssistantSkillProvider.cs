using System.Reflection;

namespace CodeFlow.Api.Assistant.Skills;

/// <summary>
/// Reads assistant skills from embedded markdown resources. Resources are committed under
/// <c>CodeFlow.Api/Assistant/Skills/*.md</c> and exposed via the <c>&lt;EmbeddedResource&gt;</c>
/// glob in the project file. The provider parses every matching resource at construction and
/// caches the result — there is no hot-reload and no DB access; new skills require a redeploy.
/// </summary>
/// <remarks>
/// Singleton-friendly. Construction is the only place a malformed skill file blows up — that
/// fail-fast keeps the homepage assistant from booting half-broken with one missing skill.
/// </remarks>
public sealed class EmbeddedAssistantSkillProvider : IAssistantSkillProvider
{
    /// <summary>
    /// Manifest-resource suffix that marks a skill source. Anything under
    /// <c>CodeFlow.Api/Assistant/Skills/</c> with a <c>.md</c> extension counts.
    /// </summary>
    private const string ResourcePrefix = "CodeFlow.Api.Assistant.Skills.";
    private const string ResourceSuffix = ".md";

    private readonly Dictionary<string, AssistantSkill> byKey;
    private readonly IReadOnlyList<AssistantSkill> ordered;

    /// <summary>
    /// Production constructor — pulls skill sources from this assembly's manifest resources.
    /// </summary>
    public EmbeddedAssistantSkillProvider()
        : this(LoadEmbeddedSources(typeof(EmbeddedAssistantSkillProvider).Assembly))
    {
    }

    /// <summary>
    /// Test-friendly constructor that takes pre-materialized <c>(fileName, content)</c> pairs.
    /// Used by unit tests to exercise frontmatter parsing, missing-key, and duplicate-key paths
    /// without round-tripping through the build's embedded-resource pipeline.
    /// </summary>
    internal EmbeddedAssistantSkillProvider(IEnumerable<(string FileName, string Content)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        byKey = new Dictionary<string, AssistantSkill>(StringComparer.Ordinal);
        var fileBySkillKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (fileName, content) in sources)
        {
            var skill = AssistantSkillParser.Parse(fileName, content);

            if (!byKey.TryAdd(skill.Key, skill))
            {
                var existingFile = fileBySkillKey[skill.Key];
                throw new InvalidSkillSourceException(
                    fileName,
                    $"Duplicate skill key '{skill.Key}' (already defined in '{existingFile}').");
            }
            fileBySkillKey[skill.Key] = fileName;
        }

        ordered = byKey.Values
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<AssistantSkill> List() => ordered;

    public AssistantSkill? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return byKey.TryGetValue(key, out var skill) ? skill : null;
    }

    private static IEnumerable<(string FileName, string Content)> LoadEmbeddedSources(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            yield return (resourceName, content);
        }
    }
}
