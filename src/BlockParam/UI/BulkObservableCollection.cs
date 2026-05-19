using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BlockParam.UI;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with a bulk <see cref="ReplaceAll"/>
/// that raises a single <see cref="NotifyCollectionChangedAction.Reset"/>
/// instead of one <c>CollectionChanged</c> per item.
///
/// <para>
/// #154 H3: the flat member list was rebuilt with <c>Clear()</c> + N×
/// <c>Add()</c>, firing N+1 <c>CollectionChanged</c> events through WPF's
/// binding engine on every open, every filter change, every search keystroke
/// and every pending-edit mutation. For a 10 000-element array that is
/// 10 001 events per rebuild. <c>ReplaceAll</c> mutates the backing
/// <see cref="Collection{T}.Items"/> list directly (no per-item events) then
/// raises exactly one Reset — the same notification <c>Clear()</c> already
/// raised, so the bound instance is unchanged and no binding/selection
/// re-plumbing is required.
/// </para>
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        for (int i = 0; i < items.Count; i++)
            Items.Add(items[i]);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
