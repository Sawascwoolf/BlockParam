using System;
using System.Diagnostics;
using System.Threading;
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

    [Fact]
    public void HumorLine_is_revealed_once_the_reveal_delay_elapses()
    {
        var controller = new LoadingSplashController("Bulk Change", "Estimating time remaining…")
        {
            HumorRevealDelay = TimeSpan.FromMilliseconds(50),
        };
        controller.Show();

        // Not shown immediately — only after the slow-load threshold trips.
        controller.HumorLine.Should().BeEmpty();

        PollUntil(() => controller.HumorLine.Length > 0, TimeSpan.FromSeconds(3))
            .Should().BeTrue("the quip must appear after the reveal delay elapses");
        controller.HumorLine.Should().Be("Estimating time remaining…");

        controller.Close();
        controller.WaitForThreadExit(ExitTimeout).Should().BeTrue();
    }

    [Fact]
    public void HumorLine_stays_empty_when_the_splash_closes_before_the_delay()
    {
        var controller = new LoadingSplashController("Bulk Change", "Estimating time remaining…")
        {
            HumorRevealDelay = TimeSpan.FromSeconds(30),
        };
        controller.Show();
        controller.Close();
        controller.WaitForThreadExit(ExitTimeout).Should().BeTrue();

        // Fast open: closed long before the 30s delay, so the quip never fires.
        controller.HumorLine.Should().BeEmpty();
    }

    [Fact]
    public void No_humor_line_means_no_quip_even_after_the_delay()
    {
        var controller = new LoadingSplashController("Bulk Change")
        {
            HumorRevealDelay = TimeSpan.FromMilliseconds(50),
        };
        controller.Show();

        // No quip was supplied → the timer is never armed. Give the delay a
        // wide berth to prove nothing appears.
        PollUntil(() => controller.HumorLine.Length > 0, TimeSpan.FromMilliseconds(400))
            .Should().BeFalse();
        controller.HumorLine.Should().BeEmpty();

        controller.Close();
        controller.WaitForThreadExit(ExitTimeout).Should().BeTrue();
    }

    private static bool PollUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            // HumorLine is written on the splash dispatcher thread; barrier so
            // the read here is well-defined without relying on x86/x64 store
            // ordering.
            Thread.MemoryBarrier();
            if (condition()) return true;
            Thread.Sleep(15);
        }
        Thread.MemoryBarrier();
        return condition();
    }
}
