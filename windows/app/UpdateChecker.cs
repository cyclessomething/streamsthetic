using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PhoneCam;

/// <summary>
/// "Check for updates": asks a release feed (GitHub's "latest release" JSON: tag_name, html_url) whether a newer version
/// exists and says so. It never downloads or installs anything by itself, and sends nothing about the user (a plain GET).
/// The feed address comes from the file update-feed.txt next to the program or the environment variable
/// STREAMSTHETIC_UPDATE_FEED, e.g. https://api.github.com/repos/OWNER/REPO/releases/latest. Without one, updates are "not set up".
/// </summary>
public static class UpdateChecker
{
    public sealed record Result(bool Configured, bool NewerAvailable, string Message, string? Url);

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>
    /// The official releases. A list, not ".../releases/latest": GitHub's "latest" skips pre-releases, and the betas are
    /// pre-releases (it answered 404). A fork points update-feed.txt or STREAMSTHETIC_UPDATE_FEED at its own repository.
    /// </summary>
    public const string DefaultFeed = "https://api.github.com/repos/cyclessomething/streamsthetic/releases?per_page=10";

    private static string? FeedUrl()
    {
        var env = Environment.GetEnvironmentVariable("STREAMSTHETIC_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var file = Path.Combine(AppContext.BaseDirectory, "update-feed.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : DefaultFeed;
    }

    /// <summary>The newest published release: the object itself ("latest" feed) or the first non-draft in a list.</summary>
    public static JsonElement? NewestRelease(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object) return root;
        if (root.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in root.EnumerateArray())
            if (!(release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)) return release;
        return null;
    }

    /// <summary>Compares dotted versions ("v1.2.3" or "1.2"): true when <paramref name="latest"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current)
    {
        static Version Parse(string text)
        {
            var clean = new string(text.TrimStart('v', 'V').TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
            return Version.TryParse(clean.Contains('.') ? clean : clean + ".0", out var v) ? v : new Version(0, 0);
        }
        return Parse(latest) > Parse(current);
    }

    public static async Task<Result> CheckAsync()
    {
        var feed = FeedUrl();
        if (string.IsNullOrEmpty(feed)) return new(false, false, "Update checks are not configured for this installation.", null);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Streamsthetic-update-check");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(feed));
            if (NewestRelease(doc.RootElement) is not JsonElement release) return new(true, false, "No releases have been published yet.", null);
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var url = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return IsNewer(tag, CurrentVersion)
                ? new(true, true, $"Version {tag.TrimStart('v')} is available (you have {CurrentVersion}).", url)
                : new(true, false, $"You have the latest version ({CurrentVersion}).", null);
        }
        catch (Exception)
        {
            return new(true, false, "Could not check for updates. Try again later and check your internet connection.", null);
        }
    }
}
