using Microsoft.Extensions.Logging;

namespace BandEMobile;

/// <summary>
/// The MAUI host. There is deliberately no DI container here.
/// </summary>
/// <remarks>
/// The app has one object graph, <see cref="AppHost"/>, and it owns two process-wide singletons — a
/// store holding an exclusive file lock and an embedded inference engine. Registering them as
/// transient or scoped services would invite a second instance, so <c>MainPage</c> asks
/// <see cref="AppHost.Create"/> for the one instance instead. This method therefore only wires
/// logging and fonts, which is all a demonstration needs from a host.
/// </remarks>
public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
