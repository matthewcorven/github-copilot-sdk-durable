using AgentOrchestrator.Durable;
using AgentOrchestrator.Durable.Components;
using Durable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The listings database is the baseline's, unchanged: property data was never agent state.
var dbPath = Path.Combine(AppContext.BaseDirectory, "properties.db");
builder.Services.AddDbContextFactory<PropertyDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<PropertyDatabase>();

var database = new PropertyDatabase(
    new PooledDbContextFactory<PropertyDbContext>(
        new DbContextOptionsBuilder<PropertyDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options));

// DURABLE_RUNTIME_PATH selects a recorded runtime for the deterministic harness; the durable
// layer reads it itself, so nothing here names a runtime. BAND_A_FIXTURE names the recording that
// runtime should serve, because the replay runtime's own default is a generic transcript.
static DurableHarnessOptions HarnessOptions()
{
    var options = new DurableHarnessOptions();
    if (Environment.GetEnvironmentVariable("BAND_A_FIXTURE") is { Length: > 0 } fixture)
    {
        options.RuntimeArguments = ["--fixture", fixture];
    }

    return options;
}

// The durable store is the only place an enquiry's state exists. It sits beside the assembly so
// run.sh can put it in a scratch directory, and it survives a restart because it is a file.
var storePath = Environment.GetEnvironmentVariable("BAND_A_STORE")
    ?? Path.Combine(AppContext.BaseDirectory, "band-a-store.jsonl");

builder.Services.AddSingleton(_ => EnquiryStore.OpenAsync(
        storePath,
        database,
        HarnessOptions())
    .GetAwaiter().GetResult());

builder.Services.AddSingleton(services => new AppState(services.GetRequiredService<EnquiryStore>()));

var app = builder.Build();
PropertyDatabase.EnsureSeeded(app.Services);

// The headless driver: the same durable code path as the page, driven from a shell so restart and
// concurrency evidence can be captured without a browser.
if (Headless.TryRun(args, storePath, database))
{
    return;
}

// Resume before the server accepts a request, so the dashboard a browser opens is already the
// dashboard the previous process left behind.
app.Services.GetRequiredService<AppState>().ResumeUnfinished();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
