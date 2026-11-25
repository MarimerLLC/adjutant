using System.Net;
using System.Text.RegularExpressions;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.HttpServer.Security;
using Adjutant.Tests.Identity;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// Requests that arrive over Tailscale Funnel (from the public internet) must never be let in with
/// the break-glass admin token, in any form: the login form, an existing token session, or the
/// REST API. Everything else behaves as it does inside the tailnet.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class AdminFunnelIntegrationTests
{
    private const string AdminToken = "test-admin-token";
    private const string TailnetOnly = "Admin-token sign-in is available only from inside the tailnet.";

    private AdjutantApp _app = null!;

    // A provider is configured but no sign-in has been recorded yet, so token login is still on:
    // the state the live server is in before it is claimed.
    [TestInitialize]
    public void Start() => _app = new AdjutantApp(new
    {
        Adjutant = new { Accounts = Array.Empty<object>(), AdminToken },
        AdminAuth = new
        {
            AllowedEmails = new[] { AdjutantApp.Owner },
            Providers = new
            {
                google = new { Authority = "https://accounts.google.com", ClientId = "test-client", ClientSecret = "test-secret" }
            }
        }
    });

    [TestCleanup]
    public void Stop() => _app.Dispose();

    private static HttpRequestMessage Get(string path, bool viaFunnel)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (viaFunnel)
            request.Headers.Add(FunnelRequest.HeaderName, "?1");
        return request;
    }

    [TestMethod]
    public async Task TheLoginPageHidesTheTokenFieldOverFunnel()
    {
        using var client = _app.CreateClient();

        var tailnet = await (await client.SendAsync(Get(AdminSignInProcessor.LoginPath, viaFunnel: false))).Content.ReadAsStringAsync();
        StringAssert.Contains(tailnet, "id=\"token\"");
        Assert.IsFalse(tailnet.Contains(TailnetOnly));

        var funnel = await (await client.SendAsync(Get(AdminSignInProcessor.LoginPath, viaFunnel: true))).Content.ReadAsStringAsync();
        StringAssert.Contains(funnel, "Sign in with Google");
        Assert.IsFalse(funnel.Contains("id=\"token\""), "the token field must not be offered over Funnel");
        StringAssert.Contains(funnel, TailnetOnly);
    }

    [TestMethod]
    public async Task ATokenLoginPostedOverFunnelIsRefused()
    {
        var browser = _app.CreateBrowser();
        var fields = await LoginFormFieldsAsync(browser.Http);

        // Over Funnel the form is not rendered, so Blazor rejects the post (400) before the handler.
        // This is the framework's form-name check, not the Funnel guard in HandleLoginAsync. That
        // guard can't be reached while the form's visibility and the handler see the same request,
        // so it is defense in depth and this test does not cover it. If the form is ever rendered
        // over Funnel, the guard becomes the only line and needs its own test. Either way: no
        // redirect, no session.
        using var refused = await PostLoginAsync(browser.Http, fields, viaFunnel: true);
        Assert.AreNotEqual(HttpStatusCode.Redirect, refused.StatusCode, await refused.Content.ReadAsStringAsync());
        Assert.IsFalse(SetsAdminCookie(refused), "no session may be issued over Funnel");

        // The same post from inside the tailnet signs in, so the refusal above is the header's doing.
        using var accepted = await PostLoginAsync(browser.Http, fields, viaFunnel: false);
        Assert.AreEqual(HttpStatusCode.Redirect, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.IsTrue(SetsAdminCookie(accepted));
    }

    [TestMethod]
    public async Task ATokenSessionCannotBeUsedOverFunnel()
    {
        var browser = _app.CreateBrowser();
        await browser.Http.GetAsync($"{AdjutantApp.TestSignInPath}?provider={AdminSessionPolicy.TokenProvider}");

        using var tailnet = await browser.Http.SendAsync(Get("/admin/ui", viaFunnel: false));
        Assert.AreEqual(HttpStatusCode.OK, tailnet.StatusCode);

        using var funnel = await browser.Http.SendAsync(Get("/admin/ui", viaFunnel: true));
        Assert.AreEqual(HttpStatusCode.Redirect, funnel.StatusCode);
        Assert.AreEqual(AdminSignInProcessor.LoginPath, funnel.Headers.Location!.OriginalString);
    }

    [TestMethod]
    public async Task AProviderSessionWorksOverFunnel()
    {
        var browser = _app.CreateBrowser();
        await browser.Http.GetAsync($"{AdjutantApp.TestSignInPath}?email={Uri.EscapeDataString(AdjutantApp.Owner)}");

        using var funnel = await browser.Http.SendAsync(Get("/admin/ui", viaFunnel: true));
        Assert.AreEqual(HttpStatusCode.OK, funnel.StatusCode);
    }

    [TestMethod]
    public async Task TheRestApiRefusesTheTokenOverFunnel()
    {
        using var client = _app.CreateClient();

        using var tailnet = Get("/admin/accounts", viaFunnel: false);
        tailnet.Headers.Authorization = new("Bearer", AdminToken);
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(tailnet)).StatusCode);

        using var funnel = Get("/admin/accounts", viaFunnel: true);
        funnel.Headers.Authorization = new("Bearer", AdminToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(funnel)).StatusCode);
    }

    [TestMethod]
    public async Task TheRestApiIsClosedOverFunnelEvenWithNoAdminTokenConfigured()
    {
        // With no token the REST API is open (development mode); never to the internet.
        _app.Dispose();
        _app = new AdjutantApp();
        using var client = _app.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Get("/admin/accounts", viaFunnel: false))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/admin/accounts", viaFunnel: true))).StatusCode);
    }

    [TestMethod]
    public async Task HealthStaysAnonymousOverFunnel()
    {
        using var client = _app.CreateClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Get("/health", viaFunnel: true))).StatusCode);
    }

    /// <summary>The login form's hidden fields (antiforgery token, form handler) plus the admin token.</summary>
    private static async Task<List<KeyValuePair<string, string>>> LoginFormFieldsAsync(HttpClient http)
    {
        var page = await http.GetStringAsync(AdminSignInProcessor.LoginPath);
        return HiddenInput().Matches(page)
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .Append(new("Input.Token", AdminToken))
            .ToList();
    }

    private static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient http, List<KeyValuePair<string, string>> fields, bool viaFunnel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AdminSignInProcessor.LoginPath)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        if (viaFunnel)
            request.Headers.Add(FunnelRequest.HeaderName, "?1");
        return http.SendAsync(request);
    }

    private static bool SetsAdminCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
        cookies.Any(c => c.Contains("AdminAuth", StringComparison.Ordinal));

    [GeneratedRegex("""<input type="hidden" name="([^"]*)" value="([^"]*)"\s*/?>""")]
    private static partial Regex HiddenInput();
}
