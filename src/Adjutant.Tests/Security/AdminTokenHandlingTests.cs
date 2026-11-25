using Adjutant.Core.Security;
using Adjutant.HttpServer.Admin;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.HttpServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Adjutant.Tests.Security;

/// <summary>Review follow-ups on #131: header parsing and keeping generated secrets out of log files.</summary>
[TestClass]
public sealed class AdminTokenHandlingTests
{
    private static HttpRequest Request(string? authorization = null, string? adminTokenHeader = null)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        if (adminTokenHeader is not null)
            context.Request.Headers["X-Admin-Token"] = adminTokenHeader;
        return context.Request;
    }

    [TestMethod]
    [DataRow("Bearer tok", null, "tok")]
    [DataRow("bearer tok", null, "tok")]
    [DataRow("BEARER tok", null, "tok")]
    [DataRow(null, "tok", "tok")]
    [DataRow("Basic dXNlcjpwYXNz", "tok", "tok")]
    [DataRow("Bearer tok", "other", "tok")]
    [DataRow("tok", null, "tok")]
    [DataRow(null, null, null)]
    public void ThePresentedTokenIsReadFromEitherHeader(string? authorization, string? header, string? expected)
    {
        Assert.AreEqual(expected, AdminAuthMiddleware.PresentedToken(Request(authorization, header)));
    }

    [TestMethod]
    public void AGeneratedSecretReachesTheConsoleButNotTheLogger()
    {
        var logger = new RecordingLogger();
        var console = new StringWriter();

        StartupSecrets.Show(logger, console, "Admin token", "adjadm_secret-value");

        StringAssert.Contains(console.ToString(), "Admin token: adjadm_secret-value");
        Assert.IsFalse(logger.Lines.Any(l => l.Contains("adjadm_secret-value")), "the secret must not reach file or OTLP sinks");
        Assert.IsTrue(logger.Lines.Any(l => l.Contains(StartupSecrets.Placeholder)));
    }

    [TestMethod]
    public void TheAdminTokenBannerKeepsTheSecretOutOfTheLogger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adjutant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tokens = new AdminTokenStore(NullLogger<AdminTokenStore>.Instance, () => null, Path.Combine(dir, AdminTokenStore.FileName));
            var logger = new RecordingLogger();
            var console = new StringWriter();

            AdminAuthStartup.EnsureAdminToken(tokens, logger, console);

            var printed = System.Text.RegularExpressions.Regex.Match(console.ToString(), @"Admin token: (\S+)").Groups[1].Value;
            Assert.IsTrue(tokens.Matches(printed), "the console shows the real token");
            Assert.IsFalse(logger.Lines.Any(l => l.Contains(printed)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
