using Adjutant.Core.Configuration;

namespace Adjutant.Tests.Configuration;

/// <summary>
/// How the tri-state <see cref="AdminAuthConfiguration.AllowTokenLogin"/> resolves. The admin token
/// is the only way into the console while no provider works, so the automatic setting must keep it
/// until a provider sign-in has actually succeeded (#125).
/// </summary>
[TestClass]
public class AdminAuthConfigurationTests
{
    private static AdminAuthConfiguration Config(bool? allowTokenLogin, bool withProvider)
    {
        var config = new AdminAuthConfiguration { AllowTokenLogin = allowTokenLogin };
        if (withProvider)
        {
            config.Providers["microsoft"] = new AdminAuthProviderConfiguration
            {
                Authority = "https://login.microsoftonline.com/common/v2.0",
                ClientId = "id",
                ClientSecret = "secret"
            };
        }

        return config;
    }

    [TestMethod]
    public void Automatic_AllowsTheTokenOnAFreshServer()
    {
        Assert.IsTrue(Config(null, withProvider: false).IsTokenLoginAllowed(oidcSignInRecorded: false));
    }

    [TestMethod]
    public void Automatic_KeepsTheTokenWhileAConfiguredProviderHasNotWorkedYet()
    {
        // The lock-out in #125: a provider saved with the secret ID instead of its value.
        Assert.IsTrue(Config(null, withProvider: true).IsTokenLoginAllowed(oidcSignInRecorded: false));
    }

    [TestMethod]
    public void Automatic_RemovesTheTokenAfterTheFirstSuccessfulProviderSignIn()
    {
        Assert.IsFalse(Config(null, withProvider: true).IsTokenLoginAllowed(oidcSignInRecorded: true));
    }

    [TestMethod]
    public void Automatic_StaysOffIfTheProviderIsLaterRemoved()
    {
        // Someone has signed in before, so the console is not fresh; the explicit setting is the
        // way back in.
        Assert.IsFalse(Config(null, withProvider: false).IsTokenLoginAllowed(oidcSignInRecorded: true));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AnExplicitValueOverridesTheAutomaticRule(bool withProvider, bool oidcSignInRecorded)
    {
        Assert.IsTrue(Config(true, withProvider).IsTokenLoginAllowed(oidcSignInRecorded));
        Assert.IsFalse(Config(false, withProvider).IsTokenLoginAllowed(oidcSignInRecorded));
    }
}
