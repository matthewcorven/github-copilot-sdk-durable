using AgentOrchestrator;
using AgentOrchestrator.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbPath = Path.Combine(AppContext.BaseDirectory, "properties.db");
builder.Services.AddDbContextFactory<PropertyDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<PropertyDatabase>();
builder.Services.AddSingleton<AppState>();

var app = builder.Build();
PropertyDatabase.EnsureSeeded(app.Services);

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
