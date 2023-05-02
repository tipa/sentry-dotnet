
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Sentry.Profiling.DiagnosticsTracing;

internal class TraceLog
{
    private readonly EventPipeEventSource _eventSource;

    public TraceLog(EventPipeEventSource eventSource)
    {
        _eventSource = eventSource;
        process = new TraceProcess(0, this, 0);
        threads = new TraceThreads(this);
        moduleFiles = new TraceModuleFiles(this);
        codeAddresses = new TraceCodeAddresses(this, moduleFiles);
        callStacks = new TraceCallStacks(this, codeAddresses);
        var sampleEventParser = new SampleProfilerTraceEventParser(_eventSource);
    }

    public void Process(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(_eventSource.StopProcessing);
        SetupCallbacks(_eventSource);
        _eventSource.Process();
        registration.Unregister();
    }

    internal readonly TraceProcess process;

    internal int eventCount;                             // Total number of events
    internal bool processingDisabled;                    // Have we turned off processing because of a MaxCount?
    internal bool removeFromStream;                      // Don't put these in the serialized stream.
    internal bool bookKeepingEvent;                      // BookKeeping events are removed from the stream by default
    internal bool bookeepingEventThatMayHaveStack;       // Some bookkeeping events (ThreadDCEnd) might have stacks
    internal bool noStack;                               // This event should never have a stack associated with it, so skip them if we every try to attach a stack.

    internal long sessionStartTimeQPC;

    private TraceThreads threads;
    private TraceCallStacks callStacks;
    private TraceCodeAddresses codeAddresses;

    /// <summary>
    /// All the Threads that logged an event in the ETLX file.  The returned TraceThreads instance supports IEnumerable so it can be used
    /// in foreach statements, but it also supports other methods to select particular thread.
    /// </summary>
    public TraceThreads Threads { get { return threads; } }
    /// <summary>
    /// All the module files (DLLs) that were loaded by some process in the ETLX file.  The returned TraceModuleFiles instance supports IEnumerable so it can be used
    /// in foreach statements, but it also supports other methods to select particular module file.
    /// </summary>
    public TraceModuleFiles ModuleFiles { get { return moduleFiles; } }
    /// <summary>
    /// All the call stacks in the ETLX file.  Normally you don't enumerate over these, but use you use other methods on TraceCallStacks
    /// information about code addresses using CallStackIndexes.
    /// </summary>
    public TraceCallStacks CallStacks { get { return callStacks; } }
    /// <summary>
    /// All the code addresses in the ETLX file.  Normally you don't enumerate over these, but use you use other methods on TraceCodeAddresses
    /// information about code addresses using CodeAddressIndexes.
    /// </summary>
    public TraceCodeAddresses CodeAddresses { get { return codeAddresses; } }

    // TODO FIX NOW remove the jittedMethods ones.
    private List<MethodLoadUnloadVerboseTraceData> jittedMethods = new();

    private TraceModuleFiles moduleFiles;
    private Internal.GrowableArray<EventsToStackIndex> eventsToStacks;

    internal Internal.GrowableArray<Guid> relatedActivityIDs;

    // In a TraceLog, we store all of the container IDs here and then 'point'
    // at them with the index into this array.  This is just like relatedActivityIDs above.
    // See TraceLog.GetContainerID.
    internal Internal.GrowableArray<string> containerIDs;

    #region EventsToStackIndex
    internal struct EventsToStackIndex
    {
        internal EventsToStackIndex(EventIndex eventIndex, CallStackIndex stackIndex)
        {
            Debug.Assert(eventIndex != EventIndex.Invalid);
            // We should never be returning the IDs we use to encode the thread itself.
            Debug.Assert(stackIndex == CallStackIndex.Invalid || 0 <= stackIndex);
            EventIndex = eventIndex;
            CallStackIndex = stackIndex;
        }
        internal EventIndex EventIndex;
        internal CallStackIndex CallStackIndex;
    }

    internal bool IsRealTime = false;

