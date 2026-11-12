using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneCam;

public sealed record GitHubIssueDraft(Uri Url, bool DescriptionShortened, string FullBody)
{
    public const string IssuePage = "https://github.com/cyclessomething/streamsthetic/issues/new";
    public const int MaxUrlLength = 6000;

    // Typed, explicitly selected context only. Never pass logs, settings, problem summaries or dumps here.
    public static GitHubIssueDraft Create(string description, Version appVersion, Version windowsVersion,
        Architecture architecture, DateTime from, DateTime to, string? problemKind)
    {
        var kind = problemKind switch
        {
            "app-crash" => "App crash",
            "receiver-crash" => "Receiver crash",
            "receiver-exit" => "Receiver stopped",
            _ => "Other / not specified"
        };
        var arch = architecture switch { Architecture.X64 => "x64", Architecture.X86 => "x86", Architecture.Arm64 => "ARM64", Architecture.Arm => "ARM", _ => "Other" };
        var context = $"\n\n## App and system\n- Streamsthetic: {appVersion}\n- Windows version: {windowsVersion}\n- OS architecture: {arch}\n- Problem type: {kind}\n- Time range (UTC): {from.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} to {to.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\n\nNo logs, settings, device identifiers or crash dumps are included automatically. Review any diagnostic ZIP before attaching it manually.\n";
        var title = $"Problem report — Streamsthetic {appVersion}";
        string Body(string text) => "## What happened?\n" + (text.Length == 0 ? "Describe what happened and how to reproduce it." : text) + context;
        string Url(string body) => IssuePage + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
        var fullBody = Body(description);
        var fullUrl = Url(fullBody);
        if (fullUrl.Length <= MaxUrlLength) return new(new Uri(fullUrl), false, fullBody);

        // Build a bounded prefix by Unicode scalar, never splitting an emoji/surrogate pair.
        const string notice = "\n\n[Description shortened for the browser link. Paste the rest from the full report draft saved on your PC.]";
        var prefix = new StringBuilder();
        var used = Url(Body(notice)).Length;
        foreach (var rune in description.EnumerateRunes())
        {
            var text = rune.ToString();
            var length = Uri.EscapeDataString(text).Length;
            if (used + length > MaxUrlLength) break;
            prefix.Append(text); used += length;
        }
        return new(new Uri(Url(Body(prefix + notice))), true, fullBody);
    }
}
