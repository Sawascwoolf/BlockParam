using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Caches <see cref="PropertyDescriptor"/> lookups per <c>(Type, memberPath)</c>
/// pair so member-path resolution costs one reflection call per distinct
/// source type, not one per row. UI-thread-only; no locking required.
/// </summary>
/// <remarks>
/// Used by <see cref="MultiSelectItemSource"/> when <see cref="PillMultiSelect.DisplayMemberPath"/>
/// or <see cref="PillMultiSelect.AbbreviationMemberPath"/> is set. When a path
/// is null or empty the resolver falls back to a caller-supplied delegate (typically
/// <c>obj => obj.ToString()</c>) so hosts that don't set member paths get reasonable
/// output without any configuration.
/// </remarks>
public sealed class MemberPathResolver
{
    // Null-value sentinel: a (Type, path) pair whose property was not found.
    // Storing null in the dictionary lets us avoid repeated failed lookups.
    private readonly Dictionary<(Type, string), PropertyDescriptor?> _cache = new();

    /// <summary>
    /// Resolves <paramref name="source"/> to a string using
    /// <paramref name="memberPath"/>. When the path is null or empty, returns
    /// <paramref name="fallback"/>(<paramref name="source"/>). When the path
    /// is set but the property is not found, returns <see cref="string.Empty"/>.
    /// </summary>
    public string Resolve(object source, string? memberPath, Func<object, string> fallback)
    {
        if (string.IsNullOrEmpty(memberPath))
            return fallback(source);

        var descriptor = GetDescriptor(source.GetType(), memberPath!);
        if (descriptor == null)
            return string.Empty;

        var value = descriptor.GetValue(source);
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Returns the <see cref="PropertyDescriptor"/> for <paramref name="path"/>
    /// on <paramref name="type"/>, or null when the property is not found.
    /// The result is cached — repeated calls for the same pair are O(1).
    /// </summary>
    public bool TryGetDescriptor(Type type, string path, out PropertyDescriptor? descriptor)
    {
        descriptor = GetDescriptor(type, path);
        return descriptor != null;
    }

    private PropertyDescriptor? GetDescriptor(Type type, string path)
    {
        var key = (type, path);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var found = TypeDescriptor.GetProperties(type).Find(path, ignoreCase: false);
        _cache[key] = found; // stores null when not found — intentional
        return found;
    }
}
