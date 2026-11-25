namespace Adjutant.HttpServer.Security;

/// <summary>
/// Shows a secret generated at first start (the admin token, the first MCP API key) to the person
/// running the server, without writing it to the log file or exporting it over OpenTelemetry.
///
/// Logging the secret would put it in plain text in <c>{data}/logs</c> (kept seven days) and in
/// any OTLP backend, which contradicts "hashed at rest, never shown again". So the logger gets a
/// placeholder, and the secret goes to the process's standard output only. That is still what
/// <c>docker logs</c> and <c>kubectl logs</c> show.
/// </summary>
public static class StartupSecrets
{
    /// <summary>What the logger records in place of the secret.</summary>
    public const string Placeholder = "(printed to the console only, not to log files)";

    /// <summary>
    /// Logs <paramref name="label"/> with <see cref="Placeholder"/>, then writes the real value to
    /// <paramref name="console"/> in the same layout as the console log lines around it.
    /// </summary>
    public static void Show(ILogger logger, TextWriter console, string label, string secret)
    {
        logger.LogWarning("    {Label}: {Secret}", label, Placeholder);
        console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [WRN]     {label}: {secret}");
        console.Flush();
    }
}
