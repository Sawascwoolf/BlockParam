using System;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class LoadingSplashControllerTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Show_creates_the_window_then_Close_tears_down_the_thread()
    {
        var controller = new LoadingSplashController("Bulk Change");

        controller.Show();

        // Show() blocks until the splash thread has created + shown the
        // window, so this is deterministic without any sleep/poll.
        controller.WindowShown.Should().BeTrue();

        // Pushing step text while the window is up must not throw.
        controller.Report("Exporting DB_X…");
        controller.SetCounter("(2 of 3)");

        controller.Close();

        controller.WaitForThreadExit(ExitTimeout).Should().BeTrue(
            "Close() must shut the splash dispatcher down so the thread exits");
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
        controller.WaitForThreadExit(ExitTimeout).Should().BeTrue();
    }

    [Fact]
    public void Report_and_SetCounter_after_close_are_safe_no_ops()
    {
        var controller = new LoadingSplashController("Bulk Change");
        controller.Show();
        controller.Close();
        controller.WaitForThreadExit(ExitTimeout);

        Action act = () =>
        {
            controller.Report("late");
            controller.SetCounter("(9 of 9)");
        };

        act.Should().NotThrow();
    }
}
