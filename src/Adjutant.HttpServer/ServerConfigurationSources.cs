namespace Adjutant.HttpServer;

/// <summary>
/// The configuration files the HTTP server reads, in the order they apply.
/// </summary>
public static class ServerConfigurationSources
{
    /// <summary>
    /// Adds the shipped defaults (<paramref name="appConfigPath"/>, next to the binaries) and, over
    /// them, the operator's settings (<paramref name="dataConfigPath"/>, in the data directory).
    ///
    /// The data-directory file is always registered and watched, even when it does not exist yet:
    /// the admin console writes there, and on a first run its first save creates the file. Were it
    /// left out because it was missing at startup, that save would not reach the running server
    /// until a restart (#130). The shipped file is not watched; it only changes with the image.
    ///
    /// Arrays merge by index across files, so the shipped file must keep <c>Adjutant:Accounts</c>
    /// empty for the operator's account list to be the whole list.
    /// </summary>
    /// <returns>The files that exist now, lowest precedence first, for the startup log.</returns>
    public static IReadOnlyList<string> AddSettingsFiles(
        IConfigurationBuilder configuration, string dataConfigPath, string appConfigPath)
    {
        var sameFile = string.Equals(
            Path.GetFullPath(dataConfigPath), Path.GetFullPath(appConfigPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!sameFile)
        {
            configuration.AddJsonFile(appConfigPath, optional: true, reloadOnChange: false);
        }

        configuration.AddJsonFile(dataConfigPath, optional: true, reloadOnChange: true);

        var loaded = new List<string>();
        if (!sameFile && File.Exists(appConfigPath))
            loaded.Add(appConfigPath);
        if (File.Exists(dataConfigPath))
            loaded.Add(dataConfigPath);
        return loaded;
    }
}
