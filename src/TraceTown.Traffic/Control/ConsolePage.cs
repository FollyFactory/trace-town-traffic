using System.Reflection;

namespace TraceTown.Traffic.Control;

/// <summary>
/// The control console, served at <c>/</c>.
/// </summary>
/// <remarks>
/// One embedded HTML file with no build step, no package manager and no CDN.
/// A generator that needed npm before it could show you a button would be a
/// worse tool, and a console that fetched a script from the internet would stop
/// working exactly where this is most useful — on an air-gapped box, or in CI.
/// </remarks>
internal static class ConsolePage
{
    private static readonly Lazy<string> Html = new(Read, isThreadSafe: true);

    internal static string Page => Html.Value;

    private static string Read()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("console.html");
        if (stream is null)
        {
            return "<!doctype html><title>trace-town-traffic</title>" +
                   "<p>The console was not embedded in this build. The API is still at <code>/api/status</code>.";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
