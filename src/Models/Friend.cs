using System.Text.Json.Serialization;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

/// <summary>
/// One Roblox friend of a manager account, shown on the Friends page. The list (id, names) loads
/// first; presence and the headshot are filled in afterwards, which is why those raise changes.
/// </summary>
public class Friend : ObservableObject
{
    public long UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";

    [JsonIgnore]
    public string DisplayNameOrUser =>
        !string.IsNullOrEmpty(DisplayName) ? DisplayName
        : !string.IsNullOrEmpty(Username) ? Username
        : L.T("Common.Unknown");

    [JsonIgnore] public string AtUsername => string.IsNullOrEmpty(Username) ? "" : "@" + Username;

    public override string ToString() => DisplayNameOrUser;

    private string _presence = PresenceStatus.Offline;
    /// <summary>Canonical presence, see <see cref="PresenceStatus"/>.</summary>
    public string Presence
    {
        get => _presence;
        set
        {
            if (!SetField(ref _presence, value ?? PresenceStatus.Offline)) return;
            RaiseStatus();
        }
    }

    private string _lastLocation = "";
    public string LastLocation
    {
        get => _lastLocation;
        set { if (SetField(ref _lastLocation, value ?? "")) OnPropertyChanged(nameof(StatusLine)); }
    }

    /// <summary>One line under the name: the game when in one, otherwise the presence.</summary>
    [JsonIgnore]
    public string StatusLine => Presence == PresenceStatus.InGame && !string.IsNullOrWhiteSpace(LastLocation)
        ? L.T("Presence.InGameAt", LastLocation)
        : PresenceStatus.Label(Presence);

    private string _headshotUrl = "";
    public string HeadshotUrl { get => _headshotUrl; set => SetField(ref _headshotUrl, value ?? ""); }

    private long _placeId;
    public long PlaceId { get => _placeId; set { if (SetField(ref _placeId, value)) RaiseStatus(); } }

    private long _rootPlaceId;
    public long RootPlaceId { get => _rootPlaceId; set { if (SetField(ref _rootPlaceId, value)) RaiseStatus(); } }

    private string? _jobId;
    public string? JobId { get => _jobId; set => SetField(ref _jobId, value); }

    [JsonIgnore] public bool IsInGame => Presence == PresenceStatus.InGame;
    [JsonIgnore] public bool IsOnline => PresenceStatus.IsOnline(Presence);

    /// <summary>True only when the friend is in a game Roblox lets us follow them into.</summary>
    [JsonIgnore] public bool CanJoin => IsInGame && (RootPlaceId > 0 || PlaceId > 0);

    [JsonIgnore] public bool IsSubPlace => IsInGame && PlaceId > 0 && RootPlaceId > 0 && PlaceId != RootPlaceId;

    [JsonIgnore] public string SubPlaceTip => IsSubPlace ? L.T("Presence.SubPlaceTip", PlaceId, RootPlaceId) : "";

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsInGame));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(IsSubPlace));
        OnPropertyChanged(nameof(SubPlaceTip));
    }

    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(SubPlaceTip));
        OnPropertyChanged(nameof(DisplayNameOrUser));
    }
}
