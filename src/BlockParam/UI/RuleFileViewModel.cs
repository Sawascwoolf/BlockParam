using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using BlockParam.Config;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// ViewModel representing a single rule file (one .json on disk).
/// A file contains 1..N <see cref="RuleViewModel"/> entries — issue #70 fix:
/// the editor now round-trips the entire <c>rules[]</c> array instead of
/// silently dropping rules 2..N on save.
/// </summary>
public class RuleFileViewModel : ViewModelBase
{
    /// <summary>Well-known TIA Portal primitive data types for the Datatype dropdown.</summary>
    public static readonly string[] TiaDataTypes =
    {
        "", "Bool", "Byte", "Word", "DWord", "LWord",
        "SInt", "Int", "DInt", "LInt",
        "USInt", "UInt", "UDInt", "ULInt",
        "Real", "LReal",
        "Char", "WChar", "String", "WString",
        "S5Time", "Time", "LTime", "Date", "LDate",
        "TimeOfDay", "LTimeOfDay", "DateTime", "LDateTime", "DTL",
        "Timer", "Counter"
    };

    /// <summary>The three sources a user can save a rule file to. Inline rules
    /// live in DB/UDT comments and are not authored through this editor.</summary>
    public static readonly RuleSource[] UserSelectableSources =
    {
        RuleSource.TiaProject,
        RuleSource.Local,
        RuleSource.Shared,
    };

    private static readonly Regex AutoNamePattern =
        new(@"^new-rule(-\d+)?\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string _fileName = "";
    private string _filePath = "";
    private RuleSource _source;
    private string _fileType = "Rule";
    private bool _isOverride;
    private RuleSource _saveDestination;
    private RuleSource _savedSaveDestination;
    private string _savedFileName = "";
    private bool _isNew;
    private bool _isExpanded = true;

    /// <summary>
    /// Tracks which rules we've subscribed to. Doubles as the source of truth
    /// for <see cref="NotifyCollectionChangedAction.Reset"/>, where neither
    /// OldItems nor NewItems is populated — without this, Reset would leak
    /// PropertyChanged subscriptions on the cleared rules.
    /// </summary>
    private readonly HashSet<RuleViewModel> _subscribedRules = new();

    public RuleFileViewModel()
    {
        // Stable per-instance identifier — used as RadioButton GroupName so
        // each file's source pills stay grouped only with each other, even
        // across save/reload cycles where FilePath transitions briefly.
        GroupId = Guid.NewGuid().ToString("N");
        Rules = new ObservableCollection<RuleViewModel>();
        Rules.CollectionChanged += OnRulesCollectionChanged;
    }

    /// <summary>
    /// Rules contained in this file. Order is preserved on save.
    /// </summary>
    public ObservableCollection<RuleViewModel> Rules { get; }

    /// <summary>Stable per-instance ID for grouping the file's source RadioButtons.</summary>
    public string GroupId { get; }

    private void Subscribe(RuleViewModel r)
    {
        if (_subscribedRules.Add(r))
        {
            r.ParentFile = this;
            r.PropertyChanged += OnRulePropertyChanged;
        }
    }

    private void Unsubscribe(RuleViewModel r)
    {
        if (_subscribedRules.Remove(r))
        {
            r.PropertyChanged -= OnRulePropertyChanged;
            if (ReferenceEquals(r.ParentFile, this))
                r.ParentFile = null;
        }
    }

    /// <summary>
    /// Re-parents rules and (un)subscribes to their PropertyChanged so the
    /// file's aggregate <see cref="IsDirty"/> updates without each rule
    /// setter having to call back. Handles Add / Remove / Replace / Reset.
    /// </summary>
    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset (e.g. Rules.Clear()) populates neither OldItems nor NewItems.
            // Diff against our subscription set: drop anything no longer present,
            // pick up anything new.
            foreach (var r in _subscribedRules.ToList())
                if (!Rules.Contains(r)) Unsubscribe(r);
            foreach (var r in Rules) Subscribe(r);
        }
        else
        {
            // OldItems first: a Replace event populates both OldItems and
            // NewItems with the (potentially same) instances. Subscribing
            // last guarantees ParentFile ends up correct on Replace.
            if (e.OldItems != null)
                foreach (RuleViewModel r in e.OldItems) Unsubscribe(r);
            if (e.NewItems != null)
                foreach (RuleViewModel r in e.NewItems) Subscribe(r);
        }

