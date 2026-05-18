using System;
using System.Collections.Generic;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class LoadingSplashViewModelTests
{
    private static List<string> TrackChanges(LoadingSplashViewModel vm)
    {
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        return changed;
    }

    [Fact]
    public void StatusText_setter_raises_PropertyChanged()
    {
        var vm = new LoadingSplashViewModel();
        var changed = TrackChanges(vm);

        vm.StatusText = "Exporting DB_X…";

        vm.StatusText.Should().Be("Exporting DB_X…");
        changed.Should().Contain(nameof(LoadingSplashViewModel.StatusText));
    }

    [Fact]
    public void CounterText_setter_raises_PropertyChanged()
    {
        var vm = new LoadingSplashViewModel();
        var changed = TrackChanges(vm);

        vm.CounterText = "(2 of 3)";

        vm.CounterText.Should().Be("(2 of 3)");
        changed.Should().Contain(nameof(LoadingSplashViewModel.CounterText));
    }

    [Fact]
    public void CounterText_null_is_coerced_to_empty()
    {
        var vm = new LoadingSplashViewModel { CounterText = "(1 of 2)" };

        vm.CounterText = null!;

        vm.CounterText.Should().BeEmpty();
    }

    [Fact]
    public void Title_defaults_to_empty_and_is_settable()
    {
        var vm = new LoadingSplashViewModel();
        vm.Title.Should().BeEmpty();

        vm.Title = "Bulk Change";

        vm.Title.Should().Be("Bulk Change");
    }

    [Fact]
    public void Setting_same_value_does_not_raise_PropertyChanged()
    {
        var vm = new LoadingSplashViewModel { StatusText = "Parsing…" };
        var changed = TrackChanges(vm);

        vm.StatusText = "Parsing…";

        changed.Should().NotContain(nameof(LoadingSplashViewModel.StatusText));
    }
}
