using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using BlockParam.Config;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// ViewModel representing a single rule file from any source (Project/Local/Shared).
/// Supports two file types: Rule, Excludes.
/// Comment templates are now part of Rule files (per-rule, not a separate file type).
/// </summary>
public class RuleFileViewModel : ViewModelBase
{
    /// <summary>
    /// Well-known TIA Portal primitive data types for the Datatype dropdown.
    /// </summary>
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

    private string _fileName = "";
    private string _filePath = "";
    private RuleSource _source;
    private string _fileType = "Rule";
    private bool _isOverride;
    private RuleSource _saveDestination;
    private bool _isNew;

    // Rule fields
    private string _pathPattern = "";
    private string _datatype = "";
    private string _tagTableName = "";
    private bool _requireTagTableValue;
    private string _min = "";
    private string _max = "";
    private string _allowedValues = "";
    private string _commentTemplate = "";
    private bool _excludeFromSetpoints;

    // Snapshot of saved state for dirty tracking
    private string _savedPathPattern = "";
    private string _savedDatatype = "";
    private string _savedTagTableName = "";
    private bool _savedRequireTagTableValue;
    private string _savedMin = "";
    private string _savedMax = "";
    private string _savedAllowedValues = "";
    private string _savedCommentTemplate = "";
    private bool _savedExcludeFromSetpoints;
    private RuleSource _savedSaveDestination;

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public RuleSource Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string SourceDisplay => Source switch
    {
        RuleSource.TiaProject => "Project",
        RuleSource.Local => "Local",
        RuleSource.Shared => "Shared",
        _ => Source.ToString()
    };

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

    /// <summary>Lower-priority versions that this rule overrides (same path pattern).</summary>
    public List<RuleFileViewModel> OverriddenVersions { get; } = new();

    /// <summary>True when this rule overrides a lower-priority version.</summary>
    public bool HasOverrides => OverriddenVersions.Count > 0;

