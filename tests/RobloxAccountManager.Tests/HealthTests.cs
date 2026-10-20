using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class StrayPolicyTests
{
    private static readonly TimeSpan S = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_windowless_process_one_of_our_clients_started_is_closed_within_seconds()
    {
        Assert.False(StrayPolicy.ShouldClose(spawnedByOurs: true, hasWindow: false, 2 * S, StrayCheck.Pass));
        Assert.True(StrayPolicy.ShouldClose(spawnedByOurs: true, hasWindow: false, 3 * S, StrayCheck.Pass));
        Assert.True(StrayPolicy.ShouldClose(spawnedByOurs: true, hasWindow: false, TimeSpan.Zero, StrayCheck.BeforeLaunch));
    }

    [Fact]
    public void A_process_with_a_window_is_never_closed()
    {
        foreach (var check in Enum.GetValues<StrayCheck>())
            Assert.False(StrayPolicy.ShouldClose(spawnedByOurs: true, hasWindow: true, TimeSpan.FromHours(1), check));
    }

    [Fact]
    public void A_client_started_by_hand_gets_time_for_its_window()
    {
        Assert.False(StrayPolicy.ShouldClose(false, false, 10 * S, StrayCheck.BeforeLaunch));
        Assert.True(StrayPolicy.ShouldClose(false, false, 20 * S, StrayCheck.BeforeLaunch));
        Assert.False(StrayPolicy.ShouldClose(false, false, 59 * S, StrayCheck.Pass));
        Assert.True(StrayPolicy.ShouldClose(false, false, 60 * S, StrayCheck.Pass));
    }

    [Fact]
    public void After_a_launch_was_handed_away_every_windowless_stray_goes()
        => Assert.True(StrayPolicy.ShouldClose(false, false, S, StrayCheck.AfterFastExit));
}

public class LaunchHealthTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 3, 14, 0, DateTimeKind.Utc);

    [Fact]
    public void Launches_dying_in_a_row_get_a_cleanup_then_a_full_reset()
    {
        var h = new LaunchHealth();
        Assert.Equal(RobloxRepair.None, h.Failed(T0));
        Assert.Equal(RobloxRepair.Cleanup, h.Failed(T0));
        Assert.Equal(RobloxRepair.None, h.Failed(T0));
        Assert.Equal(RobloxRepair.FullReset, h.Failed(T0));
    }

    [Fact]
    public void A_launch_reaching_its_game_ends_it()
    {
        var h = new LaunchHealth();
        h.Failed(T0); h.Failed(T0); h.Failed(T0);
        h.Succeeded();
        Assert.Equal(RobloxRepair.None, h.Failed(T0));
        Assert.Equal(1, h.FailuresInARow);
    }

    [Fact]
    public void A_reset_that_did_not_help_is_repeated_later_but_never_given_up()
    {
        var h = new LaunchHealth();
        for (int i = 0; i < 4; i++) h.Failed(T0);                // first reset

        for (int i = 0; i < 6; i++) Assert.NotEqual(RobloxRepair.FullReset, h.Failed(T0.AddMinutes(5)));
        Assert.Equal(RobloxRepair.FullReset, h.Failed(T0.AddMinutes(10)));

        var t = T0.AddMinutes(10);
        for (int round = 0; round < 5; round++)
        {
            t += LaunchHealth.ResetSpacing(h.ResetsInARow);
            for (int i = 0; i < 3; i++) h.Failed(t);
            Assert.Equal(RobloxRepair.FullReset, h.Failed(t));
        }
        Assert.Equal(TimeSpan.FromMinutes(30), LaunchHealth.ResetSpacing(h.ResetsInARow));
    }
}
