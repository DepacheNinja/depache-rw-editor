namespace RwEditor.Core.Setup;

/// <summary>
/// The one folder where the editor keeps what it remembers about the user (app settings, logs, backups for Undo, picture cache, Test load results, the default
/// place for a copy). By default %LOCALAPPDATA%\DepacheRwEditor. The environment variable <c>DEPACHE_RW_EDITOR_HOME</c> moves all of it (a portable setup, or a
/// clean first run for the release check) without touching the real profile.
/// </summary>
public static class ProfileFolder
{
    public const string EnvironmentVariable = "DEPACHE_RW_EDITOR_HOME";

    public static string Root
    {
        get
        {
            string? o = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(o) && Path.IsPathFullyQualified(o)
                ? o
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepacheRwEditor");
        }
    }

    public static string Sub(string name) => Path.Combine(Root, name);
}