    /// <summary>Notifies the UI that override state changed.</summary>
    public void NotifyOverrideChanged()
    {
        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(IsOverride));
    }

    /// <summary>Display text showing what this rule overrides.</summary>
    public string StatusDisplay
    {
        get
        {
            if (OverriddenVersions.Count == 0) return "";
            var sources = string.Join(", ", OverriddenVersions.Select(v => v.SourceDisplay));
            return $"overrides {sources}";
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

    /// <summary>True for newly created rules that haven't been saved yet.</summary>
    public bool IsNew
    {
        get => _isNew;
        set => SetProperty(ref _isNew, value);
    }

    /// <summary>True when any field differs from its last-saved state.</summary>
    public bool IsDirty =>
        IsNew
        || _pathPattern != _savedPathPattern
        || _datatype != _savedDatatype
        || _tagTableName != _savedTagTableName
        || _requireTagTableValue != _savedRequireTagTableValue
        || _min != _savedMin
        || _max != _savedMax
        || _allowedValues != _savedAllowedValues
        || _commentTemplate != _savedCommentTemplate
        || _excludeFromSetpoints != _savedExcludeFromSetpoints
        || _saveDestination != _savedSaveDestination;

    /// <summary>Captures the current field values as the "saved" baseline.</summary>
    public void MarkClean()
    {
        IsNew = false;
        _savedPathPattern = _pathPattern;
        _savedDatatype = _datatype;
        _savedTagTableName = _tagTableName;
        _savedRequireTagTableValue = _requireTagTableValue;
        _savedMin = _min;
        _savedMax = _max;
        _savedAllowedValues = _allowedValues;
        _savedCommentTemplate = _commentTemplate;
        _savedExcludeFromSetpoints = _excludeFromSetpoints;
        _savedSaveDestination = _saveDestination;
        OnPropertyChanged(nameof(IsDirty));
    }

    // --- Rule fields ---

    public string PathPattern
    {
        get => _pathPattern;
        set
        {
            if (SetProperty(ref _pathPattern, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                SyncFileNameFromPattern();
            }
        }
    }

    public string Datatype
    {
        get => _datatype;
        set
        {
            if (SetProperty(ref _datatype, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(IsMinMaxSupported));

                // Clear Min/Max when switching to unsupported type
                if (!IsMinMaxSupported)
                {
                    Min = "";
                    Max = "";
                }

                // Re-validate existing Min/Max against new datatype
                ValidateMinMax();
            }
        }
    }

    /// <summary>
    /// True when the selected data type supports Min/Max constraints.
    /// Empty datatype = true (conservative: rule might match various types).
    /// </summary>
    public bool IsMinMaxSupported =>
        string.IsNullOrEmpty(Datatype) || TiaDataTypeValidator.SupportsMinMax(Datatype);

    public string TagTableName
    {
        get => _tagTableName;
        set
        {
            if (SetProperty(ref _tagTableName, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool RequireTagTableValue
    {
        get => _requireTagTableValue;
        set
        {
            if (SetProperty(ref _requireTagTableValue, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    public string Min
    {
        get => _min;
        set
        {
            if (SetProperty(ref _min, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                ValidateMinMax();
            }
        }
    }

    public string Max
    {
        get => _max;
        set
        {
            if (SetProperty(ref _max, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                ValidateMinMax();
            }
        }
    }

    private string _minError = "";
    private string _maxError = "";

    /// <summary>Validation error for Min field, or empty if valid.</summary>
    public string MinError
    {
        get => _minError;
        private set => SetProperty(ref _minError, value);
    }

    /// <summary>Validation error for Max field, or empty if valid.</summary>
    public string MaxError
    {
        get => _maxError;
        private set => SetProperty(ref _maxError, value);
    }

    private void ValidateMinMax()
    {
        var dt = string.IsNullOrWhiteSpace(Datatype) ? null : Datatype;

        // Validate Min format against datatype
        MinError = !string.IsNullOrWhiteSpace(Min) && dt != null
            ? TiaDataTypeValidator.Validate(Min.Trim(), dt) ?? ""
            : "";

        // Validate Max format against datatype
        MaxError = !string.IsNullOrWhiteSpace(Max) && dt != null
            ? TiaDataTypeValidator.Validate(Max.Trim(), dt) ?? ""
            : "";
    }

    public string AllowedValues
    {
        get => _allowedValues;
        set
        {
            if (SetProperty(ref _allowedValues, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    public string CommentTemplate
    {
        get => _commentTemplate;
        set
        {
            if (SetProperty(ref _commentTemplate, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool ExcludeFromSetpoints
    {
        get => _excludeFromSetpoints;
        set
        {
            if (SetProperty(ref _excludeFromSetpoints, value))
                OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>
    /// Derives a safe filename from the path pattern.
    /// Replaces characters that are invalid in filenames.
    /// </summary>
    public static string PatternToFileName(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "new-rule.json";

        // Replace path separators and common glob tokens with readable alternatives
        var name = pattern
            .Replace("\\.", ".")       // unescape dots first
            .Replace(".*", "_any_")    // regex wildcard
            .Replace(".+", "_some_")   // regex one-or-more
            .Replace("$", "")          // anchor
            .Replace("^", "");         // anchor

        // Replace {token:value} with just value
        name = Regex.Replace(name, @"\{[^:}]+:([^}]+)\}", "$1");
        // Replace remaining {token} with token
        name = Regex.Replace(name, @"\{([^}]+)\}", "$1");

        // Replace invalid filename characters with underscore
        var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars()) { '*', '?', '{', '}', '[', ']', '|', '\\', '/' };
        var chars = name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
        name = new string(chars);

        // Collapse consecutive underscores, trim trailing only
        name = Regex.Replace(name, @"_+", "_").TrimEnd('_', '.').TrimStart('.');

        if (string.IsNullOrWhiteSpace(name)) return "new-rule.json";
        return name + ".json";
    }

    /// <summary>
    /// Updates FileName to match the current PathPattern (auto-sync).
    /// Only auto-syncs for new rules or when the filename was previously derived from the pattern.
    /// </summary>
    private void SyncFileNameFromPattern()
    {
        // Only auto-sync for new (unsaved) rules
        if (!IsNew) return;

        var derived = PatternToFileName(_pathPattern);
        FileName = derived;

        // Also update FilePath if directory is known
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            FilePath = Path.Combine(dir, derived);
    }

    /// <summary>True when FileType is "Rule" (used for XAML binding).</summary>
    public bool IsRuleType => FileType == "Rule";

    /// <summary>
    /// Loads a rule file from disk and creates a RuleFileViewModel.
    /// </summary>
    public static RuleFileViewModel? FromFile(string filePath, RuleSource source)
    {
        if (!File.Exists(filePath)) return null;

        string json;
        try { json = File.ReadAllText(filePath); }
        catch { return null; }

        var config = JsonConvert.DeserializeObject<BulkChangeConfig>(json);
        if (config == null || string.IsNullOrEmpty(config.Version))
            return null;

        var vm = new RuleFileViewModel
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            Source = source,
            SaveDestination = source
        };

        // Populate fields from first rule
        vm.FileType = "Rule";
        if (config.Rules.Count > 0)
        {
            var rule = config.Rules[0];
            vm.PathPattern = rule.PathPattern ?? "";
            vm.Datatype = rule.Datatype ?? "";
            vm.Min = rule.Constraints?.Min?.ToString() ?? "";
            vm.Max = rule.Constraints?.Max?.ToString() ?? "";
            vm.RequireTagTableValue = rule.Constraints?.RequireTagTableValue ?? false;
            vm.AllowedValues = rule.Constraints?.AllowedValues != null
                ? string.Join(", ", rule.Constraints.AllowedValues)
                : "";
            vm.TagTableName = rule.TagTableReference?.TableName ?? "";
            vm.CommentTemplate = rule.CommentTemplate ?? "";
            vm.ExcludeFromSetpoints = rule.ExcludeFromSetpoints;
        }

        vm.MarkClean();
        return vm;
    }

    /// <summary>
    /// Converts this ViewModel back to a BulkChangeConfig for serialization.
    /// </summary>
    public BulkChangeConfig ToBulkChangeConfig()
    {
        var config = new BulkChangeConfig { Version = "1.0" };

        var rule = new MemberRule
        {
            PathPattern = PathPattern,
            Datatype = string.IsNullOrWhiteSpace(Datatype) ? null : Datatype,
            CommentTemplate = string.IsNullOrWhiteSpace(CommentTemplate) ? null : CommentTemplate,
            ExcludeFromSetpoints = ExcludeFromSetpoints
        };

        if (!string.IsNullOrWhiteSpace(Min) || !string.IsNullOrWhiteSpace(Max)
            || !string.IsNullOrWhiteSpace(AllowedValues) || RequireTagTableValue)
        {
            rule.Constraints = new ValueConstraint();
            if (!string.IsNullOrWhiteSpace(Min))
                rule.Constraints.Min = double.TryParse(Min, out var minNum) ? (object)minNum : Min.Trim();
            if (!string.IsNullOrWhiteSpace(Max))
                rule.Constraints.Max = double.TryParse(Max, out var maxNum) ? (object)maxNum : Max.Trim();
            rule.Constraints.RequireTagTableValue = RequireTagTableValue;
            if (!string.IsNullOrWhiteSpace(AllowedValues))
            {
                rule.Constraints.AllowedValues = AllowedValues
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Select(v => (object)v)
                    .ToList();
            }
        }

        if (!string.IsNullOrWhiteSpace(TagTableName))
        {
            rule.TagTableReference = new TagTableReference { TableName = TagTableName };
        }

        config.Rules.Add(rule);

        return config;
    }

    /// <summary>
    /// Returns the validation error for this file, or null if valid.
    /// </summary>
    public string? Validate()
    {
        if (FileType == "Rule" && string.IsNullOrWhiteSpace(PathPattern))
            return $"Rule '{FileName}' must have a path pattern.";

        if (FileType == "Rule" && !string.IsNullOrWhiteSpace(Min) && !string.IsNullOrWhiteSpace(Max))
        {
            // Use TIA-aware parsing when datatype is known (handles T#, D#, 16# etc.)
            var dt = string.IsNullOrWhiteSpace(Datatype) ? null : Datatype;
            bool minParsed = dt != null
                ? TiaDataTypeValidator.TryParseNumericValue(Min.Trim(), dt, out var minVal)
                : double.TryParse(Min, out minVal);
            bool maxParsed = dt != null
                ? TiaDataTypeValidator.TryParseNumericValue(Max.Trim(), dt, out var maxVal)
                : double.TryParse(Max, out maxVal);

            if (minParsed && maxParsed && minVal > maxVal)
                return $"Rule '{FileName}': Min ({Min}) must be ≤ Max ({Max}).";
        }

        return null;
    }
}
