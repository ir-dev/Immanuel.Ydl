using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Ark.Feedback.Audit;

/// <summary>Everything the audit log records about who made a request. Shared by the request, analysis and comment rows.</summary>
public sealed record ClientInfo
{
    public string? Ip { get; init; }
    public string? Country { get; init; }
    public string? CountryName { get; init; }
    public string? Region { get; init; }
    public string? City { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string ClientKey { get; init; } = "";
    public string? UserAgent { get; init; }
    public UserAgentInfo Agent { get; init; } = UserAgentInfo.Unknown;
    public string? Language { get; init; }
    public string? AcceptLanguage { get; init; }
    public string? Referrer { get; init; }
    public string? TimeZone { get; init; }
    public string? Screen { get; init; }
    public bool? Pwa { get; init; }
    public string? ClientVersion { get; init; }

    public static readonly ClientInfo System = new() { ClientKey = "system", Agent = UserAgentInfo.Unknown };

    public void WriteTo(IDictionary<string, object?> row)
    {
        row["ip"] = Ip;
        row["ip_country"] = Country;
        row["ip_country_name"] = CountryName;
        row["ip_region"] = Region;
        row["ip_city"] = City;
        row["user_id"] = UserId;
        row["user_name"] = UserName;
        row["client_key"] = ClientKey;
        row["user_agent"] = UserAgent;
        row["browser"] = Agent.Browser;
        row["browser_version"] = Agent.BrowserVersion;
        row["os"] = Agent.Os;
        row["os_version"] = Agent.OsVersion;
        row["device"] = Agent.Device;
        row["is_bot"] = Agent.IsBot ? 1 : 0;
        row["language"] = Language;
        row["accept_language"] = AcceptLanguage;
        row["referrer"] = Referrer;
        row["client_tz"] = TimeZone;
        row["client_screen"] = Screen;
        row["client_pwa"] = Pwa is null ? null : Pwa.Value ? 1 : 0;
        row["client_version"] = ClientVersion;
    }
}

public sealed record UserAgentInfo(string? Browser, string? BrowserVersion, string? Os, string? OsVersion, string Device, bool IsBot)
{
    public static readonly UserAgentInfo Unknown = new(null, null, null, null, "other", false);
}

/// <summary>A small user-agent reader: browser + major version, OS, device class and bots. Client Hints win where present.</summary>
public static partial class UserAgentParser
{
    [GeneratedRegex(@"bot\b|bot/|crawl|spider|slurp|facebookexternalhit|embedly|preview|whatsapp|telegram|discord|curl/|wget/|python-|httpclient|okhttp|go-http-client|java/|libwww|axios|node-fetch|undici|postman|insomnia|headlesschrome|lighthouse|pingdom|uptime|monitor|scanner|zgrab|masscan|nmap|feedfetcher|^mozilla/5\.0$", RegexOptions.IgnoreCase)]
    private static partial Regex BotRx();

    [GeneratedRegex(@"""([^""]+)"";\s*v=""(\d+)""")]
    private static partial Regex BrandRx();

    private static readonly (Regex Rx, string Name)[] Browsers =
    [
        (new Regex(@"Edg(?:e|A|iOS)?/(\d+)", RegexOptions.Compiled), "Edge"),
        (new Regex(@"(?:OPR|OPT|Opera)/(\d+)", RegexOptions.Compiled), "Opera"),
        (new Regex(@"SamsungBrowser/(\d+)", RegexOptions.Compiled), "Samsung Internet"),
        (new Regex(@"YaBrowser/(\d+)", RegexOptions.Compiled), "Yandex"),
        (new Regex(@"UCBrowser/(\d+)", RegexOptions.Compiled), "UC Browser"),
        (new Regex(@"Vivaldi/(\d+)", RegexOptions.Compiled), "Vivaldi"),
        (new Regex(@"DuckDuckGo/(\d+)", RegexOptions.Compiled), "DuckDuckGo"),
        (new Regex(@"(?:Firefox|FxiOS)/(\d+)", RegexOptions.Compiled), "Firefox"),
        (new Regex(@"(?:Chrome|CriOS)/(\d+)", RegexOptions.Compiled), "Chrome"),
        (new Regex(@"Version/(\d+)[\d.]* (?:Mobile/\S+ )?Safari/", RegexOptions.Compiled), "Safari"),
        (new Regex(@"MSIE (\d+)|Trident/.*rv:(\d+)", RegexOptions.Compiled), "Internet Explorer"),
    ];

    public static UserAgentInfo Parse(string? ua, string? chUa = null, string? chMobile = null, string? chPlatform = null)
    {
        ua = ua?.Trim();
        if (string.IsNullOrEmpty(ua)) return UserAgentInfo.Unknown;

        var isBot = BotRx().IsMatch(ua);
        string? browser = null, version = null;
        foreach (var (rx, name) in Browsers)
        {
            var m = rx.Match(ua);
            if (!m.Success) continue;
            browser = name;
            version = m.Groups[1].Success ? m.Groups[1].Value : m.Groups.Count > 2 ? m.Groups[2].Value : null;
            break;
        }
        if (!string.IsNullOrEmpty(chUa))
        {
            foreach (Match m in BrandRx().Matches(chUa))
            {
                var brand = m.Groups[1].Value;
                if (brand.Contains("Not", StringComparison.OrdinalIgnoreCase) || brand == "Chromium") continue;
                browser = brand switch
                {
                    "Google Chrome" => "Chrome",
                    "Microsoft Edge" => "Edge",
                    "Opera" or "Opera GX" => "Opera",
                    "Brave" => "Brave",
                    _ => brand.Length <= 40 ? brand : browser,
                };
                version = m.Groups[2].Value;
                break;
            }
        }
        if (browser is null && isBot)
        {
            var token = ua.Split(' ', '/', ';', '(', ')', '+').FirstOrDefault(t => t.Length > 1 && char.IsLetter(t[0])
                && !t.Equals("Mozilla", StringComparison.OrdinalIgnoreCase) && !t.Equals("compatible", StringComparison.OrdinalIgnoreCase));
            browser = token is { Length: <= 40 } ? token : "Bot";
        }

        var (os, osVersion) = Os(ua);
        var platform = chPlatform?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(platform) && platform.Length <= 30)
        {
            var hinted = platform switch { "Chrome OS" or "Chromium OS" => "ChromeOS", "macOS" => "macOS", _ => platform };
            if (hinted != os) osVersion = null;
            os = hinted;
        }

        string device;
        if (isBot) device = "bot";
        else if (Regex.IsMatch(ua, @"SmartTV|SMART-TV|Tizen|Web0S|webOS.TV|AppleTV|CrKey|\bAFT[A-Z]|BRAVIA|HbbTV", RegexOptions.IgnoreCase)) device = "tv";
        else if (ua.Contains("iPad", StringComparison.Ordinal) || Regex.IsMatch(ua, @"Tablet|Android(?!.*Mobile)", RegexOptions.IgnoreCase)) device = "tablet";
        else if (chMobile == "?1" || Regex.IsMatch(ua, @"Mobi|iPhone|iPod|Windows Phone|Android.*Mobile", RegexOptions.IgnoreCase)) device = "mobile";
        else if (os is not null) device = "desktop";
        else device = "other";

        return new UserAgentInfo(browser, version, os, osVersion, device, isBot);
    }

