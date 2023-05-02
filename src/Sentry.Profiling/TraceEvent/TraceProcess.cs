using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceProcess represents a process in the trace.
/// </summary>
internal sealed class TraceProcess
{
    /// <summary>
    /// The OS process ID associated with the process. It is NOT unique across the whole log.  Use
    /// ProcessIndex for that.
    /// </summary>
    public int ProcessID { get { return processID; } }
    /// <summary>
    /// The index into the logical array of TraceProcesses for this process. Unlike ProcessID (which
    /// may be reused after the process dies, the process index is unique in the log.
    /// </summary>
    public ProcessIndex ProcessIndex { get { return processIndex; } }
    // /// <summary>
    // /// This is a short name for the process.  It is the image file name without the path or suffix.
    // /// </summary>
    // public string Name
    // {
    //     get
    //     {
    //         if (name == null)
    //         {
    //             name = TraceLog.GetFileNameWithoutExtensionNoIllegalChars(ImageFileName);
    //             if (name.Length == 0 && ProcessID != -1 && processID != 0)  // These special cases are so I don't have to rebaseline the tests.
    //                 name = "Process(" + ProcessID + ")";
    //         }

    //         return name;
    //     }
    // }
    // /// <summary>
    // /// The command line that started the process (may be empty string if unknown)
    // /// </summary>
    // public string CommandLine { get { return commandLine; } }
    // /// <summary>
    // /// The path name of the EXE that started the process (may be empty string if unknown)
    // /// </summary>
    // public string ImageFileName { get { return imageFileName; } }
    // /// <summary>
    // /// The time when the process started.  Returns the time the trace started if the process existed when the trace started.
    // /// </summary>
    // public DateTime StartTime { get { return log.QPCTimeToDateTimeUTC(startTimeQPC).ToLocalTime(); } }
    // /// <summary>
    // /// The time when the process started.  Returns the time the trace started if the process existed when the trace started.
    // /// Returned as the number of MSec from the beginning of the trace.
    // /// </summary>
    // public double StartTimeRelativeMsec { get { return log.QPCTimeToRelMSec(startTimeQPC); } }
    // /// <summary>
    // /// The time when the process ended.  Returns the time the trace ended if the process existed when the trace ended.
    // /// Returned as a DateTime
    // /// </summary>
    // public DateTime EndTime { get { return log.QPCTimeToDateTimeUTC(endTimeQPC).ToLocalTime(); } }
    // /// <summary>
    // /// The time when the process ended.  Returns the time the trace ended if the process existed when the trace ended.
    // /// Returned as the number of MSec from the beginning of the trace.
    // /// </summary>
    // public double EndTimeRelativeMsec { get { return log.QPCTimeToRelMSec(endTimeQPC); } }
    // /// <summary>
    // /// The process ID of the parent process
    // /// </summary>
    // public int ParentID { get { return parentID; } }
    // /// <summary>
    // /// The process that started this process.  Returns null if unknown    Unlike ParentID
    // /// the chain of Parent's will never form a loop.
    // /// </summary>
    // public TraceProcess Parent { get { return parent; } }
    // /// <summary>
    // /// If the process exited, the exit status of the process.  Otherwise null.
    // /// </summary>
    // public int? ExitStatus { get { return exitStatus; } }
    // /// <summary>
    // /// The amount of CPU time spent in this process based on the kernel CPU sampling events.
    // /// </summary>
    // public float CPUMSec { get { return (float)(cpuSamples * Log.SampleProfileInterval.TotalMilliseconds); } }
    // /// <summary>
    // /// Returns true if the process is a 64 bit process
    // /// </summary>
    // public bool Is64Bit
    // {
    //     get
    //     {
    //         // We are 64 bit if any module was loaded high or
    //         // (if we are on a 64 bit and there were no modules loaded, we assume we are the OS system process)
    //         return loadedAModuleHigh || (!anyModuleLoaded && log.PointerSize == 8);
    //     }
    // }

    /// <summary>
    /// The log file associated with the process.
    /// </summary>
    public TraceLog Log { get { return log; } }

    // /// <summary>
    // /// A list of all the threads that occurred in this process.
    // /// </summary>
    // public IEnumerable<TraceThread> Threads
    // {
    //     get
    //     {
    //         for (int i = 0; i < log.Threads.Count; i++)
    //         {
    //             TraceThread thread = log.Threads[(ThreadIndex)i];
    //             if (thread.Process == this)
    //             {
    //                 yield return thread;
    //             }
    //         }
    //     }
    // }

