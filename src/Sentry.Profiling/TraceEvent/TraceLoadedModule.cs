using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceLoadedModule represents a module (DLL or EXE) that was loaded into a process.  It represents
/// the time that this module was mapped into the processes address space.
/// </summary>
internal class TraceLoadedModule
{
    /// <summary>
    /// The address where the DLL or EXE was loaded.   Will return 0 for managed modules without NGEN images.
    /// </summary>
    public Address ImageBase
    {
        get
        {
            if (moduleFile == null)
            {
                return 0;
            }
            else
            {
                return moduleFile.ImageBase;
            }
        }
    }
    /// <summary>
    /// The load time is the time the LoadLibrary was done if it was loaded from a file, otherwise is the
    /// time the CLR loaded the module.  Expressed as a DateTime
    /// </summary>
    public DateTime LoadTime { get { return DateTime.FromFileTime(loadTimeQPC); } }

    // /// <summary>
    // /// The load time is the time the LoadLibrary was done if it was loaded from a file, otherwise is the
    // /// time the CLR loaded the module.  Expressed as as MSec from the beginning of the trace.
    // /// </summary>
    // public double LoadTimeRelativeMSec { get { return Process.Log.QPCTimeToRelMSec(loadTimeQPC); } }

    /// <summary>
    /// The load time is the time the FreeLibrary was done if it was unmanaged, otherwise is the
    /// time the CLR unloaded the module.  Expressed as a DateTime
    /// </summary>
    public DateTime UnloadTime { get { return DateTime.FromFileTime(unloadTimeQPC); } }

    // /// <summary>
    // /// The load time is the time the FreeLibrary was done if it was unmanaged, otherwise is the
    // /// time the CLR unloaded the module.  Expressed as MSec from the beginning of the trace.
    // /// </summary>
    // public double UnloadTimeRelativeMSec { get { return Process.Log.QPCTimeToRelMSec(unloadTimeQPC); } }

    // /// <summary>
    // /// The process that loaded this module
    // /// </summary>
    // public TraceProcess Process { get { return process; } }

    /// <summary>
    /// An ID that uniquely identifies the module in within the process.  Works for both the managed and unmanaged case.
    /// </summary>
    public virtual long ModuleID { get { return (long)ImageBase; } }

    /// <summary>
    /// If this managedModule was a file that was mapped into memory (eg LoadLibary), then ModuleFile points at
    /// it.  If a managed module does not have a file associated with it, this can be null.
    /// </summary>
    public TraceModuleFile ModuleFile { get { return moduleFile; } }

    /// <summary>
    /// Shortcut for ModuleFile.FilePath, but returns the empty string if ModuleFile is null
    /// </summary>
    public string FilePath
    {
        get
        {
            if (ModuleFile == null)
            {
                return "";
            }
            else
            {
                return ModuleFile.FilePath;
            }
        }
    }
    /// <summary>
    /// Shortcut for ModuleFile.Name, but returns the empty string if ModuleFile is null
    /// </summary>
    public string Name
    {
        get
        {
            if (ModuleFile == null)
            {
                return "";
            }
            else
            {
                return ModuleFile.Name;
            }
        }
    }
    // TODO: provide a way of getting at all the loaded images.
    /// <summary>
    /// Because .NET applications have AppDomains, a module that is loaded once from a process
    /// perspective, might be loaded several times (once for each AppDomain) from a .NET perspective
    /// <para> This property returns the loadedModule record for the first such managed module
    /// load associated with this load.
    /// </para>
    /// </summary>
    public TraceManagedModule? ManagedModule { get { return managedModule; } }

    #region Private

    internal TraceLoadedModule(TraceProcess process, TraceModuleFile moduleFile, Address imageBase)
    {
        this.process = process;
        this.moduleFile = moduleFile;
        unloadTimeQPC = long.MaxValue;
        key = (ulong)imageBase;
    }
    internal TraceLoadedModule(TraceProcess process, TraceModuleFile moduleFile, long moduleID)
    {
        this.process = process;
        this.moduleFile = moduleFile;
        unloadTimeQPC = long.MaxValue;
        key = (ulong)moduleID;
    }

    internal ulong key;                          // Either the base address (for unmanaged) or moduleID (managed)
    internal bool overlaps;                      // address range overlaps with other modules in the list.
    internal long loadTimeQPC;
    internal long unloadTimeQPC;
    internal TraceManagedModule? managedModule;
    private TraceProcess process;
    private TraceModuleFile moduleFile;         // Can be null (modules with files)

    internal int stackVisitedID;                // Used to determine if we have already visited this node or not.
    #endregion
}
