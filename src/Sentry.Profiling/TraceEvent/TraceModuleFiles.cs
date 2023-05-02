using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// TraceModuleFiles is the list of all the ModuleFiles in the trace.   It is an IEnumerable.
/// </summary>
internal sealed class TraceModuleFiles : IEnumerable<TraceModuleFile>
{
    /// <summary>
    /// Each file is given an index for quick lookup.   Count is the
    /// maximum such index (thus you can create an array that is 1-1 with the
    /// files easily).
    /// </summary>
    public int Count { get { return moduleFiles.Count; } }
    /// <summary>
    /// Given a ModuleFileIndex, find the TraceModuleFile which also represents it
    /// </summary>
    public TraceModuleFile? this[ModuleFileIndex moduleFileIndex]
    {
        get
        {
            if (moduleFileIndex == ModuleFileIndex.Invalid)
            {
                return null;
            }

            return moduleFiles[(int)moduleFileIndex];
        }
    }
    /// <summary>
    /// Returns the TraceLog associated with this TraceModuleFiles
    /// </summary>
    public TraceLog Log { get { return log; } }

    #region private
    /// <summary>
    /// Enumerate all the files that occurred in the trace log.
    /// </summary>
    IEnumerator<TraceModuleFile> IEnumerable<TraceModuleFile>.GetEnumerator()
    {
        for (int i = 0; i < moduleFiles.Count; i++)
        {
            yield return moduleFiles[i];
        }
    }

    internal void SetModuleFileName(TraceModuleFile moduleFile, string fileName)
    {
        Debug.Assert(moduleFile.fileName == null);
        moduleFile.fileName = fileName;
        if (moduleFilesByName != null)
        {
            moduleFilesByName[fileName] = moduleFile;
        }
    }

    /// <summary>
    /// We cache information about a native image load in a TraceModuleFile.  Retrieve or create a new
    /// cache entry associated with 'nativePath' and 'moduleImageBase'.  'moduleImageBase' can be 0 for managed assemblies
    /// that were not loaded with LoadLibrary.
    /// </summary>
    internal TraceModuleFile GetOrCreateModuleFile(string? nativePath, Address imageBase)
    {
        TraceModuleFile? moduleFile = null;
        if (nativePath != null)
        {
            moduleFile = GetModuleFile(nativePath, imageBase);
        }

        if (moduleFile == null)
        {
            moduleFile = new TraceModuleFile(nativePath, imageBase, (ModuleFileIndex)moduleFiles.Count);
            moduleFiles.Add(moduleFile);
            if (nativePath != null)
            {
                if (moduleFilesByName.TryGetValue(nativePath, out var prevValue))
                {
                    moduleFile.next = prevValue;
                }

                moduleFilesByName[nativePath] = moduleFile;
            }
        }

        Debug.Assert(moduleFilesByName == null || moduleFiles.Count >= moduleFilesByName.Count);
        return moduleFile;
    }

    /// <summary>
    /// For a given file name, get the TraceModuleFile associated with it.
    /// </summary>
    internal TraceModuleFile? GetModuleFile(string fileName, Address imageBase)
    {
        TraceModuleFile? moduleFile;
        if (moduleFilesByName == null)
        {
            moduleFilesByName = new Dictionary<string, TraceModuleFile>(Math.Max(256, moduleFiles.Count + 4), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < moduleFiles.Count; i++)
            {
                moduleFile = moduleFiles[i];
                Debug.Assert(moduleFile.next == null);

                if (string.IsNullOrEmpty(moduleFile.fileName))
                {
                    continue;
                }

                if (moduleFilesByName.TryGetValue(moduleFile.fileName, out var collision))
                {
                    moduleFile.next = collision;
                }
                else
                {
                    moduleFilesByName.Add(moduleFile.fileName, moduleFile);
                }
            }
        }
        if (moduleFilesByName.TryGetValue(fileName, out moduleFile))
        {
            do
            {
                // TODO review the imageBase == 0 condition.  Needed to get PDB signature on managed IL.
                if (moduleFile.ImageBase == imageBase)
                {
                    return moduleFile;
                }
                //                    options.ConversionLog.WriteLine("WARNING: " + fileName + " loaded with two base addresses 0x" + moduleImageBase.ToString("x") + " and 0x" + moduleFile.moduleImageBase.ToString("x"));
                moduleFile = moduleFile.next;
            } while (moduleFile != null);
        }
        return moduleFile;
    }

    internal TraceModuleFiles(TraceLog log)
    {
        this.log = log;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }

    private TraceLog log;
    private Dictionary<string, TraceModuleFile> moduleFilesByName = new();
    private GrowableArray<TraceModuleFile> moduleFiles;
    #endregion
}