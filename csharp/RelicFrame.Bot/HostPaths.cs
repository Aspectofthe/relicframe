internal static class HostPaths
{
    public static string EeLog(string runtime)
    {
        var configured = Environment.GetEnvironmentVariable("RELICFRAME_EE_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe", "EE.log");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var roots = new[]
        {
            !string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(dataHome, "Steam") : "",
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root")
        };
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(root, "steamapps", "compatdata", "230410", "pfx", "drive_c", "users", "steamuser", "Local Settings", "Application Data", "Warframe", "EE.log");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(runtime, "EE.log");
    }
}