    /// <summary>
    /// Returns the list of modules that were loaded by the process.  The modules may be managed or
    /// native, and include native modules that were loaded event before the trace started.
    /// </summary>
    public TraceLoadedModules LoadedModules { get { return loadedModules; } }

    //         /// <summary>
    //         /// Filters events to only those for a particular process.
    //         /// </summary>
    //         public TraceEvents EventsInProcess
    //         {
    //             get
    //             {
    //                 return log.Events.Filter(startTimeQPC, endTimeQPC, delegate (TraceEvent anEvent)
    //                 {
    //                     // FIX Virtual allocs
    //                     if (anEvent.ProcessID == processID)
    //                     {
    //                         return true;
    //                     }
    //                     // FIX Virtual alloc's Process ID?
    //                     if (anEvent.ProcessID == -1)
    //                     {
    //                         return true;
    //                     }

    //                     return false;
    //                 });
    //             }
    //         }
    //         /// <summary>
    //         /// Filters events to only that occurred during the time the process was alive.
    //         /// </summary>
    //         ///
    //         public TraceEvents EventsDuringProcess
    //         {
    //             get
    //             {
    //                 return log.Events.Filter(startTimeQPC, endTimeQPC, null);
    //             }
    //         }

    //         #region Private
    //         #region EventHandlersCalledFromTraceLog
    //         // #ProcessHandlersCalledFromTraceLog
    //         //
    //         // called from TraceLog.CopyRawEvents
    //         internal void ProcessStart(ProcessTraceData data)
    //         {
    //             Log.DebugWarn(parentID == 0, "Events for process happen before process start.  PrevEventTime: " + StartTimeRelativeMsec.ToString("f4"), data);

    //             if (data.Opcode == TraceEventOpcode.DataCollectionStart)
    //             {
    //                 startTimeQPC = log.sessionStartTimeQPC;
    //             }
    //             else
    //             {
    //                 Debug.Assert(data.Opcode == TraceEventOpcode.Start);
    //                 Debug.Assert(endTimeQPC == long.MaxValue); // We would create a new Process record otherwise
    //                 startTimeQPC = data.TimeStampQPC;
    //             }
    //             commandLine = data.CommandLine;
    //             imageFileName = data.ImageFileName;
    //             parentID = data.ParentID;
    //         }
    //         internal void ProcessEnd(ProcessTraceData data)
    //         {
    //             if (commandLine.Length == 0)
    //             {
    //                 commandLine = data.CommandLine;
    //             }

    //             imageFileName = data.ImageFileName;        // Always overwrite as we might have guessed via the image loads
    //             if (parentID == 0 && data.ParentID != 0)
    //             {
    //                 parentID = data.ParentID;
    //             }

    //             if (data.Opcode != TraceEventOpcode.DataCollectionStop)
    //             {
    //                 Debug.Assert(data.Opcode == TraceEventOpcode.Stop);
    //                 // Only set the exit code if it really is a process exit (not a DCStop).
    //                 if (data.Opcode == TraceEventOpcode.Stop)
    //                 {
    //                     exitStatus = data.ExitStatus;
    //                 }

    //                 endTimeQPC = data.TimeStampQPC;
    //             }
    //             Log.DebugWarn(startTimeQPC <= endTimeQPC, "Process Ends before it starts! StartTime: " + StartTimeRelativeMsec.ToString("f4"), data);
    //         }

    //         /// <summary>
    //         /// Sets the 'Parent' field for the process (based on the ParentID).
    //         ///
    //         /// sentinel is internal to the implementation, external callers should always pass null.
    //         /// TraceProcesses that have a parent==sentinel considered 'illegal' since it would form
    //         /// a loop in the parent chain, which we definitely don't want.
    //         /// </summary>
    //         internal void SetParentForProcess(TraceProcess sentinel = null)
    //         {
    //             if (parent != null)                     // already initialized, nothing to do.
    //             {
    //                 return;
    //             }

    //             if (parentID == -1)
    //             {
    //                 return;
    //             }

    //             if (parentID == 0)                      // Zero is the idle process and we prefer that it not have children.
    //             {
    //                 parentID = -1;
    //                 return;
    //             }

    //             // Look up the process ID, if we fail, we are done.
    //             int index;
    //             var potentialParent = Log.Processes.FindProcessAndIndex(parentID, startTimeQPC, out index);
    //             if (potentialParent == null)
    //             {
    //                 return;
    //             }

    //             // If this is called from the outside, intialize the sentinel.  We will pass it
    //             // along in our recurisve calls.  It is just an illegal value that we can use
    //             // to indicate that a node is currnetly a valid parent (becase it would form a loop)
    //             if (sentinel == null)
    //             {
    //                 sentinel = new TraceProcess(-1, Log, ProcessIndex.Invalid);
    //             }

