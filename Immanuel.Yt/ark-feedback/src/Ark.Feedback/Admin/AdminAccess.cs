using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ark.Feedback.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Admin;

public sealed record AdminIdentity(string Method, string? Name);

/// <summary>
/// Who may open the admin console. In order: an Ark role from Feedback:Admin:Roles (with sign-in on), a key sign-in
/// cookie (HttpOnly, SameSite=Strict, time-limited, bound to the key's fingerprint so changing the key ends every
/// session), or — only in Development with no key configured — a request from this machine.
/// </summary>
public sealed class AdminAccess(IOptions<FeedbackOptions> opt, IDataProtectionProvider dp, IWebHostEnvironment env)
{
    public const string CookieName = "ark_feedback_admin";
    public const int MinKeyLength = 16;

    private AdminOptions O => opt.Value.Admin;

    private ITimeLimitedDataProtector Protector => dp.CreateProtector("Ark.Feedback.Admin.Session.v1").ToTimeLimitedDataProtector();

    public bool KeyEnabled => O.Key is { Length: >= MinKeyLength };

    public string? KeyProblem => O.Key is { Length: > 0 and < MinKeyLength }
        ? $"Feedback:Admin:Key is shorter than {MinKeyLength} characters, so key sign-in is off."
        : null;

    public IReadOnlyList<string> Roles => O.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();

    private string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ark-feedback-admin|" + O.Key)), 0, 8);

    public bool DevelopmentLoopback(HttpContext ctx) =>
        env.IsDevelopment() && O.AllowLoopbackInDevelopment && !KeyEnabled && ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public AdminIdentity? Check(HttpContext ctx, bool authEnabled)
    {
        var user = ctx.User;
        if (authEnabled && user.Identity?.IsAuthenticated == true && Roles.Any(user.IsInRole))
            return new AdminIdentity("role", user.FindFirst("name")?.Value ?? user.FindFirst("email")?.Value ?? user.Identity.Name);

        if (KeyEnabled && ctx.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            try
            {
                if (Protector.Unprotect(token) == "key|" + Fingerprint) return new AdminIdentity("key", null);
            }
            catch (CryptographicException)
            {
                // expired, tampered with, or from a key ring this server no longer has
            }
        }
        return DevelopmentLoopback(ctx) ? new AdminIdentity("development", null) : null;
    }

    public bool KeyMatches(string? candidate)
    {
        if (!KeyEnabled || string.IsNullOrEmpty(candidate) || candidate.Length > 512) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(O.Key!));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void IssueCookie(HttpContext ctx)
    {
        var life = TimeSpan.FromHours(Math.Clamp(O.SessionHours, 1, 24 * 30));
        ctx.Response.Cookies.Append(CookieName, Protector.Protect("key|" + Fingerprint, life), new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = life, IsEssential = true,
        });
    }

    public static void ClearCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps });
}