    /// <summary>
    /// Add a new entry that associates the stack 'stackIndex' with the event with index 'eventIndex'
    /// </summary>
    internal void AddStackToEvent(EventIndex eventIndex, CallStackIndex stackIndex)
    {
        int whereToInsertIndex = eventsToStacks.Count;
        if (IsRealTime)
        {
            // We need the array to be sorted, we do insertion sort, which works great because you are almost always
            // the last element (or very near the end).
            // for non-real-time we do the sorting in bulk at the end of the trace.
            while (0 < whereToInsertIndex)
            {
                --whereToInsertIndex;
                var prevIndex = eventsToStacks[whereToInsertIndex].EventIndex;
                if (prevIndex <= eventIndex)
                {
                    if (prevIndex == eventIndex)
                    {
                        // DebugWarn(true, "Warning, two stacks given to the same event with ID " + eventIndex + " discarding the second one", null);
                        return;
                    }
                    whereToInsertIndex++;   // insert after this index is bigger than the element compared.
                    break;
                }
            }
        }
        // For non-realtime session we simply insert it at the end because we will sort by eventIndex as a
        // post-processing step.  see eventsToStacks.Sort in CopyRawEvents().
#if DEBUG
        for (int i = 1; i < 8; i++)
        {
            int idx = eventsToStacks.Count - i;
            if (idx < 0)
            {
                break;
            }
            // If this assert fires, it means that we added a stack to the same event twice.   This
            // means we screwed up which event a stack belongs to.   This can happen among other reasons
            // because we complete an incomplete stack before we should and when the other stack component
            // comes in we end up logging it as if it were a unrelated stack giving two stacks to the same event.
            // Note many of these issues are reasonably benign, (e.g. we lose the kernel part of a stack)
            // so don't sweat this too much.    Because the source that we do later is not stable, which
            // of the two equal entries gets chosen will be random.
            Debug.Assert(eventsToStacks[idx].EventIndex != eventIndex);
        }
#endif
        eventsToStacks.Insert(whereToInsertIndex, new EventsToStackIndex(eventIndex, stackIndex));
    }

    // private static readonly Func<EventIndex, EventsToStackIndex, int> stackComparer = delegate (EventIndex eventID, EventsToStackIndex elem)
    //     { return TraceEvent.Compare(eventID, elem.EventIndex); };

    #endregion

