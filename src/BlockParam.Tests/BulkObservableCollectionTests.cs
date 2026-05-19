using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// #154 H3: ReplaceAll must swap the whole contents while raising exactly
/// ONE CollectionChanged (Reset) — not Clear() + N×Add (N+1 events). The
/// bound instance is unchanged so existing WPF bindings/selection keep
/// working; only the notification volume drops.
/// </summary>
public class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesSingleResetAndReplacesContents()
    {
        var col = new BulkObservableCollection<int> { 1, 2, 3 };

        var collectionEvents = new List<NotifyCollectionChangedEventArgs>();
        var propEvents = new List<string?>();
        col.CollectionChanged += (_, e) => collectionEvents.Add(e);
        ((INotifyPropertyChanged)col).PropertyChanged += (_, e) => propEvents.Add(e.PropertyName);

        col.ReplaceAll(new[] { 10, 20, 30, 40, 50 });

        col.Should().Equal(10, 20, 30, 40, 50);
        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset,
                "a bulk replace must collapse to one Reset, not N add events");
        propEvents.Should().Contain(nameof(BulkObservableCollection<int>.Count));
        propEvents.Should().Contain("Item[]");
    }

    [Fact]
    public void ReplaceAll_FromEmpty_AndToEmpty_Work()
    {
        var col = new BulkObservableCollection<string>();

        col.ReplaceAll(new[] { "a", "b" });
        col.Should().Equal("a", "b");

        var events = new List<NotifyCollectionChangedEventArgs>();
        col.CollectionChanged += (_, e) => events.Add(e);
        col.ReplaceAll(new string[0]);

        col.Should().BeEmpty();
        events.Should().ContainSingle().Which.Action
            .Should().Be(NotifyCollectionChangedAction.Reset);
    }
}
