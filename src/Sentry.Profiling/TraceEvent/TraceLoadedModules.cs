
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Address = System.UInt64;

namespace Sentry.Profiling.TraceEvent;

/// <summary>
/// TraceLoadedModules represents the collection of modules (loaded DLLs or EXEs) in a
/// particular process.
/// </summary>
internal sealed class TraceLoadedModules : IEnumerable<TraceLoadedModule>
{
    /// <summary>
    /// The process in which this Module is loaded.
    /// </summary>
    public TraceProcess Process { get { return process; } }

    // /// <summary>
    // /// Returns the module which was mapped into memory at at 'timeRelativeMSec' and includes the address 'address'
    // /// <para> Note that Jit compiled code is placed into memory that is not associated with the module and thus will not
    // /// be found by this method.
    // /// </para>
    // /// </summary>
    // public TraceLoadedModule GetModuleContainingAddress(Address address, double timeRelativeMSec)
    // {
    //     int index;
    //     TraceLoadedModule module = FindModuleAndIndexContainingAddress(address, process.Log.RelativeMSecToQPC(timeRelativeMSec), out index);
    //     return module;
    // }

    // /// <summary>
    // /// Returns the module representing the unmanaged load of a particular fiele at a given time.
    // /// </summary>
    // public TraceLoadedModule? GetLoadedModule(string fileName, double timeRelativeMSec)
    // {
    //     long timeQPC = process.Log.RelativeMSecToQPC(timeRelativeMSec);
    //     for (int i = 0; i < modules.Count; i++)
    //     {
    //         TraceLoadedModule module = modules[i];
    //         if (string.Compare(module.FilePath, fileName, StringComparison.OrdinalIgnoreCase) == 0 && module.loadTimeQPC <= timeQPC && timeQPC < module.unloadTimeQPC)
    //         {
    //             return module;
    //         }
    //     }
    //     return null;
    // }

    #region Private
    /// <summary>
    /// Returns all modules in the process.  Note that managed modules may appear twice
    /// (once for the managed load and once for an unmanaged (LoadLibrary) load.
    /// </summary>
    public IEnumerator<TraceLoadedModule> GetEnumerator()
    {
        for (int i = 0; i < modules.Count; i++)
        {
            yield return modules[i];
        }
    }
    /// <summary>
    /// This function will find the module associated with 'address' at 'timeQPC' however it will only
    /// find modules that are mapped in memory (module associated with JIT compiled methods will not be found).
    /// </summary>
    internal TraceLoadedModule? GetLoadedModule(string fileName, long timeQPC)
    {
        for (int i = 0; i < modules.Count; i++)
        {
            TraceLoadedModule module = modules[i];
            if (string.Compare(module.FilePath, fileName, StringComparison.OrdinalIgnoreCase) == 0 && module.loadTimeQPC <= timeQPC && timeQPC < module.unloadTimeQPC)
            {
                return module;
            }
        }
        return null;
    }

