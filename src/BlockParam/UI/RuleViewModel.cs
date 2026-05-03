using BlockParam.Config;
using BlockParam.Services;

namespace BlockParam.UI;

/// <summary>
/// One rule inside a <see cref="RuleFileViewModel"/>. Holds the per-rule fields
/// previously colocated on RuleFileViewModel — the split is what fixes #70:
/// a file may now contain N rules, all of which round-trip through
/// <see cref="RuleFileViewModel.ToBulkChangeConfig"/>.
/// </summary>
public class RuleViewModel : ViewModelBase
{
    private RuleFileViewModel? _parentFile;

    private string _pathPattern = "";
    private string _datatype = "";
    private string _tagTableName = "";
    private bool _requireTagTableValue;
    private string _min = "";
    private string _max = "";
    private string _allowedValues = "";
    private string _commentTemplate = "";
    private bool _excludeFromSetpoints;
    private bool _isNew;
    private bool _isSelected;

    private string _savedPathPattern = "";
    private string _savedDatatype = "";
    private string _savedTagTableName = "";
    private bool _savedRequireTagTableValue;
    private string _savedMin = "";
    private string _savedMax = "";
    private string _savedAllowedValues = "";
    private string _savedCommentTemplate = "";
    private bool _savedExcludeFromSetpoints;

    private string _minError = "";
    private string _maxError = "";

    /// <summary>The file that owns this rule. Set by <see cref="RuleFileViewModel"/>.</summary>
    public RuleFileViewModel? ParentFile
    {
        get => _parentFile;
        internal set => SetProperty(ref _parentFile, value);
    }

    public string PathPattern
    {
        get => _pathPattern;
        set
        {
            if (SetProperty(ref _pathPattern, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(SecondaryDisplay));
                ParentFile?.NotifyChildChanged();
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
                if (!IsMinMaxSupported)
                {
                    Min = "";
                    Max = "";
                }
                ValidateMinMax();
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    /// <summary>True when the data type supports Min/Max (empty == permissive).</summary>
    public bool IsMinMaxSupported =>
        string.IsNullOrEmpty(Datatype) || TiaDataTypeValidator.SupportsMinMax(Datatype);

    public string TagTableName
    {
        get => _tagTableName;
        set
        {
            if (SetProperty(ref _tagTableName, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(SecondaryDisplay));
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    public bool RequireTagTableValue
    {
        get => _requireTagTableValue;
        set
        {
            if (SetProperty(ref _requireTagTableValue, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                ParentFile?.NotifyChildChanged();
            }
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
                ParentFile?.NotifyChildChanged();
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
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    public string MinError
    {
        get => _minError;
        private set => SetProperty(ref _minError, value);
    }

    public string MaxError
    {
        get => _maxError;
        private set => SetProperty(ref _maxError, value);
    }

    public string AllowedValues
    {
        get => _allowedValues;
        set
        {
            if (SetProperty(ref _allowedValues, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(SecondaryDisplay));
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    public string CommentTemplate
    {
        get => _commentTemplate;
        set
        {
            if (SetProperty(ref _commentTemplate, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(SecondaryDisplay));
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    public bool ExcludeFromSetpoints
    {
        get => _excludeFromSetpoints;
        set
        {
            if (SetProperty(ref _excludeFromSetpoints, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                ParentFile?.NotifyChildChanged();
            }
        }
    }

    /// <summary>True for rules created in the editor that haven't been written to disk.</summary>
    public bool IsNew
    {
        get => _isNew;
        set => SetProperty(ref _isNew, value);
    }

    /// <summary>UI-only: highlights the rule row when it's the active SelectedRule.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

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
        || _excludeFromSetpoints != _savedExcludeFromSetpoints;

    /// <summary>
    /// First non-empty descriptor for the rule list row. Helps users distinguish
    /// sibling rules in a multi-rule file at a glance.
    /// </summary>
    public string SecondaryDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_tagTableName)) return $"tagTable: {_tagTableName}";
            if (!string.IsNullOrWhiteSpace(_allowedValues)) return $"allowed: {_allowedValues}";
            if (!string.IsNullOrWhiteSpace(_commentTemplate)) return $"comment: {_commentTemplate}";
            if (!string.IsNullOrWhiteSpace(_datatype)) return _datatype;
            return "";
        }
    }

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
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ValidateMinMax()
    {
        var dt = string.IsNullOrWhiteSpace(_datatype) ? null : _datatype;

        MinError = !string.IsNullOrWhiteSpace(_min) && dt != null
            ? TiaDataTypeValidator.Validate(_min.Trim(), dt) ?? ""
            : "";

        MaxError = !string.IsNullOrWhiteSpace(_max) && dt != null
            ? TiaDataTypeValidator.Validate(_max.Trim(), dt) ?? ""
            : "";
    }

    /// <summary>Returns the validation error for this rule, or null if valid.</summary>
    public string? Validate(string fileNameForMessages)
    {
        if (string.IsNullOrWhiteSpace(_pathPattern))
            return $"Rule in '{fileNameForMessages}' must have a path pattern.";

        if (!string.IsNullOrWhiteSpace(_min) && !string.IsNullOrWhiteSpace(_max))
        {
            var dt = string.IsNullOrWhiteSpace(_datatype) ? null : _datatype;
            bool minParsed = dt != null
                ? TiaDataTypeValidator.TryParseNumericValue(_min.Trim(), dt, out var minVal)
                : double.TryParse(_min, out minVal);
            bool maxParsed = dt != null
                ? TiaDataTypeValidator.TryParseNumericValue(_max.Trim(), dt, out var maxVal)
                : double.TryParse(_max, out maxVal);

            if (minParsed && maxParsed && minVal > maxVal)
                return $"Rule '{_pathPattern}' in '{fileNameForMessages}': Min ({_min}) must be ≤ Max ({_max}).";
        }

        return null;
    }

    /// <summary>Hydrates this VM from a serialized <see cref="MemberRule"/>.</summary>
    public void LoadFromRule(MemberRule rule)
    {
        _pathPattern = rule.PathPattern ?? "";
        _datatype = rule.Datatype ?? "";
        _min = rule.Constraints?.Min?.ToString() ?? "";
        _max = rule.Constraints?.Max?.ToString() ?? "";
        _requireTagTableValue = rule.Constraints?.RequireTagTableValue ?? false;
        _allowedValues = rule.Constraints?.AllowedValues != null
            ? string.Join(", ", rule.Constraints.AllowedValues)
            : "";
        _tagTableName = rule.TagTableReference?.TableName ?? "";
        _commentTemplate = rule.CommentTemplate ?? "";
        _excludeFromSetpoints = rule.ExcludeFromSetpoints;

        OnPropertyChanged(string.Empty);
        ValidateMinMax();
        MarkClean();
    }

    /// <summary>Serializes this VM into a <see cref="MemberRule"/>.</summary>
    public MemberRule ToMemberRule()
    {
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
            rule.TagTableReference = new TagTableReference { TableName = TagTableName };

        return rule;
    }
}
