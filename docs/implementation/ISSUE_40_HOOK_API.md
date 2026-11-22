# Issue #40 — the hook API surface, as discovered

The hook-composition fact needs the exact shape of the SDK's hook types. This
was established by reflecting over `GitHub.Copilot.SDK` 1.0.26 rather than
guessing, because the types are not documented in the repo.

The probe that produced this was a throwaway console project:

```csharp
using System.Reflection;
using GitHub.Copilot;
foreach (var t in new[] { typeof(HookInvocation), typeof(UserPromptSubmittedHookInput), typeof(UserPromptSubmittedHookOutput), typeof(SessionHooks) })
{
    Console.WriteLine($"== {t.FullName} ==");
    foreach (var c in t.GetConstructors()) Console.WriteLine("  ctor: " + c);
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name} set={p.CanWrite}");
}
```

with `probe.csproj` targeting `net10.0` and referencing
`GitHub.Copilot.SDK` version `1.0.26`.

To re-run it, recreate that project anywhere outside the repo (it pulls a large
package graph — do not commit it) and run `dotnet run`.

The types to reflect over are `HookInvocation`, `UserPromptSubmittedHookInput`,
`UserPromptSubmittedHookOutput`, and `SessionHooks`. The composition wrapper
under test is at `lib/Durable.cs:5459-5483`.
