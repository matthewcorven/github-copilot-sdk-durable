// Dependency-injection registration for the durable client.
//
// This lives in its own file, in its own namespace, because a file-scoped namespace
// cannot share a file with a nested one: Durable.cs is `namespace Durable;` throughout.

using GitHub.Copilot;
using Microsoft.Extensions.DependencyInjection;

namespace Durable.DependencyInjection;

/// <summary>
/// Dependency-injection registration for the durable client.
/// </summary>
/// <remarks>
/// The registration is a thin adapter over <see cref="DurableClientBuilder"/>: it uses the
/// same builder and the same runtime, not a second implementation. A host that registers the
/// client here gets exactly the object a manual build would produce, with the same ownership
/// rules, so mixing the two styles in one application is safe as long as the store is shared
/// deliberately rather than opened twice.
/// <para>
/// The conventions are the standard .NET ones. The client is registered as a singleton: it
/// holds a store and an SDK client, which are process-scoped resources. The registration
/// configures <see cref="DurableClientOptions"/> through the usual options delegate, does no
/// I/O while the container is built — <see cref="DurableClientBuilder.Build"/> only validates
/// and constructs — and leaves disposal to the container. A store or client the host
/// registered itself is resolved from the container rather than created here, so a borrowed
/// resource stays borrowed.
/// </para>
/// </remarks>
public static class DurableServiceCollectionExtensions
{
    /// <summary>Registers a durable Copilot client with default options.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, so registrations can be chained.</returns>
    /// <remarks>
    /// The registration requires a <see cref="DurableStore"/> and, optionally, an
    /// <see cref="ExtensionRegistry"/> and a <see cref="CopilotClient"/> to be resolvable
    /// from the container: what the host registered is borrowed, what is absent is created
    /// and owned by the durable layer.
    /// </remarks>
    public static IServiceCollection AddDurableCopilotClient(this IServiceCollection services) =>
        throw NotYet("#7.5");

    /// <summary>Registers a durable Copilot client with the options the delegate sets.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options the client is built from.</param>
    /// <returns>The same collection, so registrations can be chained.</returns>
    /// <remarks>
    /// The delegate runs when the client is resolved, not when the container is built, and
    /// the container performs no I/O to produce it. Everything the delegate leaves unset
    /// falls back to what is resolvable from the container — a registered
    /// <see cref="DurableStore"/>, <see cref="ExtensionRegistry"/>, or
    /// <see cref="CopilotClient"/> is borrowed — and to the durable layer's own defaults.
    /// </remarks>
    public static IServiceCollection AddDurableCopilotClient(
        this IServiceCollection services,
        Action<DurableClientOptions> configure) =>
        throw NotYet("#7.5");

    private static NotImplementedException NotYet(string issue) =>
        new($"The durable client registration is declared in #7.1 and implemented in {issue}.");
}
