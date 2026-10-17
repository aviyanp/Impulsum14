using System.Text.Json;

namespace Impulsum14;

internal static class ClientDataStore
{
    private static object _lock => PlayerContext.Current.Sync;
    private static Dictionary<string, string> _data => PlayerContext.Current.Data.ClientData;

    public static string Get(string key)
    {
        lock (_lock) return _data.TryGetValue(key, out var v) ? v : "{}";
    }

    public static void Set(string key, string json)
    {
        lock (_lock)
        {
            if (key.Length > 128 || json.Length > 65536 || (_data.Count >= 128 && !_data.ContainsKey(key))) throw new InvalidDataException("Client data limits exceeded.");
            _data[key] = string.IsNullOrWhiteSpace(json) ? "{}" : json;
            Save();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _data.Clear();
            Save();
        }
    }

    private static void Save() => PlayerContext.MarkDirty();
}