    [Obsolete("Not obsolete, just needs to access data.TimeStampQPC", false)]
    internal void ManagedModuleLoadOrUnload(ModuleLoadUnloadTraceData data, bool isLoad, bool isDCStartStop)
    {
        var ilModulePath = data.ModuleILPath;
        var nativeModulePath = data.ModuleNativePath;
        var nativePdbSignature = data.NativePdbSignature;

        // If the NGEN image is used as the IL image (happened in CoreCLR case), change the name of the
        // IL image to be a 'fake' non-nGEN image.  We need this because we need a DISTINCT file that
        // we can hang the PDB signature information on for the IL pdb.
        if (ilModulePath == nativeModulePath)
        {
            // Remove the .ni. from the path.  Note that this file won't exist, but that is OK.
            var nisuffix = ilModulePath.LastIndexOf(".ni.", ilModulePath.Length, 8, StringComparison.OrdinalIgnoreCase);
            if (0 <= nisuffix)
            {
                ilModulePath = ilModulePath.Substring(0, nisuffix) + ilModulePath.Substring(nisuffix + 3);
            }
        }
        // This is the CoreCLR (First Generation) ReadyToRun case.   There still is a native PDB that is distinct
        // from the IL PDB.   Unlike CoreCLR NGEN, it is logged as a IL file, but it has native code (and thus an NativePdbSignature)
        // We treat the image as a native image and dummy up a il image to hang the IL PDB information on.
        else if (nativeModulePath.Length == 0 && nativePdbSignature != Guid.Empty && data.ManagedPdbSignature != Guid.Empty)
        {
            // And make up a fake .il.dll module for the IL
            var suffixPos = ilModulePath.LastIndexOf(".", StringComparison.OrdinalIgnoreCase);
            if (0 < suffixPos)
            {
                // We treat the image as the native path
                nativeModulePath = ilModulePath;
                // and make up a dummy IL path.
                ilModulePath = ilModulePath.Substring(0, suffixPos) + ".il" + ilModulePath.Substring(suffixPos);
            }
        }

        int index;
        TraceManagedModule? module = FindManagedModuleAndIndex(data.ModuleID, data.TimeStampQPC, out index);
        if (module == null)
        {
            // We need to make a new module
            TraceModuleFile newModuleFile = process.Log.ModuleFiles.GetOrCreateModuleFile(ilModulePath, 0);
            module = new TraceManagedModule(process, newModuleFile, data.ModuleID);
            modules.Insert(index + 1, module);      // put it where it belongs in the sorted list
        }

        // process.Log.DebugWarn(module.assemblyID == 0 || module.assemblyID == data.AssemblyID, "Inconsistent Assembly ID previous ID = 0x" + module.assemblyID.ToString("x"), data);
        module.assemblyID = data.AssemblyID;
        module.flags = data.ModuleFlags;
        if (nativeModulePath.Length > 0)
        {
            module.nativeModule = GetLoadedModule(nativeModulePath, data.TimeStampQPC);
        }

        if (module.ModuleFile.fileName == null)
        {
            process.Log.ModuleFiles.SetModuleFileName(module.ModuleFile, ilModulePath);
        }

        if (module.ModuleFile.pdbSignature == Guid.Empty && data.ManagedPdbSignature != Guid.Empty)
        {
            module.ModuleFile.pdbSignature = data.ManagedPdbSignature;
            module.ModuleFile.pdbAge = data.ManagedPdbAge;
            module.ModuleFile.pdbName = data.ManagedPdbBuildPath;
        }

        if (module.NativeModule != null)
        {
            Debug.Assert(module.NativeModule.managedModule == null ||
                module.NativeModule.ModuleFile.managedModule?.FilePath == module.ModuleFile.FilePath);

            module.NativeModule.ModuleFile.managedModule = module.ModuleFile;
            if (nativePdbSignature != Guid.Empty && module.NativeModule.ModuleFile.pdbSignature == Guid.Empty)
            {
                module.NativeModule.ModuleFile.pdbSignature = nativePdbSignature;
                module.NativeModule.ModuleFile.pdbAge = data.NativePdbAge;
                module.NativeModule.ModuleFile.pdbName = data.NativePdbBuildPath;
            }
        }

        // TODO factor this with the unmanaged case.
        if (isLoad)
        {
            // process.Log.DebugWarn(module.loadTimeQPC == 0 || data.Opcode == TraceEventOpcode.DataCollectionStart, "Events for module happened before load.  PrevEventTime: " + module.LoadTimeRelativeMSec.ToString("f4"), data);
            // process.Log.DebugWarn(data.TimeStampQPC < module.unloadTimeQPC, "Managed Unload time < load time!", data);

            module.loadTimeQPC = data.TimeStampQPC;
            if (!isDCStartStop)
            {
                module.loadTimeQPC = process.Log.sessionStartTimeQPC;
            }
        }
        else
        {
            // process.Log.DebugWarn(module.loadTimeQPC < data.TimeStampQPC, "Managed Unload time < load time!", data);
            // process.Log.DebugWarn(module.unloadTimeQPC == long.MaxValue, "Unloading a managed image twice PrevUnloadTime: " + module.UnloadTimeRelativeMSec.ToString("f4"), data);
            if (!isDCStartStop)
            {
                module.unloadTimeQPC = data.TimeStampQPC;
            }
        }
        CheckClassInvarients();
    }

    internal TraceManagedModule GetOrCreateManagedModule(long managedModuleID, long timeQPC)
    {
        int index;
        TraceManagedModule? module = FindManagedModuleAndIndex(managedModuleID, timeQPC, out index);
        if (module == null)
        {
            // We need to make a new module entry (which is pretty empty)
            TraceModuleFile newModuleFile = process.Log.ModuleFiles.GetOrCreateModuleFile(null, 0);
            module = new TraceManagedModule(process, newModuleFile, managedModuleID);
            modules.Insert(index + 1, module);      // put it where it belongs in the sorted list
        }
        return module;
    }

