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
    public void Automatic_ComesBackWhenEveryProviderIsRemoved()
    {
        // With no provider left there is no other way in, whatever worked before.
        Assert.IsTrue(Config(null, withProvider: false).IsTokenLoginAllowed(oidcSignInRecorded: true));
    }

    [TestMethod]
    public void Automatic_IgnoresSignInsThroughAProviderNoLongerConfigured()
    {
        // Google worked once, then was replaced by a Microsoft provider that has not worked yet.
        var store = new FakeUsers("google");
        Assert.IsTrue(Config(null, withProvider: true).IsTokenLoginAllowed(store));

        store.Providers.Add("microsoft");
        Assert.IsFalse(Config(null, withProvider: true).IsTokenLoginAllowed(store));
    }

    /// <summary>Records one sign-in per listed provider.</summary>
    private sealed class FakeUsers(params string[] providers) : Adjutant.Core.Security.IAdminUserStore
    {
        public List<string> Providers { get; } = [.. providers];

        public bool HasRecordedSignIn(IEnumerable<string> configured) =>
            Providers.Intersect(configured, StringComparer.OrdinalIgnoreCase).Any();

        public IReadOnlyList<Adjutant.Core.Security.AdminUser> List() => [];
        public Adjutant.Core.Security.AdminUser? Find(string? email) => null;
        public Adjutant.Core.Security.AdminSignInResult RecordSignIn(string email, string provider, string? subject) =>
            throw new NotSupportedException();
        public bool Remove(string email) => throw new NotSupportedException();
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
