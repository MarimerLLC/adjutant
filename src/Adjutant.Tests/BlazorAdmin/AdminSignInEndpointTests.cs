using System.Net;
using Adjutant.Core.Security;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.Tests.Identity;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// A provider that can't start a sign-in must send the operator back to the login page, where the
/// break-glass hint is shown, not leave them on a bare 500.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AdminSignInEndpointTests
{
    private AdjutantApp? _app;

    [TestCleanup]
    public void Stop() => _app?.Dispose();

    private static object Settings(string authority, string secret) => new
    {
        Adjutant = new { Accounts = Array.Empty<object>() },
        AdminAuth = new
        {
            AllowedEmails = new[] { AdjutantApp.Owner },
            Providers = new { google = new { Authority = authority, ClientId = "test-client", ClientSecret = secret } }
        }
    };

    private async Task<HttpResponseMessage> StartSignInAsync(object settings)
    {
        _app = new AdjutantApp(settings);
        return await _app.CreateBrowser().Http.GetAsync(AdminOidc.ChallengePath(AdminOidc.GoogleScheme));
    }

    [TestMethod]
    public async Task ASecretThatNoLongerDecryptsRedirectsWithAProviderError()
    {
        var response = await StartSignInAsync(
            Settings("https://accounts.google.com", PasswordProtector.EncryptedPrefix + "not-a-protected-payload"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(AdminSignInProcessor.LoginPathWithError("provider"), response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task AnAuthorityWhoseDiscoveryFailsRedirectsWithAProviderError()
    {
        // .invalid never resolves, so discovery fails inside the challenge.
        var response = await StartSignInAsync(Settings("https://adjutant-tests.invalid", "test-secret"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(AdminSignInProcessor.LoginPathWithError("provider"), response.Headers.Location?.OriginalString);
    }
}
