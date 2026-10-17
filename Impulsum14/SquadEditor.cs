using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Impulsum14;

/// <summary>Applies native squad create/copy/update documents to owned club data.</summary>
internal static class SquadEditor
{
    internal static void Apply(ClubData data, Squad target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("Expected a squad object.");

        // Parse everything before assigning anything. A malformed field must not
        // leave a partly renamed squad even when called outside a transaction.
        string name = target.Name;
        string formation = target.Formation;
        int chemistry = target.Chemistry;
        int rating = target.StarRating;
        long managerId = target.ManagerId;
        var slots = new Dictionary<int, long>(target.Slots);
        var kitNumbers = new Dictionary<int, int>(target.KitNumbers);

        if (root.TryGetProperty("squadName", out var incomingName))
        {
            if (incomingName.ValueKind != JsonValueKind.String) throw Invalid("Invalid squad name.");
            string value = incomingName.GetString()!;
            if (value.Length > 32 || value.Any(char.IsControl)) throw Invalid("Invalid squad name.");
            // FIFA's regular player updates carry an empty name for unchanged.
            if (!string.IsNullOrWhiteSpace(value)) name = value;
        }
        if (root.TryGetProperty("formation", out var incomingFormation))
        {
            if (incomingFormation.ValueKind != JsonValueKind.String) throw Invalid("Invalid formation.");
            string value = incomingFormation.GetString()!;
            if (value.Length is < 1 or > 32 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw Invalid("Invalid formation.");
            formation = value;
        }
        if (root.TryGetProperty("chemistry", out var incomingChemistry)) chemistry = Rating(incomingChemistry, "chemistry");
        if (root.TryGetProperty("starRating", out var incomingRating)) rating = Rating(incomingRating, "star rating");
        else if (root.TryGetProperty("rating", out incomingRating)) rating = Rating(incomingRating, "rating");

        if (root.TryGetProperty("players", out var players))
        {
            if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() > 23)
                throw Invalid("Invalid squad player list.");
            var owned = data.Inventory.ToDictionary(card => card.ItemId);
            var newSlots = new Dictionary<int, long>();
            var newKitNumbers = new Dictionary<int, int>();
            var indices = new HashSet<int>();
            var itemIds = new HashSet<long>();
            var footballers = new HashSet<int>();
            foreach (var player in players.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object || !player.TryGetProperty("index", out var index) ||
                    index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int slot) || slot is < 0 or > 22 || !indices.Add(slot) ||
                    !player.TryGetProperty("itemData", out var item) || item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var identity))
                    throw Invalid("Invalid squad slot.");
                long id = ItemId(identity);
                int kitNumber = 0;
                if (player.TryGetProperty("kitNumber", out var number) &&
                    (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out kitNumber) || kitNumber is < 0 or > 99))
                    throw Invalid("Invalid squad shirt number.");
                if (id == 0) continue;
                if (!owned.TryGetValue(id, out var card) || card.Pile is not (6 or 7) ||
                    (data.Listings.TryGetValue(id, out var listing) && listing.State is "active" or "sold") ||
                    !itemIds.Add(id) || !footballers.Add(card.Player.Id))
                    throw Conflict("Squad players must be distinct footballers owned in the club.");
                newSlots.Add(slot, id);
                if (kitNumber > 0) newKitNumbers.Add(slot, kitNumber);
            }
            // Both [] and 23 explicit zero slots intentionally clear a squad.
            slots = newSlots;
            kitNumbers = newKitNumbers;
        }

        if (root.TryGetProperty("manager", out var manager))
        {
            if (manager.ValueKind != JsonValueKind.Array || manager.GetArrayLength() > 1)
                throw Invalid("Invalid squad manager.");
            managerId = 0;
            if (manager.GetArrayLength() == 1)
            {
                var item = manager[0];
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("Invalid squad manager.");
                if (item.TryGetProperty("itemData", out var nested)) item = nested;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var identity))
                    throw Invalid("Invalid squad manager.");
                managerId = ItemId(identity);
            }
            if (managerId != 0 && !data.ManagerItemIds.Contains(managerId)) throw Conflict("Manager is not owned.");
        }

        var members = slots.Values.Where(id => id != 0).ToHashSet();
        long captain = members.Contains(target.CaptainId) ? target.CaptainId : 0;
        var takers = target.KickTakers
            .Where(pair => pair.Key is >= 0 and < 5 && (pair.Value == 0 || members.Contains(pair.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (root.TryGetProperty("captain", out var incomingCaptain))
        {
            captain = ItemId(incomingCaptain);
            if (captain != 0 && !members.Contains(captain)) throw Conflict("Captain must belong to the squad.");
        }
        if (root.TryGetProperty("kicktakers", out var incomingTakers))
        {
            if (incomingTakers.ValueKind != JsonValueKind.Array || incomingTakers.GetArrayLength() > 5)
                throw Invalid("Invalid squad kick takers.");
            takers = new Dictionary<int, long>();
            foreach (var taker in incomingTakers.EnumerateArray())
            {
                if (taker.ValueKind != JsonValueKind.Object || !taker.TryGetProperty("index", out var index) ||
                    index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int slot) || slot is < 0 or >= 5 ||
                    !taker.TryGetProperty("id", out var identity))
                    throw Invalid("Invalid squad kick taker.");
                long id = ItemId(identity);
                if (id != 0 && !members.Contains(id)) throw Conflict("Kick takers must belong to the squad.");
                if (!takers.TryAdd(slot, id)) throw Invalid("Duplicate kick taker index.");
            }
        }

        target.Name = name;
        target.Formation = formation;
        target.Chemistry = chemistry;
        target.StarRating = rating;
        target.Slots = slots;
        target.KitNumbers = kitNumbers;
        target.ManagerId = managerId;
        target.CaptainId = captain;
        target.KickTakers = takers;
    }

    internal static void ReconcilePiles(ClubData data)
    {
        var assigned = data.Squads.SelectMany(squad => squad.Slots.Values).Where(id => id != 0).ToHashSet();
        for (int i = 0; i < data.Inventory.Count; i++)
        {
            var card = data.Inventory[i];
            // Transfer and unassigned queues own these piles independently.
            if (card.Pile is 0 or 3) continue;
            int pile = assigned.Contains(card.ItemId) ? 7 : 6;
            if (card.Pile != pile) data.Inventory[i] = card with { Pile = pile };
        }
    }

    private static int Rating(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) throw Invalid("Invalid " + field + ".");
        return Math.Clamp(result, 0, 100);
    }

    private static long ItemId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long id) || id < 0) throw Invalid("Invalid item identity.");
        return id;
    }

    private static BadHttpRequestException Invalid(string message) => new(message, 400);
    private static BadHttpRequestException Conflict(string message) => new(message, 409);
}
