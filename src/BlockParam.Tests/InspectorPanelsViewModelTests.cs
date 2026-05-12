using System.Collections.Generic;
using System.ComponentModel;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the inspector-panel slice (#80 slice 1).
/// </summary>
public class InspectorPanelsViewModelTests
{
    [Fact]
    public void Defaults_InspectorExpanded_AllSectionsExpanded()
    {
        var vm = new InspectorPanelsViewModel();

        vm.IsInspectorCollapsed.Should().BeFalse();
        vm.IsInspectorExpanded.Should().BeTrue();
        vm.IsBulkEditExpanded.Should().BeTrue();
        vm.IsBulkPreviewExpanded.Should().BeTrue();
        vm.IsPendingExpanded.Should().BeTrue();
        vm.IsIssuesExpanded.Should().BeTrue();
    }

    [Fact]
    public void ToggleInspector_FlipsBothCollapsedAndExpanded()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.ToggleInspectorCommand.Execute(null);

        vm.IsInspectorCollapsed.Should().BeTrue();
        vm.IsInspectorExpanded.Should().BeFalse();
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsInspectorCollapsed));
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsInspectorExpanded));
    }

    [Fact]
    public void ToggleInspector_TwicePerToggle_ReturnsToOriginal()
    {
        var vm = new InspectorPanelsViewModel();

        vm.ToggleInspectorCommand.Execute(null);
        vm.ToggleInspectorCommand.Execute(null);

        vm.IsInspectorCollapsed.Should().BeFalse();
        vm.IsInspectorExpanded.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(InspectorPanelsViewModel.IsBulkEditExpanded))]
    [InlineData(nameof(InspectorPanelsViewModel.IsBulkPreviewExpanded))]
    [InlineData(nameof(InspectorPanelsViewModel.IsPendingExpanded))]
    [InlineData(nameof(InspectorPanelsViewModel.IsIssuesExpanded))]
    public void SectionFlag_SettingSameValue_DoesNotRaisePropertyChanged(string name)
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        // Defaults are true; setting true again must be a no-op.
        SetSectionFlag(vm, name, true);

        raised.Should().NotContain(name);
    }

    [Fact]
    public void ToggleBulkEdit_FlipsAndRaisesProperty()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.ToggleBulkEditCommand.Execute(null);

        vm.IsBulkEditExpanded.Should().BeFalse();
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsBulkEditExpanded));
    }

    [Fact]
    public void ToggleBulkPreview_FlipsAndRaisesProperty()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.ToggleBulkPreviewCommand.Execute(null);

        vm.IsBulkPreviewExpanded.Should().BeFalse();
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsBulkPreviewExpanded));
    }

    [Fact]
    public void TogglePending_FlipsAndRaisesProperty()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.TogglePendingCommand.Execute(null);

        vm.IsPendingExpanded.Should().BeFalse();
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsPendingExpanded));
    }

    [Fact]
    public void ToggleIssues_FlipsAndRaisesProperty()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.ToggleIssuesCommand.Execute(null);

        vm.IsIssuesExpanded.Should().BeFalse();
        raised.Should().Contain(nameof(InspectorPanelsViewModel.IsIssuesExpanded));
    }

    [Fact]
    public void IsInspectorCollapsed_SettingSameValue_DoesNotRaise()
    {
        var vm = new InspectorPanelsViewModel();
        var raised = CapturePropertyChanges(vm);

        vm.IsInspectorCollapsed = false; // already the default

        raised.Should().BeEmpty();
    }

    private static List<string?> CapturePropertyChanges(INotifyPropertyChanged vm)
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static void SetSectionFlag(InspectorPanelsViewModel vm, string name, bool value)
    {
        switch (name)
        {
            case nameof(InspectorPanelsViewModel.IsBulkEditExpanded):
                vm.IsBulkEditExpanded = value; break;
            case nameof(InspectorPanelsViewModel.IsBulkPreviewExpanded):
                vm.IsBulkPreviewExpanded = value; break;
            case nameof(InspectorPanelsViewModel.IsPendingExpanded):
                vm.IsPendingExpanded = value; break;
            case nameof(InspectorPanelsViewModel.IsIssuesExpanded):
                vm.IsIssuesExpanded = value; break;
        }
    }
}
