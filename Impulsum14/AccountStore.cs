using System.Text.Json;

namespace Impulsum14;

internal sealed class AccountProfile
{
    public string Email { get; set; } = "player@fifa14.local";
    public string Country { get; set; } = "GB";
    public string Dob { get; set; } = "1990-01-01";
    public string Language { get; set; } = "en";
    public byte GlobalOptin { get; set; } = 2;      // 2 = opted out
    public byte ThirdPartyOptin { get; set; } = 2;
}

internal static class AccountStore
{
    private static object _lock => PlayerContext.Current.Sync;
    private static AccountProfile _profile => PlayerContext.Current.Data.Account;

    public static AccountProfile Get()
    {
        lock (_lock)
            return new AccountProfile
            {
                Email = _profile.Email,
                Country = _profile.Country,
                Dob = _profile.Dob,
                Language = _profile.Language,
                GlobalOptin = _profile.GlobalOptin,
                ThirdPartyOptin = _profile.ThirdPartyOptin,
            };
    }

    // Apply an update, ignoring empty/blank fields so the game's empty sync never wipes our values. Returns the merged profile.
    public static AccountProfile Update(string email, string country, string dob, string language,
        byte globalOptin, byte thirdPartyOptin)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(email)) _profile.Email = email;
            if (!string.IsNullOrWhiteSpace(country)) _profile.Country = country;
            if (!string.IsNullOrWhiteSpace(dob)) _profile.Dob = dob;
            if (!string.IsNullOrWhiteSpace(language)) _profile.Language = language;
            if (globalOptin != 0) _profile.GlobalOptin = globalOptin;
            if (thirdPartyOptin != 0) _profile.ThirdPartyOptin = thirdPartyOptin;
            Save();
            return Get();
        }
    }

    private static void Save() => PlayerContext.MarkDirty();
}
