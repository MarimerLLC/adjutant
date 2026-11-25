using System.Security.Claims;
using Adjutant.Core.Configuration;
using Adjutant.Core.Security;
using Adjutant.HttpServer.BlazorAdmin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// The rule that ends an established console session. This is what makes removing an
/// administrator actually take effect on a circuit that is already open, so each way a session
/// can stop being valid is covered explicitly.
/// </summary>
[TestClass]
public class AdminSessionPolicyTests
{
    private string _directory = "";
    private AdminUserStore _users = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "adjutant-sessionpolicy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _users = new AdminUserStore(
            NullLogger<AdminUserStore>.Instance, Path.Combine(_directory, "admin-users.json"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ClaimsPrincipal OidcUser(string email, string provider = "google", string? subject = "sub-1")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
            new(AdminOidc.ProviderClaimType, provider)
        };

        if (subject is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal TokenUser() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"),
             new Claim(AdminOidc.ProviderClaimType, AdminSessionPolicy.TokenProvider)],
            "TestAuth"));

    private static AdminAuthConfiguration Config(
        string[]? allowed = null, bool? allowTokenLogin = null, bool withProvider = false)
    {
        var config = new AdminAuthConfiguration
        {
            AllowedEmails = [.. allowed ?? []],
            AllowTokenLogin = allowTokenLogin
        };

        if (withProvider)
        {
            config.Providers["google"] = new AdminAuthProviderConfiguration
            {
                Authority = "https://accounts.google.com",
                ClientId = "id",
                ClientSecret = "secret"
            };
        }

        return config;
    }

    private static ClaimsPrincipal TokenUser(string? fingerprint) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(AdminOidc.ProviderClaimType, AdminSessionPolicy.TokenProvider)
            }.Concat(fingerprint is null ? [] : [new Claim(AdminSessionPolicy.TokenFingerprintClaimType, fingerprint)]),
            "TestAuth"));

    private AdminTokenStore Tokens(string? configured) =>
        new(NullLogger<AdminTokenStore>.Instance, () => configured, Path.Combine(_directory, AdminTokenStore.FileName));

    [TestMethod]
    public void ATokenSessionLastsOnlyAsLongAsTheTokenItWasOpenedWith()
    {
        string? configured = "first-token";
        var tokens = new AdminTokenStore(NullLogger<AdminTokenStore>.Instance, () => configured,
            Path.Combine(_directory, AdminTokenStore.FileName));
        var session = TokenUser(tokens.Fingerprint);

        Assert.AreEqual(AdminSessionPolicy.Reason.None, AdminSessionPolicy.Evaluate(session, Config(), _users, tokens));

        configured = "rotated-token";
        Assert.AreEqual(AdminSessionPolicy.Reason.TokenChanged, AdminSessionPolicy.Evaluate(session, Config(), _users, tokens));
    }

    [TestMethod]
    public void ATokenSessionWithoutABindingEnds()
    {
        // A session from before the binding existed, including one opened when a server with no
        // token accepted any input.
        Assert.AreEqual(AdminSessionPolicy.Reason.TokenChanged,
            AdminSessionPolicy.Evaluate(TokenUser(fingerprint: null), Config(), _users, Tokens("some-token")));
        Assert.AreEqual(AdminSessionPolicy.Reason.TokenChanged,
            AdminSessionPolicy.Evaluate(TokenUser(fingerprint: ""), Config(), _users, Tokens(null)),
            "with no token at all, nothing is bound");
    }

    [TestMethod]
    public void TokenLoginTurningOffStillTakesPrecedence()
    {
        var tokens = Tokens("t");
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(AdminSessionPolicy.Reason.TokenLoginDisabled,
            AdminSessionPolicy.Evaluate(TokenUser(tokens.Fingerprint), Config(withProvider: true), _users, tokens));
    }

    [TestMethod]
    public void AnAllowedUserKeepsTheirSession()
    {
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(OidcUser("someone@example.com"), Config(["someone@example.com"]), _users));
    }

    [TestMethod]
    public void RemovingSomeoneFromTheAllowListEndsTheirSession()
    {
        // The whole point of revalidation: without it this would wait out the cookie.
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.NotOnAllowList,
            AdminSessionPolicy.Evaluate(OidcUser("someone@example.com"), Config(["someone.else@example.com"]), _users));
    }

    [TestMethod]
    public void EmptyingTheAllowListEndsEverySession()
    {
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.NotOnAllowList,
            AdminSessionPolicy.Evaluate(OidcUser("someone@example.com"), Config([]), _users));
    }

    [TestMethod]
    public void ADomainEntryKeepsTheSession()
    {
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(OidcUser("someone@example.com"), Config(["@example.com"]), _users));
    }

    [TestMethod]
    public void AnUnauthenticatedPrincipalHasNoSession()
    {
        Assert.AreEqual(
            AdminSessionPolicy.Reason.NotAuthenticated,
            AdminSessionPolicy.Evaluate(new ClaimsPrincipal(new ClaimsIdentity()), Config(["a@b.com"]), _users));
        Assert.AreEqual(
            AdminSessionPolicy.Reason.NotAuthenticated,
            AdminSessionPolicy.Evaluate(null, Config(["a@b.com"]), _users));
    }

    [TestMethod]
    public void ATokenSessionSurvivesWhileTokenLoginIsAllowed()
    {
        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(allowTokenLogin: true), _users));
    }

    [TestMethod]
    public void ATokenSessionEndsWhenTokenLoginIsTurnedOff()
    {
        Assert.AreEqual(
            AdminSessionPolicy.Reason.TokenLoginDisabled,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(allowTokenLogin: false), _users));
    }

    [TestMethod]
    public void ATokenSessionSurvivesAProviderThatHasNotWorkedYet()
    {
        // Configuring a provider is not proof that it works. Ending the token session here
        // would lock the operator out of the console they need to fix a misconfigured one (#125).
        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(withProvider: true), _users));
    }

    [TestMethod]
    public void ATokenSessionEndsAfterTheFirstSuccessfulProviderSignIn()
    {
        // AllowTokenLogin resolves to false once a provider sign-in has been recorded, so a
        // break-glass session should not stay open for the rest of the cookie's life.
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.TokenLoginDisabled,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(["someone@example.com"], withProvider: true), _users));
    }

    [TestMethod]
    public void ATokenSessionSurvivesAnExplicitOverride()
    {
        _users.RecordSignIn("someone@example.com", "google", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(allowTokenLogin: true, withProvider: true), _users));
    }

    [TestMethod]
    public void ATokenSessionEndsWhenTurnedOffBeforeAnyProviderSignIn()
    {
        Assert.AreEqual(
            AdminSessionPolicy.Reason.TokenLoginDisabled,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(allowTokenLogin: false, withProvider: true), _users));
    }

    [TestMethod]
    public void ATokenSessionIsNotCheckedAgainstTheAllowList()
    {
        // It carries no email, so applying the allow-list to it would end every token session.
        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(TokenUser(), Config(["someone@example.com"], allowTokenLogin: true), _users));
    }

    [TestMethod]
    public void AChangedSubjectBindingEndsTheSession()
    {
        _users.RecordSignIn("someone@example.com", "google", "sub-1");
        _users.Remove("someone@example.com");
        _users.RecordSignIn("someone@example.com", "google", "sub-2");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.SubjectChanged,
            AdminSessionPolicy.Evaluate(
                OidcUser("someone@example.com", subject: "sub-1"), Config(["someone@example.com"]), _users));
    }

    [TestMethod]
    public void ForgettingAUserDoesNotEndAnOtherwiseValidSession()
    {
        // No record means nothing to contradict; the allow-list is still what authorizes them.
        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(OidcUser("someone@example.com"), Config(["someone@example.com"]), _users));
    }

    [TestMethod]
    public void ASessionWithNoSubjectIsJudgedOnTheAllowListAlone()
    {
        _users.RecordSignIn("someone@example.com", "microsoft", "sub-1");

        Assert.AreEqual(
            AdminSessionPolicy.Reason.None,
            AdminSessionPolicy.Evaluate(
                OidcUser("someone@example.com", "microsoft", subject: null),
                Config(["someone@example.com"]), _users));
    }

    [TestMethod]
    public void AnOidcSessionWithNoEmailIsRefused()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "someone"), new Claim(AdminOidc.ProviderClaimType, "google")],
            "TestAuth"));

        Assert.AreEqual(
            AdminSessionPolicy.Reason.NotOnAllowList,
            AdminSessionPolicy.Evaluate(principal, Config(["someone@example.com"]), _users));
    }
}
