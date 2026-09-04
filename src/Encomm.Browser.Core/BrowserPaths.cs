namespace Encomm.Browser.Core;

/// <summary>
/// Single source of truth for filesystem paths the browser uses.
/// All paths are local to the current user.
/// </summary>
public sealed class BrowserPaths
{
    public string RootDirectory { get; }
    public string DatabaseFile { get; }
    public string UserDataDirectory { get; }
    public string LogsDirectory { get; }
    public string PreviewDirectory { get; }
    public string CacheDirectory { get; }

    public BrowserPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DatabaseFile = Path.Combine(rootDirectory, "encomm.db");
        UserDataDirectory = Path.Combine(rootDirectory, "UserData");
        LogsDirectory = Path.Combine(rootDirectory, "Logs");
        PreviewDirectory = Path.Combine(rootDirectory, "Previews");
        CacheDirectory = Path.Combine(rootDirectory, "Cache");
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(PreviewDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public static BrowserPaths Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "Encomm", "Encomm-AI-Browser");
        return new BrowserPaths(root);
    }
}