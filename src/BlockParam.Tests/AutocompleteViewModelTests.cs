using System.Collections.Generic;
using System.Linq;
using BlockParam.Services;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the autocomplete slice (#80 slice 3).
/// </summary>
public class AutocompleteViewModelTests
{
    private static AutocompleteSuggestion S(string value, string display = "", string? comment = null) =>
        new AutocompleteSuggestion(value, string.IsNullOrEmpty(display) ? value : display, comment);

    private static readonly IReadOnlyList<AutocompleteSuggestion> Sample = new[]
    {
        S("100", "Speed_Min", "Minimum"),
        S("200", "Speed_Max", "Maximum"),
        S("50",  "Pressure_Bar", "in bar"),
    };

    [Fact]
    public void Defaults_AreEmpty()
    {
        var vm = new AutocompleteViewModel();

        vm.Suggestions.Should().BeEmpty();
        vm.FilteredSuggestions.Should().BeEmpty();
        vm.SuggestionProvider.Should().BeNull();
        vm.HasFilteredSuggestions.Should().BeFalse();
        vm.SuppressSuggestions.Should().BeFalse();
    }

    [Fact]
    public void SetCandidates_PopulatesPool_ClearsFiltered()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, new GlobSuggestionProvider(Sample));

        vm.Suggestions.Should().HaveCount(3);
        vm.SuggestionProvider.Should().NotBeNull();
        vm.FilteredSuggestions.Should().BeEmpty("SetCandidates must not auto-open the overlay");
    }

    [Fact]
    public void ClearCandidates_ResetsEverything()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, new GlobSuggestionProvider(Sample));
        vm.ShowAll("");

        vm.ClearCandidates();

        vm.Suggestions.Should().BeEmpty();
        vm.FilteredSuggestions.Should().BeEmpty();
        vm.SuggestionProvider.Should().BeNull();
    }

    [Fact]
    public void ApplyFilter_EmptyPool_ProducesEmpty()
    {
        var vm = new AutocompleteViewModel();
        vm.ApplyFilter("anything");

        vm.FilteredSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_EmptyText_HidesOverlay()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ApplyFilter("");

        vm.FilteredSuggestions.Should().BeEmpty("empty text closes the overlay rather than showing all");
    }

    [Fact]
    public void ApplyFilter_AllTermsMustMatchSomewhere()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ApplyFilter("speed min");

        vm.FilteredSuggestions.Should().ContainSingle(s => s.Value == "100");
    }

    [Fact]
    public void ApplyFilter_MatchesCommentField()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ApplyFilter("bar");

        vm.FilteredSuggestions.Should().ContainSingle(s => s.Value == "50");
    }

    [Fact]
    public void ApplyFilter_IsCaseInsensitive()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ApplyFilter("SPEED");

        vm.FilteredSuggestions.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyFilter_Suppressed_AlwaysEmpty()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);
        vm.SuppressSuggestions = true;

        vm.ApplyFilter("speed");

        vm.FilteredSuggestions.Should().BeEmpty("suppression must hard-close the overlay");
    }

    [Fact]
    public void ShowAll_EmptyFilter_RendersAll()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ShowAll("");

        vm.FilteredSuggestions.Should().HaveCount(3);
        vm.HasFilteredSuggestions.Should().BeTrue();
    }

    [Fact]
    public void ShowAll_WithFilter_RendersMatchesOnly()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.ShowAll("max");

        vm.FilteredSuggestions.Should().ContainSingle(s => s.Value == "200");
    }

    [Fact]
    public void Toggle_OpensWhenClosed_ClosesWhenOpen()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        vm.Toggle("");
        vm.HasFilteredSuggestions.Should().BeTrue("first toggle opens");

        vm.Toggle("");
        vm.HasFilteredSuggestions.Should().BeFalse("second toggle closes");
    }

    [Fact]
    public void ClearFiltered_HidesOverlay_KeepsPool()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);
        vm.ShowAll("");

        vm.ClearFiltered();

        vm.FilteredSuggestions.Should().BeEmpty();
        vm.Suggestions.Should().HaveCount(3, "ClearFiltered must not touch the candidate pool");
    }

    [Fact]
    public void HasFilteredSuggestions_RaisesOnFilterChange()
    {
        var vm = new AutocompleteViewModel();
        vm.SetCandidates(Sample, null);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ShowAll("");

        raised.Should().Contain(nameof(AutocompleteViewModel.HasFilteredSuggestions));
    }

    [Fact]
    public void Match_Static_SameLogicAsInstance()
    {
        var instanceResult = new AutocompleteViewModel();
        instanceResult.SetCandidates(Sample, null);
        instanceResult.ShowAll("speed");

        var staticResult = AutocompleteViewModel.Match(Sample, "speed");

        staticResult.Select(s => s.Value).Should().BeEquivalentTo(
            instanceResult.FilteredSuggestions.Select(s => s.Value));
    }
}
