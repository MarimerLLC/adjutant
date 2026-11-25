using System.Net;
using System.Text.RegularExpressions;
using Adjutant.Core.Security;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// #131: a server with no admin token configured must not leave the admin API or the console
/// login open. It generates a token at first start instead, like the first MCP API key.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class AdminTokenBootstrapIntegrationTests
{
    // No AdminToken and no sign-in provider: the state of a freshly started container.
    private static object NoTokenSettings() => new
    {
        Adjutant = new { Accounts = Array.Empty<object>() },
        AdminAuth = new { AllowedEmails = Array.Empty<string>() }
    };

    [TestMethod]
    public async Task WithNoTokenConfiguredTheAdminApiRequiresTheGeneratedOne()
    {
        using var app = new AdjutantApp(NoTokenSettings());
        using var client = app.CreateClient();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/accounts")).StatusCode);

        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/accounts")
        {
            Content = JsonContent("""{"id":"x","displayName":"X","provider":"ics","providerConfig":{"IcsUrl":"https://example.com/a.ics"}}""")
        };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(create)).StatusCode);
        Assert.IsFalse(File.ReadAllText(Path.Combine(app.DataDirectory, "appsettings.json")).Contains("\"x\""),
            "an unauthenticated request must not write the account");

        var tokens = app.Services.GetRequiredService<IAdminTokenStore>();
        Assert.IsTrue(tokens.HasToken, "startup should have generated a token");
        Assert.IsFalse(tokens.IsConfigured);
        Assert.IsTrue(File.Exists(Path.Combine(app.DataDirectory, AdminTokenStore.FileName)));
    }

    [TestMethod]
    public async Task TheConsoleLoginRejectsAnArbitraryToken()
    {
        using var app = new AdjutantApp(NoTokenSettings());
        var browser = app.CreateBrowser();

        using var response = await PostLoginAsync(browser.Http, "anything-at-all");

        Assert.AreNotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsFalse(SetsAdminCookie(response), "no session for a token the server never issued");
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Invalid admin token");

        // And the console itself stays closed.
        using var console = await browser.Http.GetAsync("/admin/ui");
        Assert.AreEqual(HttpStatusCode.Redirect, console.StatusCode);
    }

    [TestMethod]
    public async Task TheStoredTokenIsAcceptedByTheApiAndTheLoginPage()
    {
        using var app = new AdjutantApp(NoTokenSettings());

        // Generated the way a first start does, ahead of the server, so the test knows the secret
        // that the server's startup banner would have printed.
        var secret = new AdminTokenStore(NullLogger<AdminTokenStore>.Instance, () => null,
            Path.Combine(app.DataDirectory, AdminTokenStore.FileName)).EnsureToken()!;

        using var client = app.CreateClient();
        using var list = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts");
        list.Headers.Authorization = new("Bearer", secret);
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(list)).StatusCode);

        using var header = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts");
        header.Headers.Add("X-Admin-Token", secret);
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(header)).StatusCode);

        var browser = app.CreateBrowser();
        using var login = await PostLoginAsync(browser.Http, secret);
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode, await login.Content.ReadAsStringAsync());
        Assert.IsTrue(SetsAdminCookie(login));
    }

    [TestMethod]
    public async Task AConfiguredTokenOverridesTheStoredOne()
    {
        using var app = new AdjutantApp(new
        {
            Adjutant = new { Accounts = Array.Empty<object>(), AdminToken = "operator-token" },
            AdminAuth = new { AllowedEmails = Array.Empty<string>() }
        });
        var stored = new AdminTokenStore(NullLogger<AdminTokenStore>.Instance, () => null,
            Path.Combine(app.DataDirectory, AdminTokenStore.FileName)).EnsureToken()!;

        using var client = app.CreateClient();

        using var withStored = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts");
        withStored.Headers.Authorization = new("Bearer", stored);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(withStored)).StatusCode);

        using var withConfigured = new HttpRequestMessage(HttpMethod.Get, "/admin/accounts");
        withConfigured.Headers.Authorization = new("Bearer", "operator-token");
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(withConfigured)).StatusCode);
    }

    [TestMethod]
    public async Task ATokenSessionEndsWhenTheTokenIsReplaced()
    {
        var settings = (string token) => new
        {
            Adjutant = new { Accounts = Array.Empty<object>(), AdminToken = token },
            AdminAuth = new { AllowedEmails = Array.Empty<string>() }
        };
        using var app = new AdjutantApp(settings("first-token"));

        var browser = app.CreateBrowser();
        await browser.Http.GetAsync($"{AdjutantApp.TestSignInPath}?provider={AdminSessionPolicy.TokenProvider}");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.Http.GetAsync("/admin/ui")).StatusCode);

        app.UpdateSettings(settings("rotated-token"));

        using var after = await browser.Http.GetAsync("/admin/ui");
        Assert.AreEqual(HttpStatusCode.Redirect, after.StatusCode, "the old token's session must end");
        Assert.AreEqual(AdminSignInProcessor.LoginPath, AdjutantApp.Browser.RedirectOf(after).AbsolutePath);
    }

    [TestMethod]
    public async Task ATokenSessionFromBeforeTheBindingIsRefused()
    {
        // Stands in for a cookie issued before the upgrade, when this server accepted any token.
        using var app = new AdjutantApp(NoTokenSettings());
        var browser = app.CreateBrowser();
        await browser.Http.GetAsync($"{AdjutantApp.TestSignInPath}?provider={AdminSessionPolicy.TokenProvider}&fingerprint=none");

        using var response = await browser.Http.GetAsync("/admin/ui");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(AdminSignInProcessor.LoginPath, AdjutantApp.Browser.RedirectOf(response).AbsolutePath);
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient http, string token)
    {
        var page = await http.GetStringAsync(AdminSignInProcessor.LoginPath);
        StringAssert.Contains(page, "id=\"token\"", "token login should be offered on a fresh server");

        var fields = HiddenInput().Matches(page)
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .Append(new("Input.Token", token))
            .ToList();
        return await http.PostAsync(AdminSignInProcessor.LoginPath, new FormUrlEncodedContent(fields));
    }

    private static bool SetsAdminCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
        cookies.Any(c => c.Contains("AdminAuth", StringComparison.Ordinal));

    [GeneratedRegex("""<input type="hidden" name="([^"]*)" value="([^"]*)"\s*/?>""")]
    private static partial Regex HiddenInput();
}
