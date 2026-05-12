// Clean-room API-surface stubs for Siemens TIA Portal Openness.
// Surface derived from Siemens' published Openness documentation; contains
// no Siemens IP. Every member throws — this assembly is for compilation only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Siemens.Engineering
{
    public class TiaPortal
    {
        public ProjectComposition Projects => throw new NotImplementedException();
        public IDisposable ExclusiveAccess(string description) => throw new NotImplementedException();
    }

    public class ProjectComposition : List<Project> { }

    public class Project : IEngineeringObject
    {
        public FileInfo Path => throw new NotImplementedException();
        public LanguageSettings LanguageSettings => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }

    public class LanguageSettings
    {
        public LanguageComposition ActiveLanguages => throw new NotImplementedException();
        public Language EditingLanguage => throw new NotImplementedException();
        public Language ReferenceLanguage => throw new NotImplementedException();
    }

    public class LanguageComposition : List<Language> { }

    public class Language
    {
        public CultureInfo Culture => throw new NotImplementedException();
    }

    public interface IEngineeringObject
    {
        IEngineeringObject Parent { get; }
        void SetAttribute(string name, object value);
        object GetAttribute(string name);
        T GetService<T>() where T : class;
    }
}

namespace Siemens.Engineering.AddIn
{
    public abstract class ProjectTreeAddInProvider
    {
        protected abstract IEnumerable<Menu.ContextMenuAddIn> GetContextMenuAddIns();
    }
}

namespace Siemens.Engineering.AddIn.Menu
{
    public abstract class ContextMenuAddIn
    {
        protected ContextMenuAddIn(string displayName) { }
        protected abstract void BuildContextMenuItems(ContextMenuAddInRoot addInRootSubmenu);
    }

    public class ContextMenuAddInRoot
    {
        public ActionItems Items => throw new NotImplementedException();
    }

    public class ActionItems
    {
        public void AddActionItem<T>(
            string displayName,
            Action<MenuSelectionProvider<T>> onClick,
            Func<MenuSelectionProvider<T>, MenuStatus> onUpdateStatus)
                where T : IEngineeringObject
            => throw new NotImplementedException();
    }

    public class MenuSelectionProvider<T> where T : IEngineeringObject
    {
        public IEnumerable<TItem> GetSelection<TItem>() where TItem : T
            => throw new NotImplementedException();
    }

    public enum MenuStatus
    {
        Disabled = 0,
        Enabled = 1,
        Hidden = 2,
    }
}

namespace Siemens.Engineering.Compiler
{
    public interface ICompilable
    {
        CompilerResult Compile();
    }

    public class CompilerResult
    {
        public CompilerResultState State => throw new NotImplementedException();
    }

    public enum CompilerResultState
    {
        Success = 0,
        Warning = 1,
        Error = 2,
    }
}

namespace Siemens.Engineering.SW
{
    public class PlcSoftware : IEngineeringObject
    {
        public Tags.PlcTagTableGroup TagTableGroup => throw new NotImplementedException();
        public Types.PlcTypeGroup TypeGroup => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }
}

namespace Siemens.Engineering.SW.Blocks
{
    public abstract class PlcBlock : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }

    public class DataBlock : PlcBlock
    {
        public void Export(FileInfo path, ExportOptions options) => throw new NotImplementedException();
    }

    public class PlcBlockGroup : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public PlcBlockComposition Blocks => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }

    public class PlcBlockComposition : List<PlcBlock>
    {
        public void Import(FileInfo path, ImportOptions options) => throw new NotImplementedException();
        public PlcBlock Find(string name) => throw new NotImplementedException();
    }

    public enum ExportOptions
    {
        None = 0,
        WithDefaults = 1,
        WithReadOnly = 2,
    }

    public enum ImportOptions
    {
        None = 0,
        Override = 1,
    }
}

namespace Siemens.Engineering.SW.Tags
{
    public class PlcTagTableGroup : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public PlcTagTableComposition TagTables => throw new NotImplementedException();
        public PlcTagTableGroupComposition Groups => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }

    public class PlcTagTableComposition : List<PlcTagTable> { }
    public class PlcTagTableGroupComposition : List<PlcTagTableGroup> { }

    public class PlcTagTable : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public void Export(FileInfo path, Blocks.ExportOptions options) => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }
}

namespace Siemens.Engineering.SW.Types
{
    public class PlcTypeGroup : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public PlcTypeComposition Types => throw new NotImplementedException();
        public PlcTypeGroupComposition Groups => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }

    public class PlcTypeComposition : List<PlcType> { }
    public class PlcTypeGroupComposition : List<PlcTypeGroup> { }

    public class PlcType : IEngineeringObject
    {
        public string Name => throw new NotImplementedException();
        public DateTime ModifiedDate => throw new NotImplementedException();
        public DateTime InterfaceModifiedDate => throw new NotImplementedException();
        public void Export(FileInfo path, Blocks.ExportOptions options) => throw new NotImplementedException();
        public IEngineeringObject Parent => throw new NotImplementedException();
        public void SetAttribute(string name, object value) => throw new NotImplementedException();
        public object GetAttribute(string name) => throw new NotImplementedException();
        public T GetService<T>() where T : class => throw new NotImplementedException();
    }
}
