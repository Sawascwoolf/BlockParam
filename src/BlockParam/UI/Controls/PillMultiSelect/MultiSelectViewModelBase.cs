using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlockParam.UI.Controls.PillMultiSelect;

// Minimal INotifyPropertyChanged base for the pill control's internal VMs.
// Kept here so the folder has zero dependency on a host MVVM helper.
//
// Must be `public`, not `internal`: WPF's binding engine reflects over bound
// objects from PresentationFramework, a foreign assembly. Under TIA Portal's
// partial-trust SandboxDomain, reflecting non-public types from a foreign
// assembly is rejected and bindings yield no value — pill renders blank, the
// trigger toggle doesn't write back. Full-trust CI/DevLauncher hides this.
// See #141.
public abstract class MultiSelectViewModelBase : INotifyPropertyChanged
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
