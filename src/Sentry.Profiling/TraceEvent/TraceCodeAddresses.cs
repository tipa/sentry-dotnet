using FastSerialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// Code addresses are so common in most traces, that having a .NET object (a TraceCodeAddress) for
/// each one is often too expensive.   As optimization, TraceLog also assigns a code address index
/// to every code address and this index uniquely identifies the code address in a very light weight fashion.
/// <para>
/// To be useful, however you need to be able to ask questions about a code address index without creating
/// a TraceCodeAddress.   This is the primary purpose of a TraceCodeAddresses (accessible from TraceLog.CodeAddresses).
/// It has a set of
/// methods that take a CodeAddressIndex and return properties of the code address (like its method, address, and module file)
/// </para>
/// </summary>
internal sealed class TraceCodeAddresses : IEnumerable<TraceCodeAddress>
{
    /// <summary>
    /// Chunk size for <see cref="codeAddressObjects"/>
    /// </summary>
    private const int ChunkSize = 4096;

    /// <summary>
    /// Returns the count of code address indexes (all code address indexes are strictly less than this).
    /// </summary>
    public int Count { get { return codeAddresses.Count; } }

    /// <summary>
    /// Given a code address index, return the name associated with it (the method name).  It will
    /// have the form MODULE!METHODNAME.   If the module name is unknown a ? is used, and if the
    /// method name is unknown a hexadecimal number is used as the method name.
    /// </summary>
    public string Name(CodeAddressIndex codeAddressIndex)
    {
        if (this.names == null)
        {
            this.names = new string[Count];
        }

        string name = this.names[(int)codeAddressIndex];

        if (name == null)
        {
            string moduleName = "?";
            ModuleFileIndex moduleIdx = ModuleFileIndex(codeAddressIndex);
            if (moduleFiles[moduleIdx]?.Name is { } newModuleName)
            {
                moduleName = newModuleName;
            }

            string methodName;
            MethodIndex methodIndex = MethodIndex(codeAddressIndex);
            if (methodIndex != Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
            {
                methodName = Methods.FullMethodName(methodIndex);
            }
            else
            {
                methodName = "0x" + ((ulong)Address(codeAddressIndex)).ToString("x");
            }

            this.names[(int)codeAddressIndex] = name = moduleName + "!" + methodName;
        }

        return name;
    }
    /// <summary>
    /// Given a code address index, returns the virtual address of the code in the process.
    /// </summary>
    public Address Address(CodeAddressIndex codeAddressIndex) { return codeAddresses[(int)codeAddressIndex].Address; }
    /// <summary>
    /// Given a code address index, returns the index for the module file (representing the file's path)
    /// </summary>
    public ModuleFileIndex ModuleFileIndex(CodeAddressIndex codeAddressIndex)
    {
        var ret = codeAddresses[(int)codeAddressIndex].GetModuleFileIndex(this);
        // If we have a method index, fetch the module file from the method.
        if (ret == Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid)
        {
            ret = Methods.MethodModuleFileIndex(MethodIndex(codeAddressIndex));
        }

        return ret;
    }
    /// <summary>
    /// Given a code address index, returns the index for the method associated with the code address (it may return MethodIndex.Invalid
    /// if no method can be found).
    /// </summary>
    public MethodIndex MethodIndex(CodeAddressIndex codeAddressIndex) { return codeAddresses[(int)codeAddressIndex].GetMethodIndex(this); }
    /// <summary>
    /// Given a code address index, returns the module file (the DLL paths) associated with it
    /// </summary>
    public TraceModuleFile? ModuleFile(CodeAddressIndex codeAddressIndex) { return ModuleFiles[ModuleFileIndex(codeAddressIndex)]; }
    /// <summary>
    /// If the code address is associated with managed code, return the IL offset within the method.    If the method
    /// is unmanaged -1 is returned.   To determine the IL offset the PDB for the NGEN image (for NGENed code) or the
    /// correct .NET events (for JIT compiled code) must be present.   If this information is not present -1 is returned.
    /// </summary>
    public int ILOffset(CodeAddressIndex codeAddressIndex)
    {
        ILToNativeMap? ilMap = NativeMap(codeAddressIndex);
        if (ilMap == null)
        {
            return -1;
        }

        return ilMap.GetILOffsetForNativeAddress(Address(codeAddressIndex));
    }

    public OptimizationTier OptimizationTier(CodeAddressIndex codeAddressIndex)
    {
        Debug.Assert((int)codeAddressIndex < codeAddresses.Count);
        return codeAddresses[(int)codeAddressIndex].optimizationTier;
    }

    /// <summary>
    /// Given a code address index, returns a TraceCodeAddress for it.
    /// </summary>
    public TraceCodeAddress? this[CodeAddressIndex codeAddressIndex]
    {
        get
        {
            if (codeAddressIndex == CodeAddressIndex.Invalid)
            {
                return null;
            }

            int chunk = (int)codeAddressIndex / ChunkSize;
            int offset = (int)codeAddressIndex % ChunkSize;

            if (this.codeAddressObjects == null)
            {
                this.codeAddressObjects = new TraceCodeAddress[chunk + 1][];
            }
            else if (chunk >= this.codeAddressObjects.Length)
            {
                Array.Resize(ref this.codeAddressObjects, Math.Max(this.codeAddressObjects.Length * 2, chunk + 1));
            }

            TraceCodeAddress[] data = this.codeAddressObjects[chunk];

            if (data == null)
            {
                data = this.codeAddressObjects[chunk] = new TraceCodeAddress[ChunkSize];
            }

            TraceCodeAddress ret = data[offset];

            if (ret == null)
            {
                ret = new TraceCodeAddress(this, codeAddressIndex);
                data[offset] = ret;
            }

            return ret;
        }
    }

    /// <summary>
    /// Returns the TraceMethods object that can look up information from MethodIndexes
    /// </summary>
    public TraceMethods Methods { get { return methods; } }
    /// <summary>
    /// Returns the TraceModuleFiles that can look up information about ModuleFileIndexes
    /// </summary>
    public TraceModuleFiles ModuleFiles { get { return moduleFiles; } }
    /// <summary>
    /// Indicates the number of managed method records that were encountered.  This is useful to understand if symbolic information 'mostly works'.
    /// </summary>
    public int ManagedMethodRecordCount { get { return managedMethodRecordCount; } }
    // /// <summary>
    // /// Initially CodeAddresses for unmanaged code will have no useful name.  Calling LookupSymbolsForModule
    // /// lets you resolve the symbols for a particular file so that the TraceCodeAddresses for that DLL
    // /// will have Methods (useful names) associated with them.
    // /// </summary>
    // public void LookupSymbolsForModule(SymbolReader reader, TraceModuleFile file)
    // {
    //     var codeAddrs = new List<CodeAddressIndex>();
    //     for (int i = 0; i < Count; i++)
    //     {
    //         if (codeAddresses[i].GetModuleFileIndex(this) == file.ModuleFileIndex &&
    //             codeAddresses[i].GetMethodIndex(this) == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //         {
    //             codeAddrs.Add((CodeAddressIndex)i);
    //         }
    //     }

    //     if (codeAddrs.Count == 0)
    //     {
    //         reader.m_log.WriteLine("No code addresses are in {0} that have not already been looked up.", file.Name);
    //         return;
    //     }

    //     // sort them.  TODO can we get away without this?
    //     codeAddrs.Sort(delegate (CodeAddressIndex x, CodeAddressIndex y)
    //     {
    //         ulong addrX = (ulong)Address(x);
    //         ulong addrY = (ulong)Address(y);
    //         if (addrX > addrY)
    //         {
    //             return 1;
    //         }

    //         if (addrX < addrY)
    //         {
    //             return -1;
    //         }

    //         return 0;
    //     });

    //     int totalAddressCount;

    //     // Skip to the addresses in this module
    //     var codeAddrEnum = codeAddrs.GetEnumerator();
    //     for (; ; )
    //     {
    //         if (!codeAddrEnum.MoveNext())
    //         {
    //             return;
    //         }

    //         if (Address(codeAddrEnum.Current) >= file.ImageBase)
    //         {
    //             break;
    //         }
    //     }
    //     try
    //     {
    //         LookupSymbolsForModule(reader, file, codeAddrEnum, true, out totalAddressCount);
    //     }
    //     catch (OutOfMemoryException)
    //     {
    //         // TODO find out why this happens?   I think this is because we try to do a ReadRVA
    //         // a managed-only module
    //         reader.m_log.WriteLine("Error: Caught out of memory exception on file " + file.Name + ".   Skipping.");
    //     }
    //     catch (Exception e)
    //     {
    //         reader.m_log.WriteLine("An exception occurred during symbol lookup.  Continuing...");
    //         reader.m_log.WriteLine("Exception: " + e.ToString());
    //     }
    // }
    // /// <summary>
    // /// A TraceCodeAddress can contain a method name, but does not contain number information.   To
    // /// find line number information you must read the PDB again and fetch it.   This is what
    // /// GetSoruceLine does.
    // /// <para>
    // /// Given a SymbolReader (which knows how to look up PDBs) and a code address index (which
    // /// represent a particular point in execution), find a SourceLocation (which represents a
    // /// particular line number in a particular source file associated with the code address.
    // /// Returns null if anything goes wrong (and diagnostic information will be written to the
    // /// log file associated with the SymbolReader.
    // /// </para>
    // /// </summary>
    // public SourceLocation GetSourceLine(SymbolReader reader, CodeAddressIndex codeAddressIndex)
    // {
    //     reader.m_log.WriteLine("GetSourceLine: Getting source line for code address index {0:x}", codeAddressIndex);

    //     if (codeAddressIndex == CodeAddressIndex.Invalid)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Invalid code address");
    //         return null;
    //     }

    //     var moduleFile = log.CodeAddresses.ModuleFile(codeAddressIndex);
    //     if (moduleFile == null)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Could not find moduleFile {0:x}.", log.CodeAddresses.Address(codeAddressIndex));
    //         return null;
    //     }

    //     NativeSymbolModule windowsSymbolModule;
    //     ManagedSymbolModule ilSymbolModule;
    //     // Is this address in the native code of the module (inside the bounds of module)
    //     var address = log.CodeAddresses.Address(codeAddressIndex);
    //     reader.m_log.WriteLine("GetSourceLine: address for code address is {0:x} module {1}", address, moduleFile.Name);
    //     if (moduleFile.ImageBase != 0 && moduleFile.ImageBase <= address && address < moduleFile.ImageEnd)
    //     {
    //         var methodRva = (uint)(address - moduleFile.ImageBase);
    //         reader.m_log.WriteLine("GetSourceLine: address within module: native case, VA = {0:x}, ImageBase = {1:x}, RVA = {2:x}", address, moduleFile.ImageBase, methodRva);
    //         windowsSymbolModule = OpenPdbForModuleFile(reader, moduleFile) as NativeSymbolModule;
    //         if (windowsSymbolModule != null)
    //         {
    //             string ilAssemblyName;
    //             uint ilMetaDataToken;
    //             int ilMethodOffset;

    //             var ret = windowsSymbolModule.SourceLocationForRva(methodRva, out ilAssemblyName, out ilMetaDataToken, out ilMethodOffset);
    //             if (ret == null && ilAssemblyName != null)
    //             {
    //                 // We found the RVA, but this is an NGEN image, and so we could not convert it completely to a line number.
    //                 // Look up the IL PDB needed
    //                 reader.m_log.WriteLine("GetSourceLine:  Found mapping from Native to IL assembly {0} Token 0x{1:x} offset 0x{2:x}",
    //                 ilAssemblyName, ilMetaDataToken, ilMethodOffset);
    //                 if (moduleFile.ManagedModule != null)
    //                 {
    //                     // In CoreCLR, the managed image IS the native image, so has a .ni suffix, remove it if present.
    //                     var moduleFileName = moduleFile.ManagedModule.Name;
    //                     if (moduleFileName.EndsWith(".ni", StringComparison.OrdinalIgnoreCase) || moduleFileName.EndsWith(".il", StringComparison.OrdinalIgnoreCase))
    //                     {
    //                         moduleFileName = moduleFileName.Substring(0, moduleFileName.Length - 3);
    //                     }

    //                     // TODO FIX NOW work for any assembly, not just he corresponding IL assembly.
    //                     if (string.Compare(moduleFileName, ilAssemblyName, StringComparison.OrdinalIgnoreCase) == 0)
    //                     {
    //                         TraceModuleFile ilAssemblyModule = moduleFile.ManagedModule;
    //                         ilSymbolModule = OpenPdbForModuleFile(reader, ilAssemblyModule);
    //                         if (ilSymbolModule != null)
    //                         {
    //                             reader.m_log.WriteLine("GetSourceLine: Found PDB for IL module {0}", ilSymbolModule.SymbolFilePath);
    //                             ret = ilSymbolModule.SourceLocationForManagedCode(ilMetaDataToken, ilMethodOffset);
    //                         }
    //                     }
    //                     else
    //                     {
    //                         reader.m_log.WriteLine("GetSourceLine: found IL assembly name {0} != load assembly {1} ({2}) Giving up",
    //                             ilAssemblyName, moduleFileName, moduleFile.ManagedModule.FilePath);
    //                     }
    //                 }
    //                 else
    //                 {
    //                     reader.m_log.WriteLine("GetSourceLine: Could not find managed module for NGEN image {0}", moduleFile.FilePath);
    //                 }
    //             }

    //             // TODO FIX NOW, deal with this rather than simply warn.
    //             if (ret == null && windowsSymbolModule.SymbolFilePath.EndsWith(".ni.pdb", StringComparison.OrdinalIgnoreCase))
    //             {
    //                 reader.m_log.WriteLine("GetSourceLine: Warning could not find line information in {0}", windowsSymbolModule.SymbolFilePath);
    //                 reader.m_log.WriteLine("GetSourceLine: Maybe because the NGEN pdb was generated without being able to reach the IL PDB");
    //                 reader.m_log.WriteLine("GetSourceLine: If you are on the machine where the data was collected, deleting the file may help");
    //             }

    //             return ret;
    //         }
    //         reader.m_log.WriteLine("GetSourceLine: Failed to look up {0:x} in a PDB, checking for JIT", log.CodeAddresses.Address(codeAddressIndex));
    //     }

    //     // The address is not in the module, or we could not find the PDB, see if we have JIT information
    //     var methodIndex = log.CodeAddresses.MethodIndex(codeAddressIndex);
    //     if (methodIndex == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Could not find method for {0:x}", log.CodeAddresses.Address(codeAddressIndex));
    //         return null;
    //     }

    //     var methodToken = log.CodeAddresses.Methods.MethodToken(methodIndex);
    //     if (methodToken == 0)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Could not find method for {0:x}", log.CodeAddresses.Address(codeAddressIndex));
    //         return null;
    //     }
    //     reader.m_log.WriteLine("GetSourceLine: Found JITTed method {0}, index {1:x} token {2:x}",
    //         log.CodeAddresses.Methods.FullMethodName(methodIndex), methodIndex, methodToken);

    //     // See if we have il offset information for the method.
    //     // var ilOffset = log.CodeAddresses.ILOffset(codeAddressIndex);
    //     var ilMap = log.CodeAddresses.NativeMap(codeAddressIndex);
    //     int ilOffset = 0;
    //     if (ilMap != null)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Found an il-to-native mapping MethodIdx {0:x} Start {1:x} Len {2:x}",
    //             ilMap.MethodIndex, ilMap.MethodStart, ilMap.MethodLength);

    //         // TODO remove after we are happy that this works properly.
    //         //for (int i = 0; i < ilMap.Map.Count; i++)
    //         //    reader.m_log.WriteLine("GetSourceLine:    {0,3} native {1,5:x} -> {2:x}",
    //         //        i, ilMap.Map[i].NativeOffset, ilMap.Map[i].ILOffset);

    //         ilOffset = ilMap.GetILOffsetForNativeAddress(address);
    //         reader.m_log.WriteLine("GetSourceLine: NativeOffset {0:x} ILOffset = {1:x}", address - ilMap.MethodStart, ilOffset);

    //         if (ilOffset < 0)
    //         {
    //             ilOffset = 0;       // If we return the special ILProlog or ILEpilog values.
    //         }
    //     }

    //     // Get the IL file even if we are in an NGEN image.
    //     if (moduleFile.ManagedModule != null)
    //     {
    //         moduleFile = moduleFile.ManagedModule;
    //     }

    //     ilSymbolModule = OpenPdbForModuleFile(reader, moduleFile);
    //     if (ilSymbolModule == null)
    //     {
    //         reader.m_log.WriteLine("GetSourceLine: Failed to look up PDB for {0}", moduleFile.FilePath);
    //         return null;
    //     }

    //     return ilSymbolModule.SourceLocationForManagedCode((uint)methodToken, ilOffset);
    // }
    /// <summary>
    /// The number of times a particular code address appears in the log.   Unlike TraceCodeAddresses.Count, which tries
    /// to share a code address as much as possible, TotalCodeAddresses counts the same code address in different
    /// call stacks (and even if in the same stack) as distinct.    This makes TotalCodeAddresses a better measure of
    /// the 'popularity' of a particular address (which can factor into decisions about whether to call LookupSymbolsForModule)
    /// <para>
    /// The sum of ModuleFile.CodeAddressesInModule for all modules should sum to this number.
    /// </para>
    /// </summary>
    public int TotalCodeAddresses { get { return totalCodeAddresses; } }
    /// <summary>
    /// If set to true, will only use the name of the module and not the PDB GUID to confirm that a PDB is correct
    /// for a given DLL.   Setting this value is dangerous because it is easy for the PDB to be for a different
    /// version of the DLL and thus give inaccurate method names.   Nevertheless, if a log file has no PDB GUID
    /// information associated with it, unsafe PDB matching is the only way to get at least some symbolic information.
    /// </summary>
    public bool UnsafePDBMatching { get; set; }

    #region private
    /// <summary>
    /// We expose ILToNativeMap internally so we can do diagnostics.
    /// </summary>
    internal ILToNativeMap? NativeMap(CodeAddressIndex codeAddressIndex)
    {
        var ilMapIdx = codeAddresses[(int)codeAddressIndex].GetILMapIndex(this);
        if (ilMapIdx == ILMapIndex.Invalid)
        {
            return null;
        }

        return ILToNativeMaps[(int)ilMapIdx];
    }

    internal IEnumerable<CodeAddressIndex> GetAllIndexes
    {
        get
        {
            for (int i = 0; i < Count; i++)
            {
                yield return (CodeAddressIndex)i;
            }
        }
    }

    internal TraceCodeAddresses(TraceLog log, TraceModuleFiles moduleFiles)
    {
        this.log = log;
        this.moduleFiles = moduleFiles;
        methods = new TraceMethods(this);
    }

    /// <summary>
    /// IEnumerable support.
    /// </summary>
    public IEnumerator<TraceCodeAddress> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[(CodeAddressIndex)i]!;
        }
    }

    /// <summary>
    /// Called when JIT CLR Rundown events are processed. It will look if there is any
    /// address that falls into the range of the JIT compiled method and if so log the
    /// symbolic information (otherwise we simply ignore it)
    /// </summary>
    internal void AddMethod(MethodLoadUnloadVerboseTraceData data)
    {
        managedMethodRecordCount++;
        MethodIndex methodIndex = Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
        ILMapIndex ilMap = ILMapIndex.Invalid;
        ModuleFileIndex moduleFileIndex = Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid;
        TraceManagedModule? module = null;
        ForAllUnresolvedCodeAddressesInRange(log.process, data.MethodStartAddress, data.MethodSize, true, delegate (ref CodeAddressInfo info)
        {
            // If we already resolved, that means that the address was reused, so only add something if it does not already have
            // information associated with it.
            if (info.GetMethodIndex(this) == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
            {
                // Lazily create the method since many methods never have code samples in them.
                if (module == null)
                {
#pragma warning disable 612, 618 // disable error for use of obsolete data.TimeStampQPC
                    module = log.process.LoadedModules.GetOrCreateManagedModule(data.ModuleID, data.TimeStampQPC);
#pragma warning restore 612,618
                    moduleFileIndex = module.ModuleFile.ModuleFileIndex;
                    methodIndex = methods.NewMethod(TraceLog.GetFullName(data), moduleFileIndex, data.MethodToken);
                    if (data.IsJitted)
                    {
                        ilMap = UnloadILMapForMethod(methodIndex, data);
                    }
                }
                // Set the info
                info.SetMethodIndex(this, methodIndex);
                if (ilMap != ILMapIndex.Invalid)
                {
                    info.SetILMapIndex(this, ilMap);
                }
                info.SetOptimizationTier(data.OptimizationTier);
            }
        });
    }

    // /// <summary>
    // /// Adds a JScript method
    // /// </summary>
    // internal void AddMethod(MethodLoadUnloadJSTraceData data, Dictionary<JavaScriptSourceKey, string> sourceById)
    // {
    //     MethodIndex methodIndex = Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
    //     TraceProcess process = log.Processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
    //     ForAllUnresolvedCodeAddressesInRange(process, data.MethodStartAddress, (int)data.MethodSize, true, delegate (ref CodeAddressInfo info)
    //         {
    //             // If we already resolved, that means that the address was reused, so only add something if it does not already have
    //             // information associated with it.
    //             if (info.GetMethodIndex(this) == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //             {
    //                 // Lazily create the method since many methods never have code samples in them.
    //                 if (methodIndex == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //                 {
    //                     methodIndex = MakeJavaScriptMethod(data, sourceById);
    //                 }
    //                 // Set the info
    //                 info.SetMethodIndex(this, methodIndex);
    //             }
    //         });
    // }

    // internal MethodIndex MakeJavaScriptMethod(MethodLoadUnloadJSTraceData data, Dictionary<JavaScriptSourceKey, string> sourceById)
    // {
    //     string sourceName = null;
    //     /* TODO FIX NOW decide what to do here */
    //     if (sourceById.TryGetValue(new JavaScriptSourceKey(data.SourceID, data.ScriptContextID), out sourceName))
    //     {
    //         var lastSlashIdx = sourceName.LastIndexOf('/');
    //         if (0 < lastSlashIdx)
    //         {
    //             sourceName = sourceName.Substring(lastSlashIdx + 1);
    //         }
    //     }
    //     if (sourceName == null)
    //     {
    //         sourceName = "JAVASCRIPT";
    //     }

    //     var methodName = data.MethodName;
    //     if (data.Line != 0)
    //     {
    //         methodName = methodName + " Line: " + data.Line.ToString();
    //     }

    //     var moduleFile = log.ModuleFiles.GetOrCreateModuleFile(sourceName, 0);
    //     return methods.NewMethod(methodName, moduleFile.ModuleFileIndex, data.MethodID);
    // }

    internal delegate void ForAllCodeAddrAction(ref CodeAddressInfo codeAddrInfo);
    /// <summary>
    /// Allows you to get a callback for each code address that is in the range from start to
    /// start+length within the process 'process'.   If 'considerResolved' is true' then the address range
    /// is considered resolved and future calls to this routine will not find the addresses (since they are resolved).
    /// </summary>
    internal void ForAllUnresolvedCodeAddressesInRange(TraceProcess process, Address start, int length, bool considerResolved, ForAllCodeAddrAction body)
    {
        if (process.codeAddressesInProcess == null)
        {
            return;
        }

        Debug.Assert(process.unresolvedCodeAddresses.Count <= process.codeAddressesInProcess.Count);
        Debug.Assert(process.ProcessID == 0 || process.unresolvedCodeAddresses.Count == process.codeAddressesInProcess.Count);

        // Trace.WriteLine(string.Format("Looking up code addresses from {0:x} len {1:x} in process {2} ({3})", start, length, process.Name, process.ProcessID));
        if (!process.unresolvedCodeAddressesIsSorted)
        {
            // Trace.WriteLine(string.Format("Sorting {0} unresolved code addresses for process {1} ({2})", process.unresolvedCodeAddresses.Count, process.Name, process.ProcessID));
            process.unresolvedCodeAddresses.Sort(
                (CodeAddressIndex x, CodeAddressIndex y) => codeAddresses[(int)x].Address.CompareTo(codeAddresses[(int)y].Address));
            process.unresolvedCodeAddressesIsSorted = true;
        }

        // Since we know we are sorted, we do a binary search to find the first code address.
        int startIdx;
        process.unresolvedCodeAddresses.BinarySearch(start, out startIdx, (addr, codeIdx) => addr.CompareTo(codeAddresses[(int)codeIdx].Address));
        if (startIdx < 0)
        {
            startIdx++;
        }

        bool removeAddressAfterCallback = (process.ProcessID != 0);      // We remove entries unless it is the kernel (process 0) after calling back

        // since the DLL will be unloaded in that process. Kernel DLLS stay loaded.
        // Call back for ever code address >= start than that, and then remove any code addresses we called back on.
        Address end = start + (ulong)length;
        int curIdx = startIdx;
        while (curIdx < process.unresolvedCodeAddresses.Count)
        {
            CodeAddressIndex codeAddrIdx = process.unresolvedCodeAddresses[curIdx];
            Address codeAddr = codeAddresses[(int)codeAddrIdx].Address;
            if (end <= codeAddr)
            {
                break;
            }

            body(ref codeAddresses.UnderlyingArray[(int)codeAddrIdx]);
            if (considerResolved && removeAddressAfterCallback)
            {
                process.codeAddressesInProcess.Remove(codeAddr);
            }

            curIdx++;
        }

        if (considerResolved && curIdx != startIdx)
        {
            // OK we called back on the code addresses in the range.   Remove what we just iterated over in bulk.
            // Trace.WriteLine(string.Format("Removing {0} unresolved code addresses out of {1} because of range {2:x} len {3:x} from process {4} ({5})",
            //     curIdx - startIdx, process.unresolvedCodeAddresses.Count, start, length, process.Name, process.ProcessID));
            process.unresolvedCodeAddresses.RemoveRange(startIdx, curIdx - startIdx);
            Debug.Assert(process.unresolvedCodeAddresses.Count <= process.codeAddressesInProcess.Count);
            Debug.Assert(process.ProcessID == 0 || process.unresolvedCodeAddresses.Count == process.codeAddressesInProcess.Count);
        }
    }

    /// <summary>
    /// Gets the symbolic information entry for 'address' which can be any address.  If it falls in the
    /// range of a symbol, then that symbolic information is returned.  Regardless of whether symbolic
    /// information is found, however, an entry is created for it, so every unique address has an entry
    /// in this table.
    /// </summary>
    internal CodeAddressIndex GetOrCreateCodeAddressIndex(TraceProcess process, Address address)
    {
        // See if it is a kernel address, if so use process 0 instead of the current process
        process = ProcessForAddress(process, address);

        CodeAddressIndex ret;
        if (process.codeAddressesInProcess == null)
        {
            process.codeAddressesInProcess = new Dictionary<Address, CodeAddressIndex>();
        }

        if (!process.codeAddressesInProcess.TryGetValue(address, out ret))
        {
            ret = (CodeAddressIndex)codeAddresses.Count;
            codeAddresses.Add(new CodeAddressInfo(address, process.ProcessIndex));
            process.codeAddressesInProcess[address] = ret;

            // Trace.WriteLine(string.Format("Adding new code address for address {0:x} for process {1} ({2})",  address, process.Name, process.ProcessID));
            process.unresolvedCodeAddressesIsSorted = false;
            process.unresolvedCodeAddresses.Add(ret);
            Debug.Assert(process.ProcessID == 0 || process.unresolvedCodeAddresses.Count == process.codeAddressesInProcess.Count);
        }

        codeAddresses.UnderlyingArray[(int)ret].UpdateStats();
        return ret;
    }

    /// <summary>
    /// All processes might have kernel addresses in them, this returns the kernel process (process ID == 0) if 'address' is a kernel address.
    /// </summary>
    private TraceProcess ProcessForAddress(TraceProcess process, Address address)
    {
        // if (process.IsKernelAddress(address, log.pointerSize))
        // {
        //     return log.Processes.GetOrCreateProcess(0, log.sessionStartTimeQPC);
        // }

        return process;
    }

    // TODO do we need this?
    /// <summary>
    /// Sort from lowest address to highest address.
    /// </summary>
    private IEnumerable<CodeAddressIndex> GetSortedCodeAddressIndexes()
    {
        List<CodeAddressIndex> list = new List<CodeAddressIndex>(GetAllIndexes);
        list.Sort(delegate (CodeAddressIndex x, CodeAddressIndex y)
        {
            ulong addrX = (ulong)Address(x);
            ulong addrY = (ulong)Address(y);
            if (addrX > addrY)
            {
                return 1;
            }

            if (addrX < addrY)
            {
                return -1;
            }

            return 0;
        });
        return list;
    }

    // /// <summary>
    // /// Do symbol resolution for all addresses in the log file.
    // /// </summary>
    // internal void LookupSymbols(TraceLogOptions options)
    // {
    //     SymbolReader reader = null;
    //     int totalAddressCount = 0;
    //     int noModuleAddressCount = 0;
    //     IEnumerator<CodeAddressIndex> codeAddressIndexCursor = GetSortedCodeAddressIndexes().GetEnumerator();
    //     bool notDone = codeAddressIndexCursor.MoveNext();
    //     while (notDone)
    //     {
    //         TraceModuleFile moduleFile = moduleFiles[ModuleFileIndex(codeAddressIndexCursor.Current)];
    //         if (moduleFile != null)
    //         {
    //             if (options.ShouldResolveSymbols != null && options.ShouldResolveSymbols(moduleFile.FilePath))
    //             {
    //                 if (reader == null)
    //                 {
    //                     var symPath = SymbolPath.CleanSymbolPath();
    //                     if (options.LocalSymbolsOnly)
    //                     {
    //                         symPath = symPath.LocalOnly();
    //                     }

    //                     var path = symPath.ToString();
    //                     options.ConversionLog.WriteLine("_NT_SYMBOL_PATH={0}", path);
    //                     reader = new SymbolReader(options.ConversionLog, path);
    //                 }
    //                 int moduleAddressCount = 0;
    //                 try
    //                 {
    //                     notDone = true;
    //                     LookupSymbolsForModule(reader, moduleFile, codeAddressIndexCursor, false, out moduleAddressCount);
    //                 }
    //                 catch (OutOfMemoryException)
    //                 {
    //                     options.ConversionLog.WriteLine("Hit Symbol reader out of memory issue.   Skipping that module.");
    //                 }
    //                 catch (Exception e)
    //                 {
    //                     // TODO too strong.
    //                     options.ConversionLog.WriteLine("An exception occurred during symbol lookup.  Continuing...");
    //                     options.ConversionLog.WriteLine("Exception: " + e.Message);
    //                 }
    //                 totalAddressCount += moduleAddressCount;
    //             }

    //             // Skip the rest of the addresses for that module.
    //             while ((moduleFiles[ModuleFileIndex(codeAddressIndexCursor.Current)] == moduleFile))
    //             {
    //                 notDone = codeAddressIndexCursor.MoveNext();
    //                 if (!notDone)
    //                 {
    //                     break;
    //                 }

    //                 totalAddressCount++;
    //             }
    //         }
    //         else
    //         {
    //             // TraceLog.DebugWarn("Could not find a module for address " + ("0x" + Address(codeAddressIndexCursor.Current).ToString("x")).PadLeft(10));
    //             notDone = codeAddressIndexCursor.MoveNext();
    //             noModuleAddressCount++;
    //             totalAddressCount++;
    //         }
    //     }

    //     if (reader != null)
    //     {
    //         reader.Dispose();
    //     }

    //     double noModulePercent = 0;
    //     if (totalAddressCount > 0)
    //     {
    //         noModulePercent = noModuleAddressCount * 100.0 / totalAddressCount;
    //     }

    //     options.ConversionLog.WriteLine("A total of " + totalAddressCount + " symbolic addresses were looked up.");
    //     options.ConversionLog.WriteLine("Addresses outside any module: " + noModuleAddressCount + " out of " + totalAddressCount + " (" + noModulePercent.ToString("f1") + "%)");
    //     options.ConversionLog.WriteLine("Done with symbolic lookup.");
    // }

    // // TODO number of args is getting messy.
    // private void LookupSymbolsForModule(SymbolReader reader, TraceModuleFile moduleFile,
    //     IEnumerator<CodeAddressIndex> codeAddressIndexCursor, bool enumerateAll, out int totalAddressCount)
    // {
    //     totalAddressCount = 0;
    //     int existingSymbols = 0;
    //     int distinctSymbols = 0;
    //     int unmatchedSymbols = 0;
    //     int repeats = 0;

    //     // We can get the same name for different addresses, which makes us for distinct methods
    //     // which in turn cause the treeview to have multiple children with the same name.   This
    //     // is confusing, so we intern the symbols, ensuring that code address with the same name
    //     // always use the same method.   This dictionary does that.
    //     var methodIntern = new Dictionary<string, MethodIndex>();

    //     reader.m_log.WriteLine("[Loading symbols for " + moduleFile.FilePath + "]");

    //     NativeSymbolModule moduleReader = OpenPdbForModuleFile(reader, moduleFile) as NativeSymbolModule;
    //     if (moduleReader == null)
    //     {
    //         reader.m_log.WriteLine("Could not find PDB file.");
    //         return;
    //     }

    //     reader.m_log.WriteLine("Loaded, resolving symbols");


    //     string currentMethodName = "";
    //     MethodIndex currentMethodIndex = Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
    //     Address currentMethodEnd = 0;
    //     Address endModule = moduleFile.ImageEnd;
    //     for (; ; )
    //     {
    //         // options.ConversionLog.WriteLine("Code address = " + Address(codeAddressIndexCursor.Current).ToString("x"));
    //         totalAddressCount++;
    //         Address address = Address(codeAddressIndexCursor.Current);
    //         if (!enumerateAll && address >= endModule)
    //         {
    //             break;
    //         }

    //         MethodIndex methodIndex = MethodIndex(codeAddressIndexCursor.Current);
    //         if (methodIndex == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //         {
    //             if (address < currentMethodEnd)
    //             {
    //                 repeats++;
    //                 // options.ConversionLog.WriteLine("Repeat of " + currentMethodName + " at " + address.ToString("x"));
    //             }
    //             else
    //             {
    //                 uint symbolStart = 0;
    //                 var newMethodName = moduleReader.FindNameForRva((uint)(address - moduleFile.ImageBase), ref symbolStart);
    //                 if (newMethodName.Length > 0)
    //                 {
    //                     // TODO FIX NOW
    //                     // Debug.WriteLine(string.Format("Info: address  0x{0:x} in sym {1}", address, newMethodName));
    //                     // TODO FIX NOW
    //                     currentMethodEnd = address + 1;     // Look up each unique address.

    //                     // TODO FIX NOW remove
    //                     // newMethodName = newMethodName +  " 0X" + address.ToString("x");

    //                     // If we get the exact same method name, then again we have a repeat
    //                     // In theory this should not happen, but in it seems to happen in
    //                     // practice.
    //                     if (newMethodName == currentMethodName)
    //                     {
    //                         repeats++;
    //                     }
    //                     else
    //                     {
    //                         currentMethodName = newMethodName;
    //                         if (!methodIntern.TryGetValue(newMethodName, out currentMethodIndex))
    //                         {
    //                             currentMethodIndex = methods.NewMethod(newMethodName, moduleFile.ModuleFileIndex, -(int)symbolStart);
    //                             methodIntern[newMethodName] = currentMethodIndex;
    //                             distinctSymbols++;
    //                         }
    //                     }
    //                 }
    //                 else
    //                 {
    //                     unmatchedSymbols++;
    //                     currentMethodName = "";
    //                     currentMethodIndex = Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
    //                 }
    //             }

    //             if (currentMethodIndex != Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
    //             {
    //                 CodeAddressInfo codeAddressInfo = codeAddresses[(int)codeAddressIndexCursor.Current];
    //                 Debug.Assert(codeAddressInfo.GetModuleFileIndex(this) == moduleFile.ModuleFileIndex);
    //                 codeAddressInfo.SetMethodIndex(this, currentMethodIndex);
    //                 Debug.Assert(moduleFile.ModuleFileIndex != Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid);
    //                 codeAddresses[(int)codeAddressIndexCursor.Current] = codeAddressInfo;
    //             }
    //         }
    //         else
    //         {
    //             // options.ConversionLog.WriteLine("Found existing method " + Methods[methodIndex].FullMethodName);
    //             existingSymbols++;
    //         }

    //         if (!codeAddressIndexCursor.MoveNext())
    //         {
    //             break;
    //         }
    //     }
    //     reader.m_log.WriteLine("    Addresses to look up       " + totalAddressCount);
    //     if (existingSymbols != 0)
    //     {
    //         reader.m_log.WriteLine("        Existing Symbols       " + existingSymbols);
    //     }

    //     reader.m_log.WriteLine("        Found Symbols          " + (distinctSymbols + repeats));
    //     reader.m_log.WriteLine("        Distinct Found Symbols " + distinctSymbols);
    //     reader.m_log.WriteLine("        Unmatched Symbols " + (totalAddressCount - (distinctSymbols + repeats)));
    // }

    // /// <summary>
    // /// Look up the SymbolModule (open PDB) for a given moduleFile.   Will generate NGEN pdbs as needed.
    // /// </summary>
    // private unsafe ManagedSymbolModule OpenPdbForModuleFile(SymbolReader symReader, TraceModuleFile moduleFile)
    // {
    //     string pdbFileName = null;
    //     // If we have a signature, use it
    //     if (moduleFile.PdbSignature != Guid.Empty)
    //     {
    //         pdbFileName = symReader.FindSymbolFilePath(moduleFile.PdbName, moduleFile.PdbSignature, moduleFile.PdbAge, moduleFile.FilePath, moduleFile.ProductVersion, true);
    //     }
    //     else
    //     {
    //         symReader.m_log.WriteLine("No PDB signature for {0} in trace.", moduleFile.FilePath);
    //     }

    //     if (pdbFileName == null)
    //     {
    //         // Confirm that the path from the trace points at a file that is the same (checksums match).
    //         // It will log messages if it does not match.
    //         if (TraceModuleUnchanged(moduleFile, symReader.m_log))
    //         {
    //             pdbFileName = symReader.FindSymbolFilePathForModule(moduleFile.FilePath);
    //         }
    //     }

    //     if (pdbFileName == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    //     {
    //         // Check to see if the file is inside of an existing Windows container.
    //         // Create a new instance of WindowsDeviceToVolumeMap to avoid situations where the mappings have changed but we haven't noticed.
    //         string volumePath = new WindowsDeviceToVolumeMap().ConvertDevicePathToVolumePath(moduleFile.FilePath);
    //         symReader.m_log.WriteLine("Attempting to convert {0} to a volume-based path in case the file inside of a container.", moduleFile.FilePath);
    //         if (!moduleFile.FilePath.Equals(volumePath))
    //         {
    //             symReader.m_log.WriteLine("Successfully converted to {0}.", volumePath);

    //             // Confirm that the path from the trace points at a file that is the same (checksums match).
    //             // It will log messages if it does not match.
    //             if (TraceModuleUnchanged(moduleFile, symReader.m_log, volumePath))
    //             {
    //                 pdbFileName = symReader.FindSymbolFilePathForModule(volumePath);
    //             }
    //         }
    //         else
    //         {
    //             symReader.m_log.WriteLine("Unable to convert {0} to a volume-based path.", moduleFile.FilePath);
    //         }
    //     }

    //     if (pdbFileName == null)
    //     {
    //         if (UnsafePDBMatching)
    //         {
    //             var pdbSimpleName = Path.GetFileNameWithoutExtension(moduleFile.FilePath) + ".pdb";
    //             symReader.m_log.WriteLine("The /UnsafePdbMatch specified.  Looking for {0} using only the file name to validate a match.", pdbSimpleName);
    //             pdbFileName = symReader.FindSymbolFilePath(pdbSimpleName, Guid.Empty, 0);
    //         }
    //     }

    //     if (pdbFileName == null)
    //     {
    //         // We are about to fail.   output helpful warnings.
    //         if (moduleFile.PdbSignature == Guid.Empty)
    //         {
    //             if (log.PointerSize == 8 && moduleFile.FilePath.IndexOf(@"\windows\System32", StringComparison.OrdinalIgnoreCase) >= 0)
    //             {
    //                 symReader.m_log.WriteLine("WARNING: could not find PDB signature of a 64 bit OS DLL.  Did you collect with a 32 bit version of XPERF?\r\n");
    //             }

    //             symReader.m_log.WriteLine("WARNING: The log file does not contain exact PDB signature information for {0} and the file at this path is not the file used in the trace.", moduleFile.FilePath);
    //             symReader.m_log.WriteLine("PDB files cannot be unambiguously matched to the EXE.");
    //             symReader.m_log.WriteLine("Did you merge the ETL file before transferring it off the collection machine?  If not, doing the merge will fix this.");
    //             if (!UnsafePDBMatching)
    //             {
    //                 symReader.m_log.WriteLine("The /UnsafePdbMatch option will force an ambiguous match, but this is not recommended.");
    //             }
    //         }

    //         symReader.m_log.WriteLine("Failed to find PDB for {0}", moduleFile.FilePath);
    //         return null;
    //     }

    //     // At this point pdbFileName is set,we are going to succeed.
    //     ManagedSymbolModule symbolReaderModule = symReader.OpenSymbolFile(pdbFileName);
    //     if (symbolReaderModule != null)
    //     {
    //         if (!UnsafePDBMatching && moduleFile.PdbSignature != Guid.Empty && symbolReaderModule.PdbGuid != moduleFile.PdbSignature)
    //         {
    //             symReader.m_log.WriteLine("ERROR: the PDB we opened does not match the PDB desired.  PDB GUID = " + symbolReaderModule.PdbGuid + " DESIRED GUID = " + moduleFile.PdbSignature);
    //             return null;
    //         }

    //         symbolReaderModule.ExePath = moduleFile.FilePath;

    //         // Currently NGEN pdbs do not have source server information, but the managed version does.
    //         // Thus we remember the lookup info for the managed PDB too so we have it if we need source server info
    //         var managed = moduleFile.ManagedModule;
    //         if (managed != null)
    //         {
    //             var nativePdb = symbolReaderModule as NativeSymbolModule;
    //             if (nativePdb != null)
    //             {
    //                 nativePdb.LogManagedInfo(managed.PdbName, managed.PdbSignature, managed.pdbAge);
    //             }
    //         }
    //     }

    //     symReader.m_log.WriteLine("Opened Pdb file {0}", pdbFileName);
    //     return symbolReaderModule;
    // }

    // /// <summary>
    // /// Returns true if 'moduleFile' seems to be unchanged from the time the information about it
    // /// was generated.  Logs messages to 'log' if it fails.
    // /// Specify overrideModuleFilePath if the path needs to be converted to a different format in order to be accessed (e.g. from device path to volume path).
    // /// </summary>
    // private bool TraceModuleUnchanged(TraceModuleFile moduleFile, TextWriter log, string overrideModuleFilePath = null)
    // {
    //     string moduleFilePath = SymbolReader.BypassSystem32FileRedirection(overrideModuleFilePath != null ? overrideModuleFilePath : moduleFile.FilePath);
    //     if (!File.Exists(moduleFilePath))
    //     {
    //         log.WriteLine("The file {0} does not exist on the local machine", moduleFilePath);
    //         return false;
    //     }

    //     using (var file = new PEFile.PEFile(moduleFilePath))
    //     {
    //         if (file.Header.CheckSum != (uint)moduleFile.ImageChecksum)
    //         {
    //             log.WriteLine("The local file {0} has a mismatched checksum found {1} != expected {2}", moduleFilePath, file.Header.CheckSum, moduleFile.ImageChecksum);
    //             return false;
    //         }
    //         if (moduleFile.ImageId != 0 && file.Header.TimeDateStampSec != moduleFile.ImageId)
    //         {
    //             log.WriteLine("The local file {0} has a mismatched Timestamp value found {1} != expected {2}", moduleFilePath, file.Header.TimeDateStampSec, moduleFile.ImageId);
    //             return false;
    //         }
    //     }
    //     return true;
    // }

    private const int CodeAddressInfoSerializationVersion = 1;

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }

    /// <summary>
    /// A CodeAddressInfo is the actual data stored in the ETLX file that represents a
    /// TraceCodeAddress.     It knows its Address in the process and it knows the
    /// TraceModuleFile (which knows its base address), so it also knows its relative
    /// address in the TraceModuleFile (which is what is needed to look up the value
    /// in the PDB.
    ///
    /// Note that by the time that the CodeAddressInfo is persisted in the ETLX file
    /// it no longer knows the process it originated from (thus separate processes
    /// with the same address and same DLL file loaded at the same address can share
    /// the same CodeAddressInfo.  This is actually reasonably common, since OS tend
    /// to load at their preferred base address.
    ///
    /// We also have to handle the managed case, in which case the CodeAddressInfo may
    /// also know about the TraceMethod or the ILMapIndex (which remembers both the
    /// method and the line numbers for managed code.
    ///
    /// However when the CodeAddressInfo is first created, we don't know the TraceModuleFile
    /// so we also need to remember the Process
    ///
    /// </summary>
    internal struct CodeAddressInfo
    {
        internal CodeAddressInfo(Address address, ProcessIndex processIndex)
        {
            Address = address;
            moduleFileIndex = Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid;
            methodOrProcessOrIlMapIndex = -2 - ((int)processIndex);      // Encode process index to make it unambiguous with a method index.
            InclusiveCount = 0;
            optimizationTier = Microsoft.Diagnostics.Tracing.Parsers.Clr.OptimizationTier.Unknown;
        }

        internal ILMapIndex GetILMapIndex(TraceCodeAddresses codeAddresses)
        {
            if (methodOrProcessOrIlMapIndex < 0 || (methodOrProcessOrIlMapIndex & 1) == 0)
            {
                return ILMapIndex.Invalid;
            }

            return (ILMapIndex)(methodOrProcessOrIlMapIndex >> 1);
        }
        internal void SetILMapIndex(TraceCodeAddresses codeAddresses, ILMapIndex value)
        {
            Debug.Assert(value != ILMapIndex.Invalid);

            // We may be overwriting other values, ensure that they actually don't change.
            Debug.Assert(GetMethodIndex(codeAddresses) == Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid ||
                GetMethodIndex(codeAddresses) == codeAddresses.ILToNativeMaps[(int)value].MethodIndex);
            Debug.Assert(methodOrProcessOrIlMapIndex >= 0 ||
                GetProcessIndex(codeAddresses) == codeAddresses.ILToNativeMaps[(int)value].ProcessIndex);

            methodOrProcessOrIlMapIndex = ((int)value << 1) + 1;

            Debug.Assert(GetILMapIndex(codeAddresses) == value);
        }
        /// <summary>
        /// This is only valid until MethodIndex or ModuleFileIndex is set.
        /// </summary>
        internal ProcessIndex GetProcessIndex(TraceCodeAddresses codeAddresses)
        {
            if (methodOrProcessOrIlMapIndex < -1)
            {
                return (Microsoft.Diagnostics.Tracing.Etlx.ProcessIndex)(-(methodOrProcessOrIlMapIndex + 2));
            }

            var ilMapIdx = GetILMapIndex(codeAddresses);
            if (ilMapIdx != ILMapIndex.Invalid)
            {
                return codeAddresses.ILToNativeMaps[(int)ilMapIdx].ProcessIndex;
            }
            // Can't assert because we get here if we have NGEN rundown on an NGEN image
            // Debug.Assert(false, "Asking for Process after Method has been set is illegal (to save space)");
            return Microsoft.Diagnostics.Tracing.Etlx.ProcessIndex.Invalid;
        }
        /// <summary>
        /// Only for managed code.
        /// </summary>
        internal MethodIndex GetMethodIndex(TraceCodeAddresses codeAddresses)
        {
            if (methodOrProcessOrIlMapIndex < 0)
            {
                return TryLookupMethodOrModule(codeAddresses);
            }

            if ((methodOrProcessOrIlMapIndex & 1) == 0)
            {
                return (Microsoft.Diagnostics.Tracing.Etlx.MethodIndex)(methodOrProcessOrIlMapIndex >> 1);
            }

            return codeAddresses.ILToNativeMaps[(int)GetILMapIndex(codeAddresses)].MethodIndex;
        }

        private MethodIndex TryLookupMethodOrModule(TraceCodeAddresses codeAddresses)
        {
            if (!(codeAddresses.log.IsRealTime && methodOrProcessOrIlMapIndex < -1))
            {
                return Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
            }

            int index;
            var process = codeAddresses.log.process;
            TraceLoadedModule? loadedModule = process.LoadedModules.FindModuleAndIndexContainingAddress(Address, long.MaxValue - 1, out index);
            if (loadedModule != null)
            {
                SetModuleFileIndex(loadedModule.ModuleFile);
                methodOrProcessOrIlMapIndex = -1;           //  set it as the invalid method, destroys memory of process we are in.
                return Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid;
            }
            else
            {
                MethodIndex methodIndex = process.FindJITTEDMethodFromAddress(Address);
                if (methodIndex != Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid)
                {
                    SetMethodIndex(codeAddresses, methodIndex);
                }
                else
                {
                    methodOrProcessOrIlMapIndex = -1;           //  set it as the invalid method, destroys memory of process we are in.
                }

                return methodIndex;
            }
        }
        internal void SetMethodIndex(TraceCodeAddresses codeAddresses, MethodIndex value)
        {
            Debug.Assert(value != Microsoft.Diagnostics.Tracing.Etlx.MethodIndex.Invalid);

            if (GetILMapIndex(codeAddresses) == TraceCodeAddresses.ILMapIndex.Invalid)
            {
                methodOrProcessOrIlMapIndex = (int)(value) << 1;
            }
            else
            {
                Debug.Assert(GetMethodIndex(codeAddresses) == value, "Setting method index when ILMap already set (ignored)");
            }

            Debug.Assert(GetMethodIndex(codeAddresses) == value);
        }
        /// <summary>
        /// Only for unmanaged code.   TODO, this can be folded into methodOrProcessIlMap index and save a DWORD.
        /// since if the method or IlMap is present then you can get the ModuelFile index from there.
        /// </summary>
        internal ModuleFileIndex GetModuleFileIndex(TraceCodeAddresses codeAddresses)
        {
            if (moduleFileIndex == Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid)
            {
                TryLookupMethodOrModule(codeAddresses);
            }

            return moduleFileIndex;
        }

        internal void SetModuleFileIndex(TraceModuleFile moduleFile)
        {
            if (moduleFileIndex != Microsoft.Diagnostics.Tracing.Etlx.ModuleFileIndex.Invalid)
            {
                return;
            }

            moduleFileIndex = moduleFile.ModuleFileIndex;

            if (optimizationTier == Microsoft.Diagnostics.Tracing.Parsers.Clr.OptimizationTier.Unknown &&
                moduleFile.IsReadyToRun &&
                moduleFile.ImageBase <= Address &&
                Address < moduleFile.ImageEnd)
            {
                optimizationTier = Microsoft.Diagnostics.Tracing.Parsers.Clr.OptimizationTier.ReadyToRun;
            }
        }

        internal void SetOptimizationTier(OptimizationTier value)
        {
            if (optimizationTier == Microsoft.Diagnostics.Tracing.Parsers.Clr.OptimizationTier.Unknown)
            {
                optimizationTier = value;
            }
        }

        // keep track of how popular each code stack is.
        internal void UpdateStats()
        {
            InclusiveCount++;
        }

        internal Address Address;
        /// <summary>
        /// This is a count of how many times this code address appears in any stack in the trace.
        /// It is a measure of what popular the code address is (whether we should look up its symbols).
        /// </summary>
        internal int InclusiveCount;

        // To save space, we reuse this slot during data collection
        // If x < -1 it is ProcessIndex, if > -1 and odd, it is an ILMapIndex if > -1 and even it is a MethodIndex.
        internal int methodOrProcessOrIlMapIndex;

        internal ModuleFileIndex moduleFileIndex;

        internal OptimizationTier optimizationTier;
    }

    private ILMapIndex UnloadILMapForMethod(MethodIndex methodIndex, MethodLoadUnloadVerboseTraceData data)
    {
        ILMapIndex ilMapIdx;
        var ilMap = FindAndRemove(data.MethodID, log.process.ProcessIndex, out ilMapIdx);
        if (ilMap == null)
        {
            return ilMapIdx;
        }

        Debug.Assert(ilMap.MethodStart == 0 || ilMap.MethodStart == data.MethodStartAddress);
        Debug.Assert(ilMap.MethodLength == 0 || ilMap.MethodLength == data.MethodSize);

        ilMap.MethodStart = data.MethodStartAddress;
        ilMap.MethodLength = data.MethodSize;
        Debug.Assert(ilMap.MethodIndex == 0 || ilMap.MethodIndex == methodIndex);
        ilMap.MethodIndex = methodIndex;
        return ilMapIdx;
    }

    /// <summary>
    /// Find the ILToNativeMap for 'methodId' in process associated with 'processIndex'
    /// and then remove it from the table (this is what you want to do when the method is unloaded)
    /// </summary>
    private ILToNativeMap? FindAndRemove(long methodID, ProcessIndex processIndex, out ILMapIndex mapIdxRet)
    {
        ILMapIndex mapIdx;
        if (methodIDToILToNativeMap != null && methodIDToILToNativeMap.TryGetValue(methodID, out mapIdx))
        {
            ILToNativeMap? prev = null;
            while (mapIdx != ILMapIndex.Invalid)
            {
                ILToNativeMap ret = ILToNativeMaps[(int)mapIdx];
                if (ret.ProcessIndex == processIndex)
                {
                    if (prev != null)
                    {
                        prev.Next = ret.Next;
                    }
                    else if (ret.Next == ILMapIndex.Invalid)
                    {
                        methodIDToILToNativeMap.Remove(methodID);
                    }
                    else
                    {
                        methodIDToILToNativeMap[methodID] = ret.Next;
                    }

                    mapIdxRet = mapIdx;
                    return ret;
                }
                mapIdx = ret.Next;
            }
        }
        mapIdxRet = ILMapIndex.Invalid;
        return null;
    }

    internal void AddILMapping(MethodILToNativeMapTraceData data)
    {
        var ilMap = new ILToNativeMap();
        ilMap.Next = ILMapIndex.Invalid;

        ilMap.ProcessIndex = log.process.ProcessIndex;
        ILToNativeMapTuple tuple;
        for (int i = 0; i < data.CountOfMapEntries; i++)
        {
            // There are special prologue and epilogue offsets, but the best line approximation
            // happens if we simply ignore them, so this is what we do here.
            var ilOffset = data.ILOffset(i);
            if (ilOffset < 0)
            {
                continue;
            }

            tuple.ILOffset = ilOffset;
            tuple.NativeOffset = data.NativeOffset(i);
            ilMap.Map.Add(tuple);
        }

        // They may not come back sorted, but we want to binary search so sort them by native offset (ascending)
        ilMap.Map.Sort((x, y) => x.NativeOffset - y.NativeOffset);

        ILMapIndex mapIdx = (ILMapIndex)ILToNativeMaps.Count;
        ILToNativeMaps.Add(ilMap);
        if (methodIDToILToNativeMap == null)
        {
            methodIDToILToNativeMap = new Dictionary<long, ILMapIndex>(101);
        }

        ILMapIndex prevIndex;
        if (methodIDToILToNativeMap.TryGetValue(data.MethodID, out prevIndex))
        {
            ilMap.Next = prevIndex;
        }

        methodIDToILToNativeMap[data.MethodID] = mapIdx;
    }

    internal enum ILMapIndex { Invalid = -1 };
    internal struct ILToNativeMapTuple
    {
        public int ILOffset;
        public int NativeOffset;

        internal void Deserialize(Deserializer deserializer)
        {
            deserializer.Read(out ILOffset);
            deserializer.Read(out NativeOffset);
        }
        internal void Serialize(Serializer serializer)
        {
            serializer.Write(ILOffset);
            serializer.Write(NativeOffset);
        }
    }

    internal class ILToNativeMap
    {
        public ILMapIndex Next;             // We keep a link list of maps with the same start address
                                            // (can only be from different processes);
        public ProcessIndex ProcessIndex;   // This is not serialized.
        public MethodIndex MethodIndex;
        public Address MethodStart;
        public int MethodLength;
        internal GrowableArray<ILToNativeMapTuple> Map;

        public int GetILOffsetForNativeAddress(Address nativeAddress)
        {
            int idx;
            if (nativeAddress < MethodStart || MethodStart + (uint)MethodLength < nativeAddress)
            {
                return -1;
            }

            int nativeOffset = (int)(nativeAddress - MethodStart);
            Map.BinarySearch(nativeOffset, out idx,
                delegate (int key, ILToNativeMapTuple elem)
                { return key - elem.NativeOffset; });
            if (idx < 0)
            {
                return -1;
            }

            // After looking at the empirical results, it does seem that linear interpolation
            // Gives a significantly better approximation of the IL address.
            int retIL = Map[idx].ILOffset;
            int nativeDelta = nativeOffset - Map[idx].NativeOffset;
            int nextIdx = idx + 1;
            if (nextIdx < Map.Count && nativeDelta != 0)
            {
                int ILDeltaToNext = Map[nextIdx].ILOffset - Map[idx].ILOffset;
                // If the IL deltas are going down don't interpolate.
                if (ILDeltaToNext > 0)
                {
                    int nativeDeltaToNext = Map[nextIdx].NativeOffset - Map[idx].NativeOffset;
                    retIL += (int)(((double)nativeDelta) / nativeDeltaToNext * ILDeltaToNext + .5);
                }
                else
                {
                    return retIL;
                }
            }
            // For our use in sampling the EIP is the instruction that COMPLETED, so we actually want to
            // attribute the time to the line BEFORE this one if we are exactly on the boundary.
            // TODO This probably does not belong here, but I only want to this if the IL deltas are going up.
            if (retIL > 0)
            {
                --retIL;
            }

            return retIL;
        }
    }

    private GrowableArray<ILToNativeMap> ILToNativeMaps;                    // only Jitted code has these, indexed by ILMapIndex
    private Dictionary<long, ILMapIndex>? methodIDToILToNativeMap;

    private TraceCodeAddress[][]? codeAddressObjects;  // If we were asked for TraceCodeAddresses (instead of indexes) we cache them, in sparse array
    private string[]? names;                         // A cache (one per code address) of the string name of the address
    private int managedMethodRecordCount;           // Remembers how many code addresses are managed methods (currently not serialized)
    internal int totalCodeAddresses;                 // Count of the number of times a code address appears in the log.

    // These are actually serialized.
    private TraceLog log;
    private TraceModuleFiles moduleFiles;
    private TraceMethods methods;
    // private DeferedRegion lazyCodeAddresses;
    internal GrowableArray<CodeAddressInfo> codeAddresses;

    #endregion
}
