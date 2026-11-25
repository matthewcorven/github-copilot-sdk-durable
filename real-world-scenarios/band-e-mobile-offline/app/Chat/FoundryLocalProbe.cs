using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BandEMobile.Chat;

/// <summary>
/// The only place in this app that names an Android type (decision record §3.3).
///
/// The decision record selects IPC mode, where the model runs inside Microsoft's companion
/// app <c>com.microsoft.foundrylocal.app</c> and the SDK AAR talks to it over AIDL. This type
/// answers the question that has to be answered before any transport can work: is that
/// companion app present, and can a bridge even be compiled on this machine?
///
/// It deliberately does <b>not</b> pretend to complete a turn. Calling
/// <c>FoundryLocalManager.create(context, ...)</c> needs the Kotlin facade from §3.1, and that
/// facade in turn needs a Kotlin toolchain. Reporting "no transport" accurately is the honest
/// outcome here; inventing a canned answer would be worse than a documented gap.
/// </summary>
public static class FoundryLocalProbe
{
    /// <summary>The companion app the IPC AAR queries for in its own manifest.</summary>
    public const string CompanionPackage = "com.microsoft.foundrylocal.app";

    public sealed record Availability(bool CompanionInstalled, string Detail)
    {
        public override string ToString() =>
            CompanionInstalled ? $"present — {Detail}" : $"absent — {Detail}";
    }

    /// <summary>
    /// Checks for the companion app without throwing. A missing package is a fact to report,
    /// not an exception to swallow.
    /// </summary>
    public static Availability Check(Context? context)
    {
        if (context is null)
        {
            return new Availability(false, "no Android Context available");
        }

        var sdk = (int)Build.VERSION.SdkInt;
        try
        {
            // QUERY_ALL_PACKAGES is not held and is not needed: the AAR's manifest already
            // declares a <queries><package> element for this id, which this app inherits only
            // if it also declares one. It does not, so an explicit query is used here.
            var info = context.PackageManager?.GetPackageInfo(CompanionPackage, PackageInfoFlags.MatchAll);
            if (info is null)
            {
                return new Availability(false, $"package {CompanionPackage} not visible (API {sdk})");
            }

            var version = info.VersionName ?? "unknown";
            return new Availability(true, $"version {version} (API {sdk})");
        }
        catch (PackageManager.NameNotFoundException)
        {
            return new Availability(false, $"{CompanionPackage} is not installed (API {sdk})");
        }
        catch (Exception ex)
        {
            return new Availability(false, $"package query failed: {ex.GetType().Name}");
        }
    }
}
