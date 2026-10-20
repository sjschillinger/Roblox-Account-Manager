using System.Web;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class ClientIdentityTests
{
    private const string Url = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    [Fact]
    public void Reads_the_protocol_form_the_manager_launches_with()
    {
        string url = $"{Url}?request=RequestPrivateGame&browserTrackerId=111270734572&placeId=131346454575416&accessCode=&linkCode=12345&isPlayTogetherGame=false";
        string line = "\"C:\\Roblox\\RobloxPlayerBeta.exe\" roblox-player:1+launchmode:play+gameinfo:TICKET+launchtime:1791839946991"
                    + $"+placelauncherurl:{HttpUtility.UrlEncode(url)}+browsertrackerid:111270734572+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

        var id = ClientIdentity.Parse(line)!;

        Assert.Equal("111270734572", id.Tracker);
        Assert.Equal(1791839946991, id.LaunchTime);
        Assert.Equal(JoinKind.PrivateServer, id.Target!.Kind);
        Assert.Equal(131346454575416, id.Target.PlaceId);
        Assert.Equal("12345", id.Target.LinkCode);
    }

    [Fact]
    public void Reads_the_argument_form()
    {
        string line = "\"C:\\Roblox\\RobloxPlayerBeta.exe\" --app -t TICKET "
                    + $"-j {Url}?request=RequestGameJob&browserTrackerId=1&placeId=5&gameId=abc-123&isPlayTogetherGame=false "
                    + "-b 103307661389 --launchtime=1791839962442";

        var id = ClientIdentity.Parse(line)!;

        Assert.Equal("103307661389", id.Tracker);
        Assert.Equal(1791839962442, id.LaunchTime);
        Assert.Equal(new JoinTarget(5, "abc-123"), id.Target);
    }

    [Fact]
    public void Reads_a_followed_player_and_the_home_screen()
    {
        var follow = ClientIdentity.Parse($"x +launchtime:1+placelauncherurl:{HttpUtility.UrlEncode(Url + "?request=RequestFollowUser&userId=42")}+browsertrackerid:7+");
        Assert.Equal(JoinKind.FollowUser, follow!.Target!.Kind);
        Assert.Equal(42, follow.Target.FollowUserId);

        var home = ClientIdentity.Parse("roblox-player:1+launchmode:app+gameinfo:T+launchtime:9+browsertrackerid:7+robloxLocale:en_us");
        Assert.Equal("7", home!.Tracker);
        Assert.Null(home.Target);
    }

    [Fact]
    public void A_client_started_without_a_tracker_id_is_nobody_s()
    {
        Assert.Null(ClientIdentity.Parse("\"C:\\Roblox\\RobloxPlayerBeta.exe\" --app"));
        Assert.Null(ClientIdentity.Parse(null));
    }
}

