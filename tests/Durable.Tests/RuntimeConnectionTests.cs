using System.Runtime.Versioning;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// Selecting the runtime the harness connects to: the explicit option, the environment
/// variable that sets the same thing, and the default that keeps the bundled runtime.
/// </summary>
/// <remarks>
/// The tests assert the resolution step, which is what the option and the environment
/// variable change. Opening a harness against a named executable starts that executable, so
/// the tests that do not need a live runtime stop at resolution. Environment variables are
/// process-wide, so these tests are in their own collection and never run in parallel with
/// each other.
/// </remarks>
[Collection(nameof(RuntimeConnectionTests))]
[CollectionDefinition(nameof(RuntimeConnectionTests), DisableParallelization = true)]
public class RuntimeConnectionTests
{
    [Fact]
    public void NothingSet_ResolvesToTheBundledRuntime()
    {
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, null);

        Assert.Null(DurableHarness.ResolveRuntimePath(new DurableHarnessOptions()));
        Assert.Null(new DurableHarnessOptions().RuntimePath);
    }

    [Fact]
    public void EmptyEnvironmentVariable_ResolvesToTheBundledRuntime()
    {
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, "   ");

        Assert.Null(DurableHarness.ResolveRuntimePath(new DurableHarnessOptions()));
    }

    [Fact]
    public void EnvironmentVariable_SelectsTheNamedExecutable()
    {
        var executable = ExistingExecutable();
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, executable);

        Assert.Equal(executable, DurableHarness.ResolveRuntimePath(new DurableHarnessOptions()));
    }

    [Fact]
    public void Option_SelectsTheNamedExecutable()
    {
        var executable = ExistingExecutable();
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, null);

        var options = new DurableHarnessOptions { RuntimePath = executable };

        Assert.Equal(executable, DurableHarness.ResolveRuntimePath(options));
    }

    [Fact]
    public void Option_WinsOverTheEnvironmentVariable()
    {
        var executable = ExistingExecutable();
        using var env = new EnvironmentScope(
            DurableHarnessOptions.RuntimePathEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), $"durable-missing-{Guid.NewGuid():N}"));

        var options = new DurableHarnessOptions { RuntimePath = executable };

        Assert.Equal(executable, DurableHarness.ResolveRuntimePath(options));
    }

    [Fact]
    public void MissingPath_NamesThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"durable-missing-{Guid.NewGuid():N}");
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, null);

        var error = Assert.Throws<FileNotFoundException>(
            () => DurableHarness.ResolveRuntimePath(new DurableHarnessOptions { RuntimePath = missing }));

        Assert.Contains(missing, error.Message);
        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public void MissingEnvironmentVariablePath_NamesThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"durable-missing-{Guid.NewGuid():N}");
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, missing);

        var error = Assert.Throws<FileNotFoundException>(
            () => DurableHarness.ResolveRuntimePath(new DurableHarnessOptions()));

        Assert.Contains(missing, error.Message);
        Assert.Contains(DurableHarnessOptions.RuntimePathEnvironmentVariable, error.Message);
    }

    [Fact]
    public async Task MissingPath_FailsTheOpenBeforeStartingAnything()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"durable-missing-{Guid.NewGuid():N}");
        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, null);
        using var store = DurableStore.CreateEphemeral();

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => DurableHarness.OpenAsync(
                store,
                new ExtensionRegistry(),
                new DurableHarnessOptions { RuntimePath = missing }));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task Option_OpensAgainstTheNamedExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, null);
        var directory = Directory.CreateTempSubdirectory("durable-runtime-");
        try
        {
            // The SDK spawns whatever path it is given. A script that records that it ran and
            // then exits is enough to prove which executable the client connected to: the
            // open fails on the lost JSON-RPC connection, after the spawn.
            var marker = Path.Combine(directory.FullName, "spawned.txt");
            var executable = WriteScript(
                directory.FullName,
                "runtime",
                $"#!/bin/sh\nprintf '%s' \"$0\" > '{marker}'\nexit 1\n");

            using var store = DurableStore.CreateEphemeral();

            await Assert.ThrowsAnyAsync<Exception>(
                () => DurableHarness.OpenAsync(
                    store,
                    new ExtensionRegistry(),
                    new DurableHarnessOptions { RuntimePath = executable }));

            Assert.True(File.Exists(marker), "The named runtime executable was never started.");
            Assert.Equal(executable, File.ReadAllText(marker));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnvironmentVariable_OpensAgainstTheNamedExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("durable-runtime-");
        try
        {
            var marker = Path.Combine(directory.FullName, "spawned.txt");
            var executable = WriteScript(
                directory.FullName,
                "runtime",
                $"#!/bin/sh\nprintf '%s' \"$0\" > '{marker}'\nexit 1\n");
            using var env = new EnvironmentScope(DurableHarnessOptions.RuntimePathEnvironmentVariable, executable);
            using var store = DurableStore.CreateEphemeral();

            await Assert.ThrowsAnyAsync<Exception>(
                () => DurableHarness.OpenAsync(store, new ExtensionRegistry()));

            Assert.True(File.Exists(marker), "The named runtime executable was never started.");
            Assert.Equal(executable, File.ReadAllText(marker));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Option_IsSourceCompatibleWithTheExistingSurface()
    {
        var options = new DurableHarnessOptions
        {
            TolerateHostHookFailures = true,
            EnableFileHooks = false,
        };

        Assert.Null(options.RuntimePath);
        Assert.Null(options.PermissionHandler);
        Assert.True(options.TolerateHostHookFailures);
        Assert.False(options.EnableFileHooks);
    }

    /// <summary>
    /// A path that exists on every supported platform without writing a file: the executable
    /// running the tests.
    /// </summary>
    private static string ExistingExecutable()
    {
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrEmpty(processPath));
        Assert.True(File.Exists(processPath));
        return processPath!;
    }

    /// <summary>Writes an executable script and returns its path.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string WriteScript(string directory, string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Sets an environment variable for the duration of a test and restores it.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
