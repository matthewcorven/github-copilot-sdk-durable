using System.Text.Json;
using Durable;
using Durable.Runtime;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// Linked skills, MCP servers and tools: explicit authorization, least privilege, and safe failure.
/// Every negative case here is a regression guard: a link that is missing, incompatible, or wider
/// than the member's own grants must refuse the run rather than silently narrow or widen it.
/// </summary>
public sealed class LinkedCapabilityTests
{
    private static ExtensionRegistry RegistryWithTool(
        string extensionName = "linked",
        string toolName = "linked_tool",
        string? version = null)
    {
        var tool = CopilotTool.DefineTool(
            () => "linked tool result",
            factoryOptions: new AIFunctionFactoryOptions { Name = toolName });
        var registry = new ExtensionRegistry();
        registry.Install(new Extension(extensionName, tools: [tool], version: version));
        return registry;
    }

    [Fact]
    public void UnlistedToolLink_IsDeniedWithAnActionableError()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "not-installed" }],
        };

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurableLinks.Resolve(definition.Links, registry, null, definition.Permissions));

        Assert.Contains("not installed", error.Message, StringComparison.Ordinal);
        Assert.Contains("linked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMcpServer_IsDeniedAndNamesTheInstalledServers()
    {
        var servers = new DurableMcpServerRegistry();
        servers.Install(new DurableMcpServer("github", "2.0.0", ["search"]));

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Mcp, Name = "missing-server" }],
        };

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurableLinks.Resolve(definition.Links, new ExtensionRegistry(), servers, definition.Permissions));

        Assert.Contains("missing-server", error.Message, StringComparison.Ordinal);
        Assert.Contains("github", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompatibleLinkVersion_FailsClosed()
    {
        var registry = RegistryWithTool("linked", version: "1.0.0");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "linked", Version = "2.0.0" }],
        };

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurableLinks.Resolve(definition.Links, registry, null, definition.Permissions));

        Assert.Contains("2.0.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnversionedLink_AgainstVersionedHost_IsAllowed()
    {
        var registry = RegistryWithTool("linked", version: "1.0.0");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "linked" }],
        };

        var resolved = DurableLinks.Resolve(definition.Links, registry, null, definition.Permissions);

        Assert.Single(resolved);
        Assert.Equal("1.0.0", resolved[0].Version);
    }

    [Fact]
    public void LinkGrantWiderThanMember_IsRefused()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Permissions = ["read:/workspace"],
            Links =
            [
                new DurableLink
                {
                    Kind = DurableLinkKind.Tool,
                    Name = "linked",
                    Grants = ["write:/workspace"],
                },
            ],
        };

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurableLinks.Resolve(definition.Links, registry, null, definition.Permissions));

        Assert.Contains("cannot widen authority", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkGrantWithinMember_IsResolvedWithoutAddingAuthority()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Permissions = ["read:/workspace"],
            Links =
            [
                new DurableLink
                {
                    Kind = DurableLinkKind.Tool,
                    Name = "linked",
                    Grants = ["read:/workspace"],
                },
            ],
        };

        var resolved = DurableLinks.Resolve(definition.Links, registry, null, definition.Permissions);

        Assert.Single(resolved);
        Assert.Equal(["read:/workspace"], resolved[0].Grants);
        Assert.Equal(["read:/workspace"], definition.Permissions);
    }

    [Fact]
    public void McpServerManifestGrants_AreNotAppliedAsAuthority()
    {
        var servers = new DurableMcpServerRegistry();
        servers.Install(new DurableMcpServer("github", "2.0.0", ["search"], Grants: ["write:/"]));

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Permissions = ["read:/workspace"],
            Links = [new DurableLink { Kind = DurableLinkKind.Mcp, Name = "github" }],
        };

        var resolved = DurableLinks.Resolve(definition.Links, new ExtensionRegistry(), servers, definition.Permissions);

        Assert.Single(resolved);
        Assert.Empty(resolved[0].Grants);
        Assert.Equal(["read:/workspace"], definition.Permissions);
    }

    [Fact]
    public void ToChoices_FoldsLinkedExtensionAndToolNamesIn()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "linked" }],
        };

        var choices = definition.ToChoices(registry);

        Assert.Contains("linked", choices.Extensions!);
        Assert.Contains("linked_tool", choices.Tools!);
    }

    [Fact]
    public void ToChoices_WithoutLinks_IsUnchanged()
    {
        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Extensions = ["declared"],
            Tools = ["declared_tool"],
        };

        var choices = definition.ToChoices(new ExtensionRegistry());

        Assert.Equal(["declared"], choices.Extensions);
        Assert.Equal(["declared_tool"], choices.Tools);
    }

    [Fact]
    public void Validator_AcceptsLegacyDefinitionWithoutLinks()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition { Name = "reviewer" };

        AgentDefinitionValidator.Validate(definition, registry);

        Assert.Null(definition.Links);
    }

    [Fact]
    public void Validator_MissingLink_FailsClosed()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Links = [new DurableLink { Kind = DurableLinkKind.Mcp, Name = "absent" }],
        };

        Assert.Throws<DurableLinkedCapabilityException>(
            () => AgentDefinitionValidator.Validate(definition, registry));
    }

    [Fact]
    public void Export_ContainsNoSecrets()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Instructions = "Review the change and cite the file.",
            Permissions = ["read:/workspace"],
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "linked" }],
        };

        var exported = DurablePortableConfiguration.Export(definition, registry);

        var json = exported.GetRawText();
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_RefusesADefinitionCarryingSecretMaterial()
    {
        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Instructions = "Use token ghp_0123456789abcdefghijABCDEFGHIJ012345 to call the API.",
        };

        var error = Assert.Throws<DurableConfigurationException>(
            () => DurablePortableConfiguration.Export(definition));

        Assert.Contains("must not carry credentials", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RefusesASecretNamedProperty()
    {
        // Import is the only surface that accepts a document the type system did not build, so it
        // is the one place a secret-shaped *property name* can arrive. The public surface has no
        // credentials field, so this cannot be expressed through AgentDefinition.
        var document = JsonDocument.Parse(
            """
            {
              "schemaVersion": 1,
              "name": "reviewer",
              "clientSecret": "not-a-real-secret"
            }
            """);

        var error = Assert.Throws<DurableConfigurationException>(
            () => DurablePortableConfiguration.Import(document.RootElement, new ExtensionRegistry()));

        Assert.Contains("must not carry credentials", error.Message, StringComparison.Ordinal);
        Assert.Contains("clientSecret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_OmitsHostOnlyFields()
    {
        var definition = new AgentDefinition { Name = "reviewer" };

        var exported = DurablePortableConfiguration.Export(definition);
        var json = exported.GetRawText();

        Assert.DoesNotContain("hooks", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onPermissionRequest", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissionHandler", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_UnknownSchemaVersion_FailsClosed()
    {
        var registry = RegistryWithTool("linked");
        using var document = JsonDocument.Parse(
            """{"schemaVersion": 99, "name": "reviewer"}""");

        var error = Assert.Throws<DurableConfigurationException>(
            () => DurablePortableConfiguration.Import(document.RootElement, registry));

        Assert.Contains("99", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RoundTripsAnExportedDefinition()
    {
        var registry = RegistryWithTool("linked");

        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Description = "Reviews changes.",
            Permissions = ["read:/workspace"],
            Links = [new DurableLink { Kind = DurableLinkKind.Tool, Name = "linked" }],
        };

        var exported = DurablePortableConfiguration.Export(definition, registry);
        var imported = DurablePortableConfiguration.Import(exported, registry);

        Assert.Equal("reviewer", imported.Name);
        Assert.Equal("Reviews changes.", imported.Description);
        Assert.Equal(["read:/workspace"], imported.Permissions);
        Assert.NotNull(imported.Links);
        Assert.Equal("linked", imported.Links![0].Name);
    }

    [Fact]
    public void Import_RetrievedLinkCannotWidenAuthority()
    {
        var registry = RegistryWithTool("linked");
        using var document = JsonDocument.Parse(
            """
            {
              "schemaVersion": 1,
              "name": "reviewer",
              "permissions": ["read:/workspace"],
              "links": [
                { "kind": "tool", "name": "linked", "grants": ["write:/workspace"] }
              ]
            }
            """);

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurablePortableConfiguration.Import(document.RootElement, registry));

        Assert.Contains("cannot widen authority", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_MissingLinkedCapability_FailsClosed()
    {
        var registry = RegistryWithTool("linked");
        using var document = JsonDocument.Parse(
            """
            {
              "schemaVersion": 1,
              "name": "reviewer",
              "links": [ { "kind": "mcp", "name": "absent" } ]
            }
            """);

        var error = Assert.Throws<DurableLinkedCapabilityException>(
            () => DurablePortableConfiguration.Import(document.RootElement, registry));

        Assert.Contains("absent", error.Message, StringComparison.Ordinal);
    }
}
