using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace BlockParam.PillSample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new SampleData();
    }
}

public sealed class SampleData
{
    public IReadOnlyList<Person> Employees { get; } = new[]
    {
        new Person("A. Kowalski", "AKO"),
        new Person("B. Schäfer",  "BSC"),
        new Person("C. Hoffmann", "CHO"),
        new Person("D. Lang",     "DLN"),
        new Person("E. Krüger",   "EKR"),
        new Person("F. Baumann",  "FBM"),
        new Person("G. Weber",    "GWE"),
        new Person("H. Roth",     "HRT"),
        new Person("I. Zentner",  "IZN"),
        new Person("J. Fischer",  "JFR"),
    };

    public IReadOnlyList<DataBlock> DataBlocks { get; } = new[]
    {
        new DataBlock("DB_ProcessControl_HighPriority", "DB10"),
        new DataBlock("DB_ProcessControl_LowPriority",  "DB11"),
        new DataBlock("DB_PumpStation_001",             "DB42"),
        new DataBlock("DB_ConfigParams",                "DB99"),
        new DataBlock("DB_DiagnosticData",              "DB100"),
        new DataBlock("DB_RecipeManager",               "DB101"),
        new DataBlock("DB_TankSettings_3",              "DB200"),
    };

    public IList SelectedEmployees { get; }
    public IList SelectedDataBlocks { get; }
    public IList SelectedQuickPicks { get; }

    public SampleData()
    {
        SelectedEmployees = new ObservableCollection<object>(
            Employees.Where(p => p.Abbrev is "AKO" or "EKR" or "GWE"));
        SelectedDataBlocks = new ObservableCollection<object>(
            DataBlocks.Where(d => d.Number is "DB10" or "DB42" or "DB99" or "DB100" or "DB101" or "DB200"));
        SelectedQuickPicks = new ObservableCollection<object>(
            DataBlocks.Where(d => d.Number is "DB10"));
    }
}

public sealed class Person
{
    public Person(string name, string abbrev) { Name = name; Abbrev = abbrev; }
    public string Name { get; }
    public string Abbrev { get; }
}

public sealed class DataBlock
{
    public DataBlock(string name, string number) { Name = name; Number = number; }
    public string Name { get; }
    public string Number { get; }
}