public class RecoverySlotTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);
    private static readonly JoinTarget Private = new(131346454575416, LinkCode: "code");

    private static RecoverySlot Running(out DateTime now, JoinTarget? target = null, int pid = 100)
    {
        var slot = new RecoverySlot(1, target ?? Private, null);
        slot.Launched(target ?? Private, null, 1, T0);
        slot.Found(pid);
        now = T0;
        return slot;
    }

    private static ClientObservation Client(int pid, TimeSpan uptime, ClientLogState? log, bool window = true)
        => new(pid, uptime, window, log, true);

    private static ClientLogState InGame(DateTime at)
    {
        var log = new ClientLogState(at);
        log.Feed("! Joining game 'x' place 1", at);
        log.Feed("[FLog::Network] Replicator created", at);
        return log;
    }

    private static RecoveryDecision Tick(RecoverySlot slot, ClientObservation c, DateTime now, bool canLaunch = true)
        => slot.Tick(c, now, watchDisconnects: true, Wait, canLaunch);

    [Fact]
    public void A_launch_whose_client_never_appears_is_retried_not_forgotten()
    {
        // Oct 12, 06:36 and 20:01: "no client could be attributed" left the account with nothing.
        var slot = new RecoverySlot(1, Private, null);
        slot.Launched(Private, null, 1, T0);

        Assert.Equal(RecoveryAction.None, Tick(slot, ClientObservation.None, T0.AddSeconds(90)).Action);
        var booked = Tick(slot, ClientObservation.None, T0.AddMinutes(2));
        Assert.Equal(RecoveryAction.Booked, booked.Action);

        var relaunch = Tick(slot, ClientObservation.None, T0.AddMinutes(2).AddSeconds(5));
        Assert.Equal(RecoveryAction.Launch, relaunch.Action);
        Assert.Equal(Private, relaunch.Target);
    }

    [Fact]
    public void Clients_that_die_instantly_are_retried_ever_more_slowly_but_forever()
    {
        // Oct 12, 20:01-21:18: every new client died within 0-2 s.
        var slot = Running(out var now);
        var delays = new List<TimeSpan>();
        for (int i = 0; i < 10; i++)
        {
            delays.Add(slot.Lost(now));
            now += delays[^1];
            Assert.Equal(RecoveryAction.Launch, Tick(slot, ClientObservation.None, now).Action);
            slot.BeginLaunch(now);
            slot.Launched(Private, null, i + 2, now);
            slot.Found(200 + i);
            now += TimeSpan.FromSeconds(2);
        }

        Assert.All(delays.Take(RejoinBackoff.FreeRejoins), d => Assert.True(d < TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromMinutes(2), delays[3]);
        Assert.Equal(TimeSpan.FromMinutes(15), delays[^1]);
    }

    [Fact]
    public void Ten_minutes_in_game_ends_the_streak()
    {
        var slot = Running(out var now);
        for (int i = 0; i < 5; i++) slot.Lost(now);   // a bad stretch
        slot.Launched(Private, null, 9, now);
        slot.Found(300);

        var log = InGame(now);
        Tick(slot, Client(300, TimeSpan.Zero, log), now);
        Tick(slot, Client(300, TimeSpan.FromMinutes(11), log), now.AddMinutes(11));
        Assert.Equal(0, slot.Streak);
        Assert.True(slot.Lost(now.AddMinutes(12)) < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_client_that_only_runs_without_reaching_its_game_does_not_end_the_streak()
    {
        var slot = Running(out var now);
        for (int i = 0; i < 5; i++) slot.Lost(now);
        slot.Launched(Private, null, 9, now);
        slot.Found(300);

        var loading = new ClientLogState(now);
        loading.Feed("! Joining game 'x' place 1", now);
        Tick(slot, Client(300, TimeSpan.FromMinutes(7), loading), now.AddMinutes(7));
        Assert.Equal(5, slot.Streak);
    }

    [Fact]
    public void A_disconnect_waits_for_Roblox_s_own_reconnect_then_replaces_the_client()
    {
        var slot = Running(out var now);
        var log = InGame(now);
        log.Feed("[FLog::Network] Time to disconnect replication data: 5.17", now.AddMinutes(30));

        Assert.Equal(RecoveryAction.None, Tick(slot, Client(100, TimeSpan.FromMinutes(30), log), now.AddMinutes(30.5)).Action);
        var d = Tick(slot, Client(100, TimeSpan.FromMinutes(31), log), now.AddMinutes(31));
        Assert.Equal(RecoveryAction.Replace, d.Action);
        Assert.Contains("disconnected", d.Why);
    }

    [Fact]
    public void A_shut_down_server_is_replaced_without_waiting()
    {
        var slot = Running(out var now);
        var log = InGame(now);
        log.Feed("[FLog::Network] Time to disconnect replication data: 5.17", now.AddMinutes(30));
        log.Feed("Disconnection Notification. Reason: 274", now.AddMinutes(30));

        Assert.Equal(RecoveryAction.Replace, Tick(slot, Client(100, TimeSpan.FromMinutes(30), log), now.AddMinutes(30)).Action);
    }

    [Fact]
    public void A_client_stuck_loading_is_replaced_after_four_minutes()
    {
        var slot = Running(out var now);
        var log = new ClientLogState(now);
        log.Feed("! Joining game 'x' place 1", now);

        Assert.Equal(RecoveryAction.None, Tick(slot, Client(100, TimeSpan.FromMinutes(3), log), now.AddMinutes(3)).Action);
        Assert.Equal(RecoveryAction.Replace, Tick(slot, Client(100, TimeSpan.FromMinutes(4), log), now.AddMinutes(4)).Action);
    }

    [Fact]
    public void A_fresh_client_without_a_log_of_its_own_is_replaced_when_other_logs_are_read()
    {
        var slot = Running(out var now);
        ClientObservation NoLog(double minutes, bool working) => new(100, TimeSpan.FromMinutes(minutes), true, null, true, working);

        Assert.Equal(RecoveryAction.None, Tick(slot, NoLog(4, true), now.AddMinutes(4)).Action);
        Assert.Equal(RecoveryAction.None, Tick(slot, NoLog(6, false), now.AddMinutes(6)).Action);    // nobody's log is read: no grounds
        Assert.Equal(RecoveryAction.Replace, Tick(slot, NoLog(5, true), now.AddMinutes(5)).Action);
        Assert.Equal(RecoveryAction.None, Tick(slot, NoLog(31 * 60, true), now.AddMinutes(31 * 60)).Action);   // outlived a manager restart
    }

    [Fact]
    public void A_reset_of_Roblox_relaunches_after_its_delay_without_adding_to_the_streak()
    {
        var slot = Running(out var now);
        slot.Reset(now, TimeSpan.FromSeconds(15));

        Assert.Equal(0, slot.Streak);
        Assert.Equal(RecoveryAction.None, Tick(slot, ClientObservation.None, now.AddSeconds(10)).Action);
        Assert.Equal(RecoveryAction.Launch, Tick(slot, ClientObservation.None, now.AddSeconds(15)).Action);
    }

    [Fact]
    public void A_client_without_a_window_is_replaced_after_three_minutes()
    {
        var slot = Running(out var now);
        Assert.Equal(RecoveryAction.None, Tick(slot, Client(100, TimeSpan.FromMinutes(2), null, window: false), now.AddMinutes(2)).Action);
        Assert.Equal(RecoveryAction.Replace, Tick(slot, Client(100, TimeSpan.FromMinutes(3), null, window: false), now.AddMinutes(3)).Action);
    }

    [Fact]
    public void Joined_from_elsewhere_closes_the_client_and_stops()
    {
        var slot = Running(out var now);
        var log = InGame(now);
        log.Feed("[FLog::Network] Time to disconnect replication data: 1.0", now.AddMinutes(5));
        log.Feed("Disconnection Notification. Reason: 273", now.AddMinutes(5));

        Assert.Equal(RecoveryAction.CloseAndStop, Tick(slot, Client(100, TimeSpan.FromMinutes(5), log), now.AddMinutes(5)).Action);
    }

    [Fact]
    public void Nothing_is_launched_while_any_client_of_the_account_is_still_alive()
    {
        var slot = Running(out var now);
        slot.Lost(now);   // closing it
        var closing = new ClientObservation(0, TimeSpan.Zero, false, null, AnyProcess: true);

        Assert.Equal(RecoveryAction.None, Tick(slot, closing, now.AddMinutes(1)).Action);
        Assert.Equal(RecoveryAction.Launch, Tick(slot, ClientObservation.None, now.AddMinutes(1)).Action);
    }

    [Fact]
    public void One_launch_at_a_time()
    {
        var slot = Running(out var now);
        slot.Lost(now);
        Assert.Equal(RecoveryAction.None, Tick(slot, ClientObservation.None, now.AddMinutes(1), canLaunch: false).Action);
        Assert.Equal(RecoveryAction.Launch, Tick(slot, ClientObservation.None, now.AddMinutes(1)).Action);
    }

    [Fact]
    public void The_first_relaunch_goes_back_to_the_same_server_later_ones_to_any_server()
    {
        var server = new JoinTarget(5, "job-1");
        var slot = Running(out var now, server);

        slot.Lost(now);
        Assert.Equal(server, Tick(slot, ClientObservation.None, now.AddMinutes(1)).Target);

        slot.Launched(server, null, 2, now);
        slot.Found(101);
        slot.Lost(now.AddMinutes(2));
        Assert.Equal(server.WithoutServer(), Tick(slot, ClientObservation.None, now.AddMinutes(3)).Target);
    }

    [Fact]
    public void Someone_launching_the_account_cancels_its_waiting_relaunch()
    {
        var slot = Running(out var now);
        for (int i = 0; i < 6; i++) slot.Lost(now);
        Assert.NotNull(slot.RelaunchAtUtc);

        slot.Launched(Private, null, 50, now.AddMinutes(1));
        Assert.Null(slot.RelaunchAtUtc);
        Assert.True(slot.Launching);
    }

    [Fact]
    public void A_client_that_would_not_close_is_not_replaced_again_until_its_backoff_passes()
    {
        var slot = Running(out var now);
        var log = InGame(now);
        log.Feed("[FLog::Network] Time to disconnect replication data: 5.17", now);
        var stuck = Client(100, TimeSpan.FromMinutes(5), log);

        Assert.Equal(RecoveryAction.Replace, Tick(slot, stuck, now.AddMinutes(1)).Action);
        slot.Lost(now.AddMinutes(1));
        slot.Found(100);                                  // the close failed
        slot.HoldReplace(now.AddMinutes(3));
        Assert.Equal(RecoveryAction.None, Tick(slot, stuck, now.AddMinutes(2)).Action);
        Assert.Equal(RecoveryAction.Replace, Tick(slot, stuck, now.AddMinutes(3)).Action);
    }

    [Fact]
    public void A_restart_that_cannot_relaunch_books_a_retry()
    {
        var slot = Running(out var now);
        slot.Handover(now);
        Assert.True(slot.Launching);
        Assert.Equal(RecoveryAction.None, Tick(slot, ClientObservation.None, now.AddMinutes(1)).Action);   // the restart is busy

        slot.LaunchFailed(now.AddMinutes(1));
        Assert.Equal(RecoveryAction.Launch, Tick(slot, ClientObservation.None, now.AddMinutes(2)).Action);
    }

    [Fact]
    public void A_relaunch_that_never_reports_back_does_not_block_forever()
    {
        var slot = Running(out var now);
        slot.Lost(now);
        slot.BeginLaunch(now);
        Assert.Equal(RecoveryAction.None, Tick(slot, ClientObservation.None, now.AddMinutes(9)).Action);
        Assert.Equal(RecoveryAction.Booked, Tick(slot, ClientObservation.None, now.AddMinutes(10)).Action);
        Assert.False(slot.Launching);
    }

    [Fact]
    public void A_client_gone_without_a_reported_exit_is_relaunched()
    {
        var slot = Running(out var now);
        Assert.Equal(RecoveryAction.Booked, Tick(slot, ClientObservation.None, now.AddMinutes(5)).Action);
        Assert.Equal(0, slot.Pid);
        Assert.NotNull(slot.RelaunchAtUtc);
    }
}

public class EventLineTests
{
    private static readonly DateTime T = new(2026, 10, 12, 20, 0, 14, DateTimeKind.Utc);

    [Fact]
    public void One_event_is_one_readable_line()
    {
        string line = EventLine.Format(T, "EXIT", "JapasBiggestFan (117)",
            new (string, object?)[] { ("pid", 30104), ("uptime", "8s"), ("closedBy", "nobody (crash)"), ("skip", null), ("line", "a\nb") });

        Assert.Equal("2026-10-12 20:00:14Z  EXIT      JapasBiggestFan (117)  pid=30104  uptime=8s  closedBy=\"nobody (crash)\"  line=\"a b\"", line);
    }

    [Fact]
    public void Durations_are_short()
    {
        Assert.Equal("8s", EventLine.Duration(TimeSpan.FromSeconds(8)));
        Assert.Equal("3m 05s", EventLine.Duration(TimeSpan.FromSeconds(185)));
        Assert.Equal("14h 12m", EventLine.Duration(TimeSpan.FromMinutes(852)));
    }

    [Fact]
    public void Keeps_the_chosen_number_of_days()
    {
        var files = Enumerable.Range(0, 10).Select(i => (EventLine.FileFor(T.AddDays(-i)), 1000L)).Append(("notes.txt", 5L));
        var delete = EventLine.ToDelete(files, T, keepDays: 7, capBytes: long.MaxValue);

        Assert.Equal(new[] { "events-2026-10-03.log", "events-2026-10-04.log", "events-2026-10-05.log" }, delete.OrderBy(n => n));
    }

    [Fact]
    public void The_size_cap_drops_the_oldest_days_but_never_today()
    {
        var files = new[] { (EventLine.FileFor(T.AddDays(-2)), 30L), (EventLine.FileFor(T.AddDays(-1)), 30L), (EventLine.FileFor(T), 30L) };
        Assert.Equal(new[] { EventLine.FileFor(T.AddDays(-2)) }, EventLine.ToDelete(files, T, 30, capBytes: 60));
        Assert.Equal(new[] { EventLine.FileFor(T.AddDays(-2)), EventLine.FileFor(T.AddDays(-1)) }, EventLine.ToDelete(files, T, 30, capBytes: 10));
    }
}
