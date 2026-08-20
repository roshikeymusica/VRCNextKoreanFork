namespace VRCNext.Services.Helpers;

public static class ConsoleHelper
{
    public record Result(string Text, string Color, string? Extra = null, object? ExtraPayload = null);

    public static Result Execute(string input)
    {
        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new("", "info");

        switch (parts[0].ToLowerInvariant())
        {
            case "/help":
                return new(HelpText, "info");

            case "/memc":
                // Handled by AppShell.MemcCommand, which owns the MemoryManager instance.
                return new("", "info", "memc", new { args = parts });

            case "/trim":
                return new("Triggering memory trim...", "sec", "forceTrim", new { });

            case "/force":
                if (parts.Length >= 2 && parts[1].Equals("trim", StringComparison.OrdinalIgnoreCase))
                    return new("Triggering force trim...", "sec", "forceTrimAll", new { });
                return new("Usage: /force trim", "err");

            case "/fix":
                if (parts.Length >= 2 &&
                    (parts[1].Equals("nps", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("npsm", StringComparison.OrdinalIgnoreCase)))
                    return new("Running NPSMSvc fix — see Activity Log for results.", "sec", "fixNps", new { });
                return new("Usage: /fix nps", "err");

            case "/msg":
                if (parts.Length >= 2)
                {
                    var sub = parts[1].ToLowerInvariant();
                    if (sub == "invite")
                        return new("Fetching invite messages...", "info", "vrcMsgList", new { msgType = "message" });
                    if (sub == "request")
                        return new("Fetching invite request responses...", "info", "vrcMsgList", new { msgType = "requestResponse" });
                }
                return new("Usage: /msg invite | /msg request", "err");

            case "/vrcn-get":
            case "/vrcn-set":
            case "/vrcn-del":
                if (parts.Length < 2)
                    return new($"Usage: {parts[0]} <usr_xxxxxxxx-...>", "err");
                return new("", "info", "vrcnPlusAdmin", new {
                    sub      = parts[0].Substring(6),
                    targetId = parts[1],
                });

            case "/debug":
                if (parts.Length >= 4
                    && parts[1].Equals("img",   StringComparison.OrdinalIgnoreCase)
                    && parts[2].Equals("cache", StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(parts[3], out var val))
                {
                    ImageCacheHelper.DebugMode = val;
                    return new(
                        $"Image cache debug: {(val ? "ON" : "OFF")}",
                        val ? "ok" : "warn",
                        "debugImgCacheState",
                        new { enabled = val }
                    );
                }
                if (parts.Length >= 3
                    && parts[1].Equals("avatar-lookup", StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(parts[2], out var alv))
                {
                    return new(
                        $"Avatar lookup debug: {(alv ? "ON" : "OFF")} — right-click a user to test avtrdb / ICU / VRCNDb individually.",
                        alv ? "ok" : "warn",
                        "debugAvatarLookupState",
                        new { enabled = alv }
                    );
                }
                return new("Usage: /debug img cache <true/false>  |  /debug avatar-lookup <true/false>", "err");

            default:
                return new($"Unknown command: '{parts[0]}'. Type /help for available commands.", "err");
        }
    }

    private const string HelpText =
        "Commands:\n" +
        "\n" +
        "Help:\n" +
        "  /help\n" +
        "  Shows all commands\n" +
        "\n" +
        "Memory:\n" +
        "  /trim\n" +
        "  Forces a GC memory trim and clears in-memory caches.\n" +
        "\n" +
        "  /force trim\n" +
        "  Same as /trim but skips all thresholds — always trims all caches.\n" +
        "\n" +
        "Windows Fixes:\n" +
        "  /fix nps\n" +
        "  Force-restarts the hung Windows 'Now Playing' service (NPSMSvc) and reports the result.\n" +
        "\n" +
        "Invite Messages:\n" +
        "  /msg invite\n" +
        "  Lists all invite message slots (the messages you send with invites).\n" +
        "\n" +
        "  /msg request\n" +
        "  Lists all invite-request response slots (the replies you send when someone requests an invite).\n" +
        "\n" +
        "Debugging:\n" +
        "  /debug img cache <true/false>\n" +
        "  Shows an overlay on all images.\n" +
        "  Green = cached (disk). Red = not cached (CDN/API). Orange = WebView cache.\n" +
        "\n" +
        "  /debug avatar-lookup <true/false>\n" +
        "  Adds a submenu to the right-click 'Check for Avatar' so you can resolve a\n" +
        "  file id against avtrdb, ICU or VRCNDb individually (results in the Activity Log).";
}
