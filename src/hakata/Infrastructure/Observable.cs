using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hakata.Infrastructure;

/// <summary>Base view model: raises several computed properties after the state they derive from changed.</summary>
public abstract class Observable : ObservableObject
{
    protected void Notify(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }
}

/// <summary>In-place <see cref="ObservableCollection{T}"/> sync for lists bound to a selection.</summary>
public static class CollectionSync
{
    /// <summary>
    /// Updates <paramref name="collection"/> in place to match <paramref name="wanted"/>: Clear()+Add fires
    /// a Reset, and Avalonia nulls a bound ListBox/ComboBox SelectedItem on a Reset even when the same objects
    /// come back (#0049 class of bug). Item equality is the collection's own (records compare by value).
    /// </summary>
    public static void InPlace<T>(ObservableCollection<T> collection, IReadOnlyList<T> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = collection.IndexOf(item);
            if (at == i) continue;
            // value-equal duplicates in wanted can leave the first match already placed while the
            // collection is exactly full: Move with newIndex == Count throws, so it only runs in
            // range, and an insert past the end clamps to an append the same way; a match at < i is
            // already consumed by an earlier slot, so the duplicate is inserted, not moved
            if (at < 0 || at < i) collection.Insert(Math.Min(i, collection.Count), item);
            else if (i < collection.Count) collection.Move(at, i);
        }
        while (collection.Count > wanted.Count) collection.RemoveAt(collection.Count - 1);
    }
}
