using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Impulsum14;

internal sealed class ManagerTaskProgress
{
    public bool Completed { get; set; }
    public bool Claimed { get; set; }
}

internal static class ManagerTasks
{
    // CardsDLLzf.dll's ManagerQuest GET/SET serializers exchange three opaque
    // signed integers. They are client UI state, never authority to mint rewards.
    internal static string NativeJson() => JsonSerializer.Serialize(new
    {
        entries = PlayerContext.Current.Data.NativeManagerQuest.Select((value, key) => new { key, value })
    });

    internal static string SaveNative(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 3)
            throw new BadHttpRequestException("Manager quest requires three entries.", 400);
        int[] values = new int[3];
        int seen = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.Number || !keyValue.TryGetInt32(out int key) || key is < 0 or > 2 ||
                (seen & (1 << key)) != 0 || !entry.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out values[key]))
                throw new BadHttpRequestException("Invalid manager quest entry.", 400);
            seen |= 1 << key;
        }
        var data = PlayerContext.Current.Data;
        if (!data.NativeManagerQuest.SequenceEqual(values))
        {
            data.NativeManagerQuest = values;
            PlayerContext.MarkDirty();
        }
        return NativeJson();
    }

    // Server-observed community tasks. See docs/COMPATIBILITY.md for the retail UI limitation.
    internal static readonly (int Id, string Event, string Name)[] Definitions =
    {
        (1,"create_club","Create a club"), (2,"rename_club","Rename your club"), (3,"create_squad","Create a squad"),
        (4,"rename_squad","Rename a squad"), (5,"formation","Change formation"), (6,"contract","Apply a contract"),
        (7,"fitness","Apply fitness"), (8,"training","Apply training"), (9,"healing","Heal a player"),
        (10,"position","Change player position"), (11,"chemstyle","Apply chemistry style"), (12,"kit","Equip a kit"),
        (13,"badge","Equip a badge"), (14,"stadium","Equip a stadium"), (15,"buy_player","Buy a player"),
        (16,"list_player","List a player"), (17,"open_pack","Open a pack"), (18,"play_match","Complete a match")
    };
    internal static void Observe(string action)
    {
        var match = Definitions.FirstOrDefault(d => d.Event == action);
        if (match.Id == 0) return;
        var tasks = PlayerContext.Current.Data.ManagerTasks;
        if (tasks.TryGetValue(match.Id, out var existing) && existing.Completed) return;
        tasks[match.Id] = new ManagerTaskProgress { Completed = true };
        PlayerContext.MarkDirty();
    }
    internal static string Json(int earned = 0)
    {
        var data = PlayerContext.Current.Data;
        return JsonSerializer.Serialize(new
        {
            tasks = Definitions.Select(d => new { id = d.Id, name = d.Name, completed = data.ManagerTasks.GetValueOrDefault(d.Id)?.Completed == true, claimed = data.ManagerTasks.GetValueOrDefault(d.Id)?.Claimed == true, coins = ServerOptions.Current.ManagerTaskCoins }),
            completionClaimed = data.ManagerCompletionClaimed, completionCoins = ServerOptions.Current.ManagerCompletionCoins,
            coinsEarned = earned, credits = data.Profile.Coins
        });
    }
    internal static string Claim(int id = 0)
    {
        if (id != 0 && !Definitions.Any(d => d.Id == id)) throw new BadHttpRequestException("Unknown manager task.", 404);
        var data = PlayerContext.Current.Data;
        int earned = 0;
        bool changed = false;
        foreach (var pair in data.ManagerTasks)
            if ((id == 0 || id == pair.Key) && pair.Value.Completed && !pair.Value.Claimed)
            { pair.Value.Claimed = true; changed = true; earned += ServerOptions.Current.ManagerTaskCoins; }
        if (!data.ManagerCompletionClaimed && Definitions.All(d => data.ManagerTasks.GetValueOrDefault(d.Id)?.Claimed == true))
        { data.ManagerCompletionClaimed = true; changed = true; earned += ServerOptions.Current.ManagerCompletionCoins; }
        if (changed) { checked { data.Profile.Coins += earned; } PlayerContext.MarkDirty(); }
        return Json(earned);
    }
}