    /// <summary>
    /// Finds the index and module for an a given managed module ID.  If not found, new module
    /// should be inserted at index + 1;
    /// </summary>
    private TraceManagedModule? FindManagedModuleAndIndex(long moduleID, long timeQPC, out int index)
    {
        modules.BinarySearch((ulong)moduleID, out index, compareByKey);
        // Index now points at the last place where module.key <= moduleId;
        // Search backwards from where for a module that is loaded and in range.
        while (index >= 0)
        {
            TraceLoadedModule candidateModule = modules[index];
            if (candidateModule.key < (ulong)moduleID)
            {
                break;
            }

            Debug.Assert(candidateModule.key == (ulong)moduleID);

            // We keep managed modules after unmanaged modules
            TraceManagedModule? managedModule = candidateModule as TraceManagedModule;
            if (managedModule == null)
            {
                break;
            }

            // we also sort all modules with the same module ID by unload time
            if (!(timeQPC < candidateModule.unloadTimeQPC))
            {
                break;
            }

            // Is it in range?
            if (candidateModule.loadTimeQPC <= timeQPC)
            {
                return managedModule;
            }

            --index;
        }
        return null;
    }
    /// <summary>
    /// Finds the index and module for an address that lives within the image.  If the module
    /// did not match the new entry should go at index+1.
    /// </summary>
    internal TraceLoadedModule? FindModuleAndIndexContainingAddress(Address address, long timeQPC, out int index)
    {
        modules.BinarySearch((ulong)address, out index, compareByKey);
        // Index now points at the last place where module.ImageBase <= address;
        // Search backwards from where for a module that is loaded and in range.
        int candidateIndex = index;
        while (candidateIndex >= 0)
        {
            TraceLoadedModule canidateModule = modules[candidateIndex];
            // The table contains both native modules (where the key is the image base) and managed (where it is the ModuleID)
            // We only care about the native case.
            if (canidateModule.key == canidateModule.ImageBase)
            {
                ulong candidateImageEnd = (ulong)canidateModule.ImageBase + (uint)canidateModule.ModuleFile.ImageSize;
                if ((ulong)address < candidateImageEnd)
                {
                    // Have we found a match?
                    if ((ulong)canidateModule.ImageBase <= (ulong)address)
                    {
                        if (canidateModule.loadTimeQPC <= timeQPC && timeQPC <= canidateModule.unloadTimeQPC)
                        {
                            index = candidateIndex;
                            return canidateModule;
                        }
                    }
                }
                else if (!canidateModule.overlaps)
                {
                    break;
                }
            }
            --candidateIndex;
        }
        // We return the index associated with the binary search.
        return null;
    }
    private void InsertAndSetOverlap(int moduleIndex, TraceLoadedModule module)
    {
        modules.Insert(moduleIndex, module);      // put it where it belongs in the sorted list

        // Does it overlap with the previous entry
        if (moduleIndex > 0)
        {
            var prevModule = modules[moduleIndex - 1];
            ulong prevImageEnd = (ulong)prevModule.ImageBase + (uint)prevModule.ModuleFile.ImageSize;
            if (prevImageEnd > (ulong)module.ImageBase)
            {
                prevModule.overlaps = true;
                module.overlaps = true;
            }
        }
        // does it overlap with the next entry
        if (moduleIndex + 1 < modules.Count)
        {
            var nextModule = modules[moduleIndex + 1];
            ulong moduleImageEnd = (ulong)module.ImageBase + (uint)module.ModuleFile.ImageSize;
            if (moduleImageEnd > (ulong)nextModule.ImageBase)
            {
                nextModule.overlaps = true;
                module.overlaps = true;
            }
        }

        // I should not have to look at entries further away
    }
    internal static readonly Func<ulong, TraceLoadedModule, int> compareByKey = delegate (ulong x, TraceLoadedModule y)
    {
        if (x > y.key)
        {
            return 1;
        }

        if (x < y.key)
        {
            return -1;
        }

        return 0;
    };

    [Conditional("DEBUG")]
    private void CheckClassInvarients()
    {
        // Modules better be sorted
        ulong lastkey = 0;
        TraceLoadedModule? lastModule = null;
        for (int i = 0; i < modules.Count; i++)
        {
            TraceLoadedModule module = modules[i];
            Debug.Assert(module.key != 0);
            Debug.Assert(module.key >= lastkey, "regions not sorted!");

            TraceManagedModule? asManaged = module as TraceManagedModule;
            if (asManaged != null)
            {
                Debug.Assert((ulong)asManaged.ModuleID == module.key);
            }
            else
            {
                Debug.Assert((ulong)module.ImageBase == module.key);
#if false // TODO FIX NOW enable fails on eventSourceDemo.etl file
                    if (lastModule != null && (ulong)lastModule.ImageBase + (uint)lastModule.ModuleFile.ImageSize > (ulong)module.ImageBase)
                        Debug.Assert(lastModule.overlaps && module.overlaps, "Modules overlap but don't delcare that they do");
#endif
            }
            lastkey = module.key;
            lastModule = module;
        }
    }

    internal TraceLoadedModules(TraceProcess process)
    {
        this.process = process;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }

    private TraceProcess process;
    private GrowableArray<TraceLoadedModule> modules;               // Contains unmanaged modules sorted by key
    #endregion
}
