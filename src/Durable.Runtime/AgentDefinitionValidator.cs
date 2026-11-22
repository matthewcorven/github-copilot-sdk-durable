using System.Text.RegularExpressions;
using Durable;

namespace Durable.Runtime;

/// <summary>An agent definition the host refuses to use.</summary>
internal sealed class AgentDefinitionException(string message) : Exception(message);

/// <summary>
/// Validates an agent definition against the extensions the host actually has.
/// </summary>
/// <remarks>
/// The same rules run at import, at host startup, and on every invocation. A definition that
/// names something the host cannot supply is refused rather than silently narrowed: a run that
/// quietly drops an extension is a run whose behaviour does not match the definition that
/// describes it.
/// <para>
/// Extension names are resolved with <see cref="ExtensionRegistry.Find"/> rather than through
/// the runtime's own resolution, because that resolution filters unresolved names out. Filtering
/// is the right behaviour for a conversation that is already running; it is the wrong behaviour
/// for validation, where an unresolved name has to be an error.
/// </para>
/// </remarks>
internal static partial class AgentDefinitionValidator
{
    /// <summary>The pattern an agent name has to match.</summary>
    public const string NamePattern = "^[a-z0-9][a-z0-9._-]*$";

    [GeneratedRegex(NamePattern)]
    private static partial Regex NameRegex();

    /// <summary>Validates <paramref name="definition"/> against <paramref name="registry"/>.</summary>
    /// <exception cref="AgentDefinitionException">The definition names something the host cannot supply.</exception>
    /// <exception cref="DurableSandboxPolicyException">The definition's sandbox policy cannot be applied.</exception>
    public static void Validate(AgentDefinition definition, ExtensionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);

        ValidateName(definition);
        var selected = ValidateExtensions(definition, registry);
        ValidateTools(definition, selected);
        ValidateSandbox(definition);
        ValidatePermissions(definition);
    }

    private static void ValidateName(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || !NameRegex().IsMatch(definition.Name))
        {
            throw new AgentDefinitionException(
                $"agent name '{definition.Name}' is not valid. A name must match {NamePattern}.");
        }
    }

    private static IReadOnlyList<Extension> ValidateExtensions(AgentDefinition definition, ExtensionRegistry registry)
    {
        if (definition.Extensions is null)
        {
            return registry.Extensions;
        }

        var selected = new List<Extension>(definition.Extensions.Count);
        foreach (var name in definition.Extensions)
        {
            var extension = registry.Find(name);
            if (extension is null)
            {
                var installed = registry.Extensions.Select(candidate => candidate.Name).ToList();
                var listing = installed.Count == 0 ? "(none)" : string.Join(", ", installed);
                throw new AgentDefinitionException(
                    $"agent '{definition.Name}' names extension '{name}' which is not installed.\n"
                    + $"Installed extensions: {listing}.\n"
                    + "Install it, or remove it from the agent definition.");
            }

            selected.Add(extension);
        }

        return selected;
    }

    private static void ValidateTools(AgentDefinition definition, IReadOnlyList<Extension> selected)
    {
        if (definition.Tools is null)
        {
            return;
        }

        var available = selected
            .SelectMany(extension => extension.Tools)
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in definition.Tools)
        {
            if (available.Contains(name))
            {
                continue;
            }

            var searched = selected.Count == 0 ? "(none)" : string.Join(", ", selected.Select(extension => extension.Name));
            var offered = available.Count == 0 ? "(none)" : string.Join(", ", available.Order(StringComparer.Ordinal));
            throw new AgentDefinitionException(
                $"agent '{definition.Name}' names tool '{name}' which the extensions it selects do not provide.\n"
                + $"Extensions searched: {searched}.\n"
                + $"Tools available: {offered}.");
        }
    }

    private static void ValidateSandbox(AgentDefinition definition)
    {
        if (definition.Sandbox is { } sandbox)
        {
            DurableRuntime.ValidateSandboxPolicy(sandbox);
        }
    }

    private static void ValidatePermissions(AgentDefinition definition)
    {
        if (definition.Permissions is null)
        {
            return;
        }

        foreach (var text in definition.Permissions)
        {
            // Parse never throws: it splits on the first colon and trims. A grant that parses to
            // an empty kind is the malformed case, so the check is on the result rather than on
            // the parse.
            var grant = PermissionGrant.Parse(text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(grant.Kind))
            {
                throw new AgentDefinitionException(
                    $"agent '{definition.Name}' declares permission grant '{text}', which is not a grant. "
                    + "Write 'kind' or 'kind:scope', for example 'read' or 'read:/workspace'.");
            }
        }
    }
}