    //             // During our recursive calls mark our parent with the sentinel this avoids loops.
    //             parent = sentinel;                      // Mark this node as off limits.

    //             // If the result is marked (would form a loop), give up setting the parent variable.
    //             if (potentialParent.parent == sentinel)
    //             {
    //                 parent = null;
    //                 parentID = -1;                              // This process ID is wrong, poison it to avoid using it again.
    //                 return;
    //             }

    //             potentialParent.SetParentForProcess(sentinel);   // Finish the intialization of the parent Process, also giving up if it hits a sentinel
    //             parent = potentialParent;                        // OK parent is fully intialized, I can reset the sentenel
    //         }

    // #if DEBUG
    //         internal int ParentDepth()
    //         {
    //             int depth = 0;
    //             TraceProcess cur = this;
    //             while (depth < Log.Processes.Count)
    //             {
    //                 if (cur.parent == null)
    //                 {
    //                     break;
    //                 }

    //                 depth++;
    //                 cur = cur.parent;
    //             }
    //             return depth;
    //         }
    // #endif
    //         #endregion

    //         internal bool IsKernelAddress(Address ip, int pointerSize)
    //         {
    //             // EventPipe doesn't generate kernel address events and current heauristics are not deterministic on none Windows platforms.
    //             if (log?.rawEventSourceToConvert is EventPipeEventSource)
    //                 return false;

    //             return TraceLog.IsWindowsKernelAddress(ip, pointerSize);
    //         }

    /// <summary>
    /// Create a new TraceProcess.  It should only be done by log.CreateTraceProcess because
    /// only TraceLog is responsible for generating a new ProcessIndex which we need.   'processIndex'
    /// is a index that is unique for the whole log file (where as processID can be reused).
    /// </summary>
    internal TraceProcess(int processID, TraceLog log, ProcessIndex processIndex)
    {
        this.log = log;
        this.processID = processID;
        this.processIndex = processIndex;
        // endTimeQPC = long.MaxValue;
        // commandLine = "";
        // imageFileName = "";
        loadedModules = new TraceLoadedModules(this);
        // TODO FIX NOW ACTIVITIES: if this is only used during translation, we should not allocate it in the ctor
        // scheduledActivityIdToActivityIndex = new Dictionary<Address, ActivityIndex>();
    }

    private int processID;
    internal ProcessIndex processIndex;
    private TraceLog log;

    // private string commandLine;
    // internal string imageFileName;
    // private string name;
    // internal long firstEventSeenQPC;      // Sadly there are events before process start.   This is minimum of those times.  Note that there may be events before this
    // internal long startTimeQPC;
    // internal long endTimeQPC;
    // private int? exitStatus;
    // private int parentID;
    // private TraceProcess parent;

    // internal int cpuSamples;
    // internal bool loadedAModuleHigh;    // Was any module loaded above 0x100000000?  (which indicates it is a 64 bit process)
    // internal bool anyModuleLoaded;
    // internal bool anyThreads;

    // internal bool isServerGC;
    // // We only set this in the GCStart event because we want to make sure we are seeing a complete GC.
    // // After we have seen a complete GC we set this to FALSE.
    // internal bool shouldCheckIsServerGC = false;
    // internal Dictionary<int, int> markThreadsInGC = new Dictionary<int, int>(); // Used during collection to determine if we are server GC or not.

    private TraceLoadedModules loadedModules;

    /* These are temporary and only used during conversion from ETL to resolve addresses to a CodeAddress.  */
    /// <summary>
    /// This table allows us to intern codeAddress so we only at most one distinct address per process.
    /// </summary>
    internal Dictionary<Address, CodeAddressIndex>? codeAddressesInProcess;
    /// <summary>
    /// We also keep track of those code addresses that are NOT yet resolved to at least a File (for JIT compiled
    /// things this would be to a method
    /// </summary>
    internal GrowableArray<CodeAddressIndex> unresolvedCodeAddresses;
    internal bool unresolvedCodeAddressesIsSorted;      // True if we know that unresolvedCodeAddresses is sorted
    internal bool seenVersion2GCStartEvents;

    /// <summary>
    /// This is all the information needed to remember about at JIT compiled method (used in the jitMethods variable)
    /// </summary>
    internal class MethodLookupInfo
    {
        public MethodLookupInfo(Address startAddress, int length, MethodIndex method)
        {
            StartAddress = startAddress;
            Length = length;
            MethodIndex = method;
        }
        public Address StartAddress;
        public int Length;
        public MethodIndex MethodIndex;             // Logically represents the TraceMethod.
        public ModuleFileIndex ModuleIndex;
    }