    /// <summary>
    /// SetupCallbacks installs all the needed callbacks for TraceLog Processing (stacks, process, thread, summaries etc)
    /// on the TraceEventSource rawEvents.
    /// </summary>
    private void SetupCallbacks(TraceEventDispatcher rawEvents)
    {
        sessionStartTimeQPC = 0; // TODO not accessible at the moment rawEvents.sessionStartTimeQPC;
        processingDisabled = false;
        removeFromStream = false;
        bookKeepingEvent = false;                  // BookKeeping events are removed from the stream by default
        bookeepingEventThatMayHaveStack = false;   // Some bookkeeping events (ThreadDCEnd) might have stacks
        noStack = false;                           // This event should never have a stack associated with it, so skip them if we every try to attach a stack.
        //x pastEventInfo = new PastEventInfo(this);
        eventCount = 0;

        // FIX NOW HACK, because Method and Module unload methods are missing.
        jittedMethods = new List<MethodLoadUnloadVerboseTraceData>();

        // If a event does not have a callback, then it will be treated as unknown.  Unfortunately this also means that the
        // virtual method 'LogCodeAddresses() will not fire.  Thus any event that has this overload needs to have a callback.
        // The events below don't otherwise need a callback, but we add one so that LogCodeAddress() works.
        Action<TraceEvent> doNothing = delegate (TraceEvent data)
        { };

        // We want high volume events to be looked up properly since GetEventCount() is slower thant we want.
        rawEvents.Clr.GCAllocationTick += doNothing;
        rawEvents.Clr.GCJoin += doNothing;
        rawEvents.Clr.GCFinalizeObject += doNothing;
        rawEvents.Clr.MethodJittingStarted += doNothing;

        rawEvents.Clr.LoaderModuleLoad += delegate (ModuleLoadUnloadTraceData data)
        {
            process.LoadedModules.ManagedModuleLoadOrUnload(data, true, false);
        };
        rawEvents.Clr.LoaderModuleUnload += delegate (ModuleLoadUnloadTraceData data)
        {
            process.LoadedModules.ManagedModuleLoadOrUnload(data, false, false);
        };
        rawEvents.Clr.LoaderModuleDCStopV2 += delegate (ModuleLoadUnloadTraceData data)
        {
            process.LoadedModules.ManagedModuleLoadOrUnload(data, false, true);
        };

        var ClrRundownParser = new ClrRundownTraceEventParser(rawEvents);
        Action<ModuleLoadUnloadTraceData> onLoaderRundown = delegate (ModuleLoadUnloadTraceData data)
        {
            process.LoadedModules.ManagedModuleLoadOrUnload(data, false, true);
        };

        ClrRundownParser.LoaderModuleDCStop += onLoaderRundown;
        ClrRundownParser.LoaderModuleDCStart += onLoaderRundown;

        Action<MethodLoadUnloadVerboseTraceData> onMethodStart = delegate (MethodLoadUnloadVerboseTraceData data)
            {
                // We only capture data on unload, because we collect the addresses first.
                if (!data.IsDynamic && !data.IsJitted)
                {
                    bookKeepingEvent = true;
                }

                if ((int)data.ID == 139)       // MethodDCStartVerboseV2
                {
                    bookKeepingEvent = true;
                }

                if (data.IsJitted)
                {
                    process.InsertJITTEDMethod(data.MethodStartAddress, data.MethodSize, delegate ()
                    {
#pragma warning disable 612, 618 // disable error for use of obsolete data.TimeStampQPC
                        TraceManagedModule module = process.LoadedModules.GetOrCreateManagedModule(data.ModuleID, data.TimeStampQPC);
#pragma warning restore 612,618
                        MethodIndex methodIndex = CodeAddresses.Methods.NewMethod(TraceLog.GetFullName(data), module.ModuleFile.ModuleFileIndex, data.MethodToken);
                        return new TraceProcess.MethodLookupInfo(data.MethodStartAddress, data.MethodSize, methodIndex);
                    });

                    jittedMethods.Add((MethodLoadUnloadVerboseTraceData)data.Clone());
                }
            };
        rawEvents.Clr.MethodLoadVerbose += onMethodStart;
        rawEvents.Clr.MethodDCStartVerboseV2 += onMethodStart;
        ClrRundownParser.MethodDCStartVerbose += onMethodStart;

        rawEvents.Clr.MethodUnloadVerbose += delegate (MethodLoadUnloadVerboseTraceData data)
        {
            codeAddresses.AddMethod(data);
            if (!data.IsJitted)
            {
                bookKeepingEvent = true;
            }
        };
        rawEvents.Clr.MethodILToNativeMap += delegate (MethodILToNativeMapTraceData data)
        {
            codeAddresses.AddILMapping(data);
            bookKeepingEvent = true;
        };

        ClrRundownParser.MethodILToNativeMapDCStop += delegate (MethodILToNativeMapTraceData data)
        {
            codeAddresses.AddILMapping(data);
            bookKeepingEvent = true;
        };


        Action<MethodLoadUnloadVerboseTraceData> onMethodDCStop = delegate (MethodLoadUnloadVerboseTraceData data)
        {
#if false // TODO this is a hack for VS traces that only did DCStarts but no DCStops.
                if (data.IsJitted && data.TimeStampRelativeMSec < 4000)
                {
                    jittedMethods.Add((MethodLoadUnloadVerboseTraceData)data.Clone());
                }
#endif

            codeAddresses.AddMethod(data);
            bookKeepingEvent = true;
        };

        rawEvents.Clr.MethodDCStopVerboseV2 += onMethodDCStop;
        ClrRundownParser.MethodDCStopVerbose += onMethodDCStop;

        // TODO
        // Action<ClrStackWalkTraceData> clrStackWalk = delegate (ClrStackWalkTraceData data)
        // {
        //     bookKeepingEvent = true;

        //     // Avoid creating data structures for events we will throw away
        //     if (processingDisabled)
        //     {
        //         return;
        //     }

        //     int i = 0;
        //     // Look for the previous CLR event on this same thread.
        //     for (PastEventInfoIndex prevEventIndex = pastEventInfo.CurrentIndex; ;)
        //     {
        //         i++;
        //         Debug.Assert(i < 20000);

        //         prevEventIndex = pastEventInfo.GetPreviousEventIndex(prevEventIndex, data.ThreadID, true);
        //         if (prevEventIndex == PastEventInfoIndex.Invalid)
        //         {
        //             // DebugWarn(false, "Could not find a previous event for a CLR stack trace.", data);
        //             return;
        //         }
        //         if (pastEventInfo.IsClrEvent(prevEventIndex))
        //         {
        //             if (pastEventInfo.HasStack(prevEventIndex))
        //             {
        //                 // DebugWarn(false, "CLR Stack trying to be given to same event twice (can happen with lost events)", data);
        //                 return;
        //             }
        //             pastEventInfo.SetHasStack(prevEventIndex);

        //             thread = Threads.GetOrCreateThread(data.ThreadID, data.TimeStampQPC, process);

        //             CallStackIndex callStackIndex = callStacks.GetStackIndexForStackEvent(
        //                 data.InstructionPointers, data.FrameCount, data.PointerSize, thread);
        //             Debug.Assert(callStacks.Depth(callStackIndex) == data.FrameCount);
        //             // DebugWarn(pastEventInfo.GetThreadID(prevEventIndex) == data.ThreadID, "Mismatched thread for CLR Stack Trace", data);

        //             // Get the previous event on the same thread.
        //             EventIndex eventIndex = pastEventInfo.GetEventIndex(prevEventIndex);
        //             Debug.Assert(eventIndex != EventIndex.Invalid); // We don't delete CLR events and that is the only way eventIndexes can be invalid
        //             AddStackToEvent(eventIndex, callStackIndex);
        //             pastEventInfo.GetEventCounts(prevEventIndex).m_stackCount++;
        //             return;
        //         }
        //     }
        // };
        // rawEvents.Clr.ClrStackWalk += clrStackWalk;

        // TODO SampleProfilerTraceEventParser.ThreadStackWalk is marked obsolete and that it can be removed.
        // // Process stack trace from EventPipe trace
        // Action<ClrThreadStackWalkTraceData> clrThreadStackWalk = delegate (ClrThreadStackWalkTraceData data)
        // {
        //     bookKeepingEvent = true;

        //     // Avoid creating data structures for events we will throw away
        //     if (processingDisabled)
        //     {
        //         return;
        //     }

        //     PastEventInfoIndex prevEventIndex = pastEventInfo.GetPreviousEventIndex(pastEventInfo.CurrentIndex, data.ThreadID, true);

        //     if (prevEventIndex == PastEventInfoIndex.Invalid)
        //     {
        //         DebugWarn(false, "Could not find a previous event for a CLR thread stack trace.", data);
        //         return;
        //     }

        //     thread = Threads.GetOrCreateThread(data.ThreadID, data.TimeStampQPC, process);

        //     CallStackIndex callStackIndex = callStacks.GetStackIndexForStackEvent(
        //         data.InstructionPointers, data.FrameCount, data.PointerSize, thread);
        //     Debug.Assert(callStacks.Depth(callStackIndex) == data.FrameCount);

        //     // Get the previous event and add stack
        //     EventIndex eventIndex = pastEventInfo.GetEventIndex(prevEventIndex);
        //     AddStackToEvent(eventIndex, callStackIndex);
        //     pastEventInfo.GetEventCounts(prevEventIndex).m_stackCount++;

        //     return;
        // };
        // var eventPipeParser = new SampleProfilerTraceEventParser(rawEvents);
        // eventPipeParser.ThreadStackWalk += clrThreadStackWalk;

        // var clrPrivate = new ClrPrivateTraceEventParser(rawEvents);
        // clrPrivate.ClrStackWalk += clrStackWalk;

        // // The following 3 callbacks for a small state machine to determine whether the process
        // // is running server GC and what the server GC threads are.
        // // We assume we are server GC if there are more than one thread doing the 'MarkHandles' event
        // // during a GC, and the threads that do that are the server threads.  We use this to mark the
        // // threads as Server GC Threads.
        // rawEvents.Clr.GCStart += delegate (GCStartTraceData data)
        // {
        //     if ((process.markThreadsInGC.Count == 0) && (process.shouldCheckIsServerGC == false))
        //     {
        //         process.shouldCheckIsServerGC = true;
        //     }
        // };
        // rawEvents.Clr.GCStop += delegate (GCEndTraceData data)
        // {
        //     if (process.markThreadsInGC.Count > 0)
        //     {
        //         process.shouldCheckIsServerGC = false;
        //     }

        //     if (!process.isServerGC && (process.markThreadsInGC.Count > 1))
        //     {
        //         process.isServerGC = true;
        //         foreach (var curThread in process.Threads)
        //         {
        //             if (thread.threadInfo == null && process.markThreadsInGC.ContainsKey(curThread.ThreadID))
        //             {
        //                 curThread.threadInfo = ".NET Server GC Thread(" + process.markThreadsInGC[curThread.ThreadID] + ")";
        //             }
        //         }
        //     }
        // };
        // rawEvents.Clr.GCMarkWithType += delegate (GCMarkWithTypeTraceData data)
        // {
        //     if (data.Type == (int)MarkRootType.MarkHandles)
        //     {
        //         AddMarkThread(data.ThreadID, data.TimeStampQPC, data.HeapNum);
        //     }
        // };
        // clrPrivate.GCMarkHandles += delegate (GCMarkTraceData data)
        // {
        //     AddMarkThread(data.ThreadID, data.TimeStampQPC, data.HeapNum);
        // };

        // TODO these just set thread names based on heuristics
        // var aspNetParser = new AspNetTraceEventParser(rawEvents);
        // aspNetParser.AspNetReqStart += delegate (AspNetStartTraceData data)
        // { CategorizeThread(data, "Incoming Request Thread"); };
        // rawEvents.Clr.GCFinalizersStart += delegate (GCNoUserDataTraceData data)
        // { CategorizeThread(data, ".NET Finalizer Thread"); };
        // rawEvents.Clr.GCFinalizersStop += delegate (GCFinalizersEndTraceData data)
        // { CategorizeThread(data, ".NET Finalizer Thread"); };
        // Action<TraceEvent> MarkAsBGCThread = delegate (TraceEvent data)
        // {
        //     thread = Threads.GetOrCreateThread(data.ThreadID, data.TimeStampQPC, process);
        //     bool isServerGC = (thread != null && thread.process.isServerGC);
        //     CategorizeThread(data, ".NET Background GC Thread");
        // };

        // // We use more than then GCBGStart to mark a GC thread because we need an event that happens more routinely
        // // since this might be a circular buffer or other short trace.
        // clrPrivate.GCBGCStart += delegate (GCNoUserDataTraceData data)
        // { MarkAsBGCThread(data); };
        // clrPrivate.GCBGC1stConStop += delegate (GCNoUserDataTraceData data)
        // { MarkAsBGCThread(data); };
        // clrPrivate.GCBGCDrainMark += delegate (BGCDrainMarkTraceData data)
        // { MarkAsBGCThread(data); };
        // clrPrivate.GCBGCRevisit += delegate (BGCRevisitTraceData data)
        // { MarkAsBGCThread(data); };
        // rawEvents.Clr.ThreadPoolWorkerThreadAdjustmentSample += delegate (ThreadPoolWorkerThreadAdjustmentSampleTraceData data)
        // {
        //     CategorizeThread(data, ".NET ThreadPool");
        // };
        // rawEvents.Clr.ThreadPoolIODequeue += delegate (ThreadPoolIOWorkTraceData data)
        // { CategorizeThread(data, ".NET IO ThreadPool Worker", true); };

        // var fxParser = new FrameworkEventSourceTraceEventParser(rawEvents);
        // fxParser.ThreadPoolDequeueWork += delegate (ThreadPoolDequeueWorkArgs data)
        // { CategorizeThread(data, ".NET ThreadPool Worker"); };
        // fxParser.ThreadTransferReceive += delegate (ThreadTransferReceiveArgs data)
        // { CategorizeThread(data, ".NET ThreadPool Worker"); };
    }

    internal static string GetFullName(MethodLoadUnloadVerboseTraceData data)
    {
        string sig = data.MethodSignature;
        int parens = sig.IndexOf('(');
        string args;
        if (parens >= 0)
        {
            args = sig.Substring(parens);
        }
        else
        {
            args = "";
        }

        string fullName = data.MethodNamespace + "." + data.MethodName + args;
        return fullName;
    }

    /// <summary>
    /// Converts a Relative MSec time to the Query Performance Counter (QPC) ticks
    /// </summary>
    internal long RelativeMSecToQPC(double relativeMSec)
    {
        // TODO
        return (long)relativeMSec;
        // Debug.Assert(sessionStartTimeQPC != 0 && _syncTimeQPC != 0 && _syncTimeUTC.Ticks != 0 && _QPCFreq != 0);
        // return (long)(relativeMSec * _QPCFreq / 1000) + sessionStartTimeQPC;
    }
}
