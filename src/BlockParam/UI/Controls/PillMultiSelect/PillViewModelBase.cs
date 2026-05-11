using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlockParam.UI.Controls.PillMultiSelect;

// Minimal INotifyPropertyChanged base for the pill control's internal VMs.
// Kept here so the folder has zero dependency on a host MVVM helper.
internal abstract class PillViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