    /// <summary>
    /// This table has a entry for each JIT compiled method that remembers its range.   It is actually only needed
    /// for the real time case, as the non-real time case you resolve code addresses on method unload/rundown and thus
    /// don't need to remember the information.   This table is NOT persisted in the ETLX file since is only needed
    /// to convert raw addresses into TraceMethods.
    ///
    /// It is a array of arrays to make insertion efficient.  Most of the time JIT methods will be added in
    /// contiguous memory (thus will be in order), however from time to time things will 'jump around' to a new
    /// segment.   By having a list of lists, (which are in order in both lists) you can efficiently (log(N)) search
    /// as well as insert.
    /// </summary>
    internal System.Collections.Generic.GrowableArray<System.Collections.Generic.GrowableArray<MethodLookupInfo>> jitMethods;

    internal MethodIndex FindJITTEDMethodFromAddress(Address codeAddress)
    {
        int index;
        jitMethods.BinarySearch(codeAddress, out index, (addr, elemList) => addr.CompareTo(elemList[0].StartAddress));
        if (index < 0)
        {
            return MethodIndex.Invalid;
        }

        System.Collections.Generic.GrowableArray<MethodLookupInfo> subList = jitMethods[index];
        subList.BinarySearch(codeAddress, out index, (addr, elem) => addr.CompareTo(elem.StartAddress));
        if (index < 0)
        {
            return MethodIndex.Invalid;
        }

        MethodLookupInfo methodLookupInfo = subList[index];
        Debug.Assert(methodLookupInfo.StartAddress <= codeAddress);
        if (methodLookupInfo.StartAddress + (uint)methodLookupInfo.Length <= codeAddress)
        {
            return MethodIndex.Invalid;
        }

        return methodLookupInfo.MethodIndex;
    }

    internal void InsertJITTEDMethod(Address startAddress, int length, Func<MethodLookupInfo> onInsert)
    {
        // Debug.WriteLine(string.Format("Process {0} Adding 0x{1:x} Len 0x{2:x}", ProcessID, startAddress, length));
        int index;
        if (jitMethods.BinarySearch(startAddress, out index, (addr, elemList) => addr.CompareTo(elemList[0].StartAddress)))
        {
            return;     // Start address already exists, do nothing.
        }

        if (index < 0)
        {
            // either empty or we are going BEFORE the first element in the list, add a new entry there and we are done.
            index = 0;
            var newSubList = new System.Collections.Generic.GrowableArray<MethodLookupInfo>();
            newSubList.Add(onInsert());
            jitMethods.Insert(0, newSubList);
        }
        else
        {

            System.Collections.Generic.GrowableArray<MethodLookupInfo> subList = jitMethods[index];
            Debug.Assert(0 < subList.Count);
            int subIndex = subList.Count - 1;        // Guess that it goes after the last element
            if (startAddress < subList[subIndex].StartAddress)
            {
                // bad case, we are not adding in the end, move those elements to a new region so we can add at the end.
                if (subList.BinarySearch(startAddress, out subIndex, (addr, elem) => addr.CompareTo(elem.StartAddress)))
                {
                    return;     // Start address already exists, do nothing.
                }

                subIndex++;
                var toMove = subList.Count - subIndex;
                Debug.Assert(0 < toMove);
                if (toMove <= 8)
                {
                    jitMethods.UnderlyingArray[index].Insert(subIndex, onInsert());
                    goto RETURN;
                }

                // Move all the elements larger than subIndex to a new list right after this element.
                var newSubList = new System.Collections.Generic.GrowableArray<MethodLookupInfo>();
                for (int i = subIndex; i < subList.Count; i++)
                {
                    newSubList.Add(subList[i]);
                }

                jitMethods.UnderlyingArray[index].Count = subIndex;

                // Add the new list to the first-level list-of-lists.
                jitMethods.Insert(index + 1, newSubList);
            }
            // Add the new entry
            jitMethods.UnderlyingArray[index].Add(onInsert());
        }

RETURN:
        ;
    }

    // /// <summary>
    // /// Maps a newly scheduled "user" activity ID to the ActivityIndex of the
    // /// Activity. This keeps track of currently created/scheduled activities
    // /// that have not started yet, and for multi-trigger events, created/scheduled
    // /// activities that have not conclusively "died" (e.g. by having their "user"
    // /// activity ID reused by another activity).
    // /// </summary>
    // internal Dictionary<Address, ActivityIndex> scheduledActivityIdToActivityIndex;
}
