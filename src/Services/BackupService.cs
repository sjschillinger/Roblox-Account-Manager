using System.IO;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Portable, password-encrypted account export/import.
///
/// The normal store encrypts cookies with DPAPI, which is bound to the current
/// Windows user — copying accounts.dat to another PC loses everything. A backup
/// instead serialises the raw cookies and re-encrypts the whole blob with a
/// user-chosen password (AES-256-GCM), so it can be restored anywhere.
///
/// File layout: magic "RAMBK1\n" + Crypto.EncryptPassword(json).
/// </summary>
public static class BackupService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RAMBK1\n");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private class Entry
    {
        public string Cookie { get; set; } = "";
        /// <summary>
        /// Carried so a restore lands a usable account straight away. Almost everything keys off
        /// the user id — presence, RAP, premium, launch attribution — and a restored account
        /// without one is inert until it is re-validated. Absent in pre-1.5 backups, which
        /// deserialize to 0 and are backfilled by the cookie check the restore runs.
        /// </summary>
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Group { get; set; } = "Default";
        public string Description { get; set; } = "";
        public string TotpSecret { get; set; } = "";
        public string BrowserTrackerId { get; set; } = "";

        // v2.0+: carried so a restore brings the whole account back, not just its session.
        public string Color { get; set; } = "";
        public bool IsFavorite { get; set; }
        public bool AutoRejoin { get; set; }
        public string FFlags { get; set; } = "";
        public string ProxyUrl { get; set; } = "";
        public DateTime? AddedUtc { get; set; }
        public DateTime? CookieUpdatedUtc { get; set; }
    }

    public static void Export(IEnumerable<Account> accounts, string path, string password)
    {
        var entries = accounts.Select(a => new Entry
        {
            Cookie = a.Cookie,
            UserId = a.UserId,
            Username = a.Username,
            DisplayName = a.DisplayName,
            Alias = a.Alias,
            Group = a.Group,
            Description = a.Description,
            TotpSecret = a.TotpSecret,
            BrowserTrackerId = a.BrowserTrackerId,
            Color = a.Color,
            IsFavorite = a.IsFavorite,
            AutoRejoin = a.AutoRejoin,
            FFlags = a.FFlags,
            ProxyUrl = a.ProxyUrl,
            AddedUtc = a.AddedUtc,
            CookieUpdatedUtc = a.CookieUpdatedUtc,
        }).ToList();

        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, JsonOpts));
        byte[] enc = Crypto.EncryptPassword(json, password);

        // Atomic write: build the whole file in a temp path first, then move it into place,
        // so a crash / disk-full mid-write can't leave a truncated (unrecoverable) backup —
        // especially when overwriting an existing export at the same path.
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(Magic);
            fs.Write(enc);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Decrypts a backup and returns its accounts (cookies in the clear, in memory only).
    /// Throws on wrong password / not a backup file.
    /// </summary>
    public static List<Account> Import(string path, string password)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < Magic.Length || !all.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException(L.T("Backup.NotABackup"));

        byte[] enc = all[Magic.Length..];
        byte[] plain = Crypto.Decrypt(enc, password);   // throws on wrong password / tamper
        var entries = JsonSerializer.Deserialize<List<Entry>>(Encoding.UTF8.GetString(plain)) ?? new();

        return entries.Select(e => new Account
        {
            Cookie = e.Cookie,
            UserId = e.UserId,
            Username = e.Username,
            DisplayName = e.DisplayName,
            Alias = e.Alias,
            Group = string.IsNullOrWhiteSpace(e.Group) ? "Default" : e.Group,
            Description = e.Description,
            TotpSecret = e.TotpSecret,
            BrowserTrackerId = e.BrowserTrackerId,
            Color = e.Color ?? "",
            IsFavorite = e.IsFavorite,
            AutoRejoin = e.AutoRejoin,
            FFlags = e.FFlags ?? "",
            ProxyUrl = e.ProxyUrl ?? "",
            AddedUtc = e.AddedUtc ?? DateTime.UtcNow,
            CookieUpdatedUtc = e.CookieUpdatedUtc,
        }).ToList();
    }
}
