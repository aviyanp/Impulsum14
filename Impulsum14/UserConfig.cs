using System.Text.Json;

namespace Impulsum14;

internal static class UserConfig
{
    private static readonly string _path = Path.Combine(ServerOptions.Current.DataDirectory, "user.json");
    private static readonly UserConfigData _data = Load();

    public static string Username => _data.Username;

    private static UserConfigData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<UserConfigData>(File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data != null && !string.IsNullOrWhiteSpace(data.Username))
                {
                    ServerDiagnostics.Write($"[UserConfig] loaded username '{data.Username}' from {_path}");
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[UserConfig] failed to load {_path}: {ex.GetType().Name}: {ex.Message}");
        }

        var defaults = new UserConfigData();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            ServerDiagnostics.Write($"[UserConfig] created default {_path} (username='{defaults.Username}')");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Write($"[UserConfig] could not write default {_path}: {ex.GetType().Name}: {ex.Message}");
        }
        return defaults;
    }

    private sealed class UserConfigData
    {
        public string Username { get; set; } = "FUT14";
    }
}
