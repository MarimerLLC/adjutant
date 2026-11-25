using Adjutant.Core.Security;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// The login page a locked-out operator actually sees (#125): with a provider configured but never
/// used successfully, the admin token field must still be there.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AdminTokenLoginIntegrationTests
{
    private AdjutantApp _app = null!;

    // The default settings configure a Google provider and an allow-list, as an operator has done
    // after following the setup guide but before any sign-in has gone through.
    [TestInitialize]
    public void Start() => _app = new AdjutantApp();

    [TestCleanup]
    public void Stop() => _app.Dispose();

    private async Task<string> LoginPageAsync(string query = "")
    {
        using var client = _app.CreateClient();
        return await client.GetStringAsync(AdminSignInProcessor.LoginPath + query);
    }

    [TestMethod]
    public async Task TheTokenFieldStaysUntilAProviderSignInSucceeds()
    {
        var page = await LoginPageAsync();
        StringAssert.Contains(page, "Sign in with Google");
        StringAssert.Contains(page, "id=\"token\"", "token login should survive a provider that has not worked yet");

        _app.Services.GetRequiredService<IAdminUserStore>()
            .RecordSignIn(AdjutantApp.Owner, AdminOidc.GoogleScheme, "sub-1");

        page = await LoginPageAsync();
        StringAssert.Contains(page, "Sign in with Google");
        Assert.IsFalse(page.Contains("id=\"token\""), "token login should end after the first provider sign-in");
    }

    [TestMethod]
    public async Task AProviderFailureNamesTheBreakGlassSettingOnlyWhenTheTokenIsGone()
    {
        Assert.IsFalse((await LoginPageAsync("?error=provider")).Contains("AdminAuth:AllowTokenLogin"),
            "the token field is right there, no hint needed");

        _app.Services.GetRequiredService<IAdminUserStore>()
            .RecordSignIn(AdjutantApp.Owner, AdminOidc.GoogleScheme, "sub-1");

        StringAssert.Contains(await LoginPageAsync("?error=provider"), "AdminAuth:AllowTokenLogin");
    }
}