        OnPropertyChanged(nameof(RuleCount));
        OnPropertyChanged(nameof(HasMultipleRules));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HeaderSummary));
    }

    private void OnRulePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuleViewModel.IsDirty)
            || string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public int RuleCount => Rules.Count;
    public bool HasMultipleRules => Rules.Count > 1;

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetProperty(ref _fileName, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(HeaderSummary));
                OnPropertyChanged(nameof(IsAutoNamed));
            }
        }
    }

    /// <summary>
    /// True when the filename still matches the placeholder auto-name pattern
    /// produced by <c>GenerateUniqueNewFileName</c>. Used by SaveAll to decide
    /// whether to derive the final filename from the first rule's pathPattern.
    /// Once the user types anything else, this becomes false and the user's
    /// name is preserved verbatim.
    /// </summary>
    public bool IsAutoNamed => AutoNamePattern.IsMatch(_fileName);

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public RuleSource Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
                OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    public string SourceDisplay => Source switch
    {
        RuleSource.TiaProject => "Project",
        RuleSource.Local => "Local",
        RuleSource.Shared => "Shared",
        _ => Source.ToString()
    };

    /// <summary>Kept for backwards compat with tests; "Rule" is the only supported value.</summary>
    public string FileType
    {
        get => _fileType;
        set => SetProperty(ref _fileType, value);
    }

    public bool IsOverride
    {
        get => _isOverride;
        set
        {
            if (SetProperty(ref _isOverride, value))
                OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    /// <summary>Lower-priority versions of this file (same filename in another source).</summary>
    public List<RuleFileViewModel> OverriddenVersions { get; } = new();

    public bool HasOverrides => OverriddenVersions.Count > 0;

    public void NotifyOverrideChanged()
    {
        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(IsOverride));
        OnPropertyChanged(nameof(HeaderSummary));
    }

    public string StatusDisplay
    {
        get
        {
            if (OverriddenVersions.Count == 0) return "";
            var sources = string.Join(", ", OverriddenVersions.Select(v => v.SourceDisplay));
            return $"overrides {sources}";
        }
    }

    /// <summary>
    /// One-line summary used as the secondary text in the file header
    /// (e.g. "Project · 11 rules · overrides Local").
    /// </summary>
    public string HeaderSummary
    {
        get
        {
            var parts = new List<string> { SourceDisplay };
            parts.Add(RuleCount == 1 ? "1 rule" : $"{RuleCount} rules");
            if (HasOverrides) parts.Add(StatusDisplay);
            return string.Join(" · ", parts);
        }
    }

    public RuleSource SaveDestination
    {
        get => _saveDestination;
        set
        {
            if (SetProperty(ref _saveDestination, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>True for files created in the editor that haven't been written to disk yet.</summary>
    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (SetProperty(ref _isNew, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>UI-only state: whether the file's rule list is expanded in the left panel.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>True when any field — file-level metadata or any contained rule — has changed since last save.</summary>
    public bool IsDirty =>
        IsNew
        || _saveDestination != _savedSaveDestination
        || _fileName != _savedFileName
        || Rules.Any(r => r.IsDirty);

    public void MarkClean()
    {
        IsNew = false;
        _savedSaveDestination = _saveDestination;
        _savedFileName = _fileName;
        foreach (var r in Rules) r.MarkClean();
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>
    /// Derives a safe filename from a rule's path pattern. Used when a new file
    /// is being authored — auto-syncs filename to match the pattern.
    /// </summary>
    public static string PatternToFileName(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "new-rule.json";

        var name = pattern
            .Replace("\\.", ".")
            .Replace(".*", "_any_")
            .Replace(".+", "_some_")
            .Replace("$", "")
            .Replace("^", "");

        name = Regex.Replace(name, @"\{[^:}]+:([^}]+)\}", "$1");
        name = Regex.Replace(name, @"\{([^}]+)\}", "$1");

        var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars())
            { '*', '?', '{', '}', '[', ']', '|', '\\', '/' };
        var chars = name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
        name = new string(chars);

        name = Regex.Replace(name, @"_+", "_").TrimEnd('_', '.').TrimStart('.');

        if (string.IsNullOrWhiteSpace(name)) return "new-rule.json";
        return name + ".json";
    }

    /// <summary>
    /// Loads a rule file from disk. Returns null when the file is missing,
    /// unreadable, or doesn't carry the BlockParam version sentinel.
    /// </summary>
    public static RuleFileViewModel? FromFile(string filePath, RuleSource source)
    {
        if (!File.Exists(filePath)) return null;

        string json;
        try { json = File.ReadAllText(filePath); }
        catch { return null; }

        BulkChangeConfig? config;
        try { config = JsonConvert.DeserializeObject<BulkChangeConfig>(json); }
        catch (JsonException) { return null; }

        if (config == null || string.IsNullOrEmpty(config.Version))
            return null;

        var vm = new RuleFileViewModel
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            Source = source,
            SaveDestination = source,
            FileType = "Rule"
        };

        foreach (var rule in config.Rules)
        {
            var rvm = new RuleViewModel();
            rvm.LoadFromRule(rule);
            vm.Rules.Add(rvm);
        }

        vm.MarkClean();
        return vm;
    }

    /// <summary>
    /// Serializes the file back into a <see cref="BulkChangeConfig"/>. All rules
    /// are emitted in order — preserving multi-rule files (issue #70).
    /// </summary>
    public BulkChangeConfig ToBulkChangeConfig()
    {
        var config = new BulkChangeConfig { Version = "1.0" };
        foreach (var rule in Rules)
            config.Rules.Add(rule.ToMemberRule());
        return config;
    }

    /// <summary>
    /// Validates every rule in the file. Returns the first error encountered,
    /// or null when all rules are valid.
    /// </summary>
    public string? Validate()
    {
        if (Rules.Count == 0)
            return $"File '{FileName}' has no rules.";

        foreach (var r in Rules)
        {
            var err = r.Validate(FileName);
            if (err != null) return err;
        }
        return null;
    }

    /// <summary>
    /// Convenience accessor: filename auto-derive from the first rule's pattern,
    /// used while the file is still <see cref="IsNew"/>. Returns a safe filename.
    /// </summary>
    public string DeriveFileNameFromFirstRule()
    {
        var first = Rules.FirstOrDefault();
        return PatternToFileName(first?.PathPattern ?? "");
    }
}
