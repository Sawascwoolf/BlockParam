using System;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class LoadingSplashControllerTests
{
    [Fact]
    public void Close_before_show_delay_never_creates_a_window_and_thread_exits()
    {
        var controller = new LoadingSplashController("Bulk Change");

        controller.Show();
        // Caller thread reports a step before the (150 ms) window even appears —
        // must not throw and must not force the window into existence.
        controller.Report("Exporting DB_X…");
        controller.SetCounter("(1 of 2)");
        controller.Close();

        controller.WaitForThreadExit(TimeSpan.FromSeconds(5)).Should().BeTrue();
        controller.WindowShown.Should().BeFalse();
    }

    [Fact]
    public void Close_is_idempotent_and_safe_to_call_repeatedly()
    {
        var controller = new LoadingSplashController("Bulk Change");
        controller.Show();

        Action act = () =>
        {
            controller.Close();
            controller.Close();
            controller.Dispose();
        };

        act.Should().NotThrow();
        controller.WaitForThreadExit(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void Report_and_SetCounter_after_close_are_safe_no_ops()
    {
        var controller = new LoadingSplashController("Bulk Change");
        controller.Show();
        controller.Close();
        controller.WaitForThreadExit(TimeSpan.FromSeconds(5));

        Action act = () =>
        {
            controller.Report("late");
            controller.SetCounter("(9 of 9)");
        };

        act.Should().NotThrow();
    }
}
