using System.Reflection;

namespace Cuebot.Portal.Web;

/// <summary>
/// The mod portal's web UI (portal/ui, built by <c>npm run build</c> in portal/ into this folder and embedded): Cuebot
/// sends it to a signed-in device, so the UI always matches this Cuebot's API (docs/MOD_PORTAL_PLAN.md, section 3).
/// </summary>
public static class PortalUi
{
    private static readonly Lazy<(string Js, string Css)> Bundle = new(() => (Read("portal-ui.js"), Read("portal-ui.css")));

    public static string Js => Bundle.Value.Js;
    public static string Css => Bundle.Value.Css;

    private static string Read(string name)
    {
        using var stream = typeof(PortalUi).Assembly.GetManifestResourceStream("Cuebot.Portal.Web." + name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