    private static (string? Os, string? Version) Os(string ua)
    {
        Match m;
        if ((m = Regex.Match(ua, @"Windows NT (\d+\.\d+)")).Success)
            return ("Windows", m.Groups[1].Value switch { "10.0" => "10/11", "6.3" => "8.1", "6.2" => "8", "6.1" => "7", var v => v });
        if (ua.Contains("Windows Phone", StringComparison.Ordinal)) return ("Windows Phone", null);
        if ((m = Regex.Match(ua, @"iPad.*?OS (\d+)")).Success) return ("iPadOS", m.Groups[1].Value);
        if ((m = Regex.Match(ua, @"(?:iPhone|iPod).*?OS (\d+)")).Success) return ("iOS", m.Groups[1].Value);
        if ((m = Regex.Match(ua, @"Android (\d+)")).Success) return ("Android", m.Groups[1].Value);
        if (ua.Contains("Android", StringComparison.Ordinal)) return ("Android", null);
        if (ua.Contains("CrOS", StringComparison.Ordinal)) return ("ChromeOS", null);
        if ((m = Regex.Match(ua, @"Mac OS X (\d+)[_.](\d+)")).Success) return ("macOS", $"{m.Groups[1].Value}.{m.Groups[2].Value}");
        if (ua.Contains("Macintosh", StringComparison.Ordinal)) return ("macOS", null);
        if (ua.Contains("Linux", StringComparison.Ordinal) || ua.Contains("X11", StringComparison.Ordinal)) return ("Linux", null);
        return (null, null);
    }
}

public static class IpTools
{
    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public static string Mask(IPAddress ip)
    {
        ip = Normalize(ip);
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) b[3] = 0;
        else for (var i = 6; i < b.Length; i++) b[i] = 0;
        return new IPAddress(b).ToString();
    }

    public static string Hash(IPAddress ip, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + Normalize(ip)));
        return "h:" + Convert.ToHexStringLower(bytes, 0, 8);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        ip = Normalize(ip);
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0;
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6None);
    }
}

/// <summary>Reads what the page sends about itself in X-Ark-Client (a query string), keeping only well-formed values.</summary>
public static partial class ClientHeader
{
    public const string Name = "X-Ark-Client";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-/]{0,63}$")]
    private static partial Regex TzRx();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,3}){1,2}(?:-[A-Za-z0-9.]{1,12})?$")]
    private static partial Regex VersionRx();

    public static (string? Tz, string? Screen, bool? Pwa, string? Version) Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 400) return (null, null, null, null);
        var q = QueryHelpers.ParseQuery(header.StartsWith('?') ? header : "?" + header);
        string? Get(string k) => q.TryGetValue(k, out var v) ? v.ToString() : null;

        var tz = Get("tz");
        if (tz is not null && !TzRx().IsMatch(tz)) tz = null;
        string? screen = null;
        if (int.TryParse(Get("sw"), NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w is > 0 and < 20000
            && int.TryParse(Get("sh"), NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is > 0 and < 20000)
        {
            screen = $"{w}x{h}";
            if (double.TryParse(Get("dpr"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpr) && dpr is >= 0.5 and <= 8)
                screen += "@" + Math.Round(dpr, 2).ToString(CultureInfo.InvariantCulture);
        }
        bool? pwa = Get("pwa") switch { "1" => true, "0" => false, _ => null };
        var version = Get("v");
        if (version is not null && !VersionRx().IsMatch(version)) version = null;
        return (tz, screen, pwa, version);
    }
}

public static class ClientIdentity
{
    public static (string? Id, string? Name) User(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return (null, null);
        var id = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name;
        var name = user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("name")?.Value ?? user.Identity.Name;
        return (Clip(id, 200), Clip(name, 200));
    }

    public static string? Referrer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !Uri.TryCreate(header, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        return Clip(u.GetLeftPart(UriPartial.Path), 300);
    }

    public static string? PrimaryLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return null;
        var first = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        return first.Length is > 0 and <= 35 && first != "*" ? first : null;
    }

    /// <summary>The key rate limits and "one analysis at a time" use: the signed-in user, else the IP.</summary>
    public static string ClientKey(HttpContext ctx)
    {
        var (id, _) = User(ctx.User);
        if (id is not null) return "user:" + id;
        return "ip:" + (ctx.Connection.RemoteIpAddress is { } ip ? IpTools.Normalize(ip).ToString() : "unknown");
    }

    public static string? Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Replace("\0", "");
        return s.Length > max ? s[..max] : s;
    }
}
