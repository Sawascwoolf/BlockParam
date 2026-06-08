using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BlockParam.UI;
using BlockParam.UI.Controls.PillMultiSelect;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class EnumerateAllPillsTests
{
    [UIFact]
    public void Finds_pills_in_synthetic_visual_tree()
    {
        var panel = new StackPanel();
        var pill1 = new PillMultiSelect();
        var pill2 = new PillMultiSelect();
        panel.Children.Add(pill1);
        panel.Children.Add(pill2);
        ForceLayout(panel);

        var pills = BulkChangeDialog.EnumerateAllPills(panel).ToList();

        pills.Should().HaveCount(2);
        pills.Should().Contain(pill1);
        pills.Should().Contain(pill2);
    }

    [UIFact]
    public void Returns_empty_when_no_pills()
    {
        var panel = new StackPanel();
        ForceLayout(panel);

        var pills = BulkChangeDialog.EnumerateAllPills(panel).ToList();

        pills.Should().BeEmpty();
    }

    [UIFact]
    public void Finds_nested_pills()
    {
        var outer = new StackPanel();
        var inner = new StackPanel();
        var pill = new PillMultiSelect();
        inner.Children.Add(pill);
        outer.Children.Add(inner);
        ForceLayout(outer);

        var pills = BulkChangeDialog.EnumerateAllPills(outer).ToList();

        pills.Should().ContainSingle()
            .Which.Should().BeSameAs(pill);
    }

    [UIFact]
    public void Does_not_descend_into_pill_children()
    {
        var panel = new StackPanel();
        var pill = new PillMultiSelect();
        panel.Children.Add(pill);
        ForceLayout(panel);

        var pills = BulkChangeDialog.EnumerateAllPills(panel).ToList();

        pills.Should().ContainSingle("the pill itself is yielded, " +
            "but its internal visual tree is not traversed for nested pills");
    }

    private static void ForceLayout(FrameworkElement element)
    {
        element.Measure(new Size(800, 600));
        element.Arrange(new Rect(0, 0, 800, 600));
        element.UpdateLayout();
    }
}
