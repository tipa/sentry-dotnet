using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceThread represents a thread of execution in a process.
/// </summary>
internal sealed class TraceThread
{
    /// <summary>
    /// The OS process ID associated with the process.
    /// </summary>
    public int ThreadID { get { return threadID; } }
    /// <summary>
    /// The index into the logical array of TraceThreads for this process.  Unlike ThreadId (which
    /// may be reused after the thread dies) the T index is unique over the log.
    /// </summary>
    public ThreadIndex ThreadIndex { get { return threadIndex; } }
    /// <summary>
    /// The process associated with the thread.
    /// </summary>
    public TraceProcess Process { get { return process; } }

    // /// <summary>
    // /// The time when the thread started.  Returns the time the trace started if the thread existed when the trace started.
    // /// Returned as a DateTime
    // /// </summary>
    // public DateTime StartTime { get { return Process.Log.QPCTimeToDateTimeUTC(startTimeQPC).ToLocalTime(); } }
    // /// <summary>
    // /// The time when the thread started.  Returns the time the trace started if the thread existed when the trace started.
    // /// Returned as the number of MSec from the beginning of the trace.
    // /// </summary>
    // public double StartTimeRelativeMSec { get { return process.Log.QPCTimeToRelMSec(startTimeQPC); } }
    // /// <summary>
    // /// The time when the thread ended.  Returns the time the trace ended if the thread existed when the trace ended.
    // /// Returned as a DateTime
    // /// </summary>
    // public DateTime EndTime { get { return Process.Log.QPCTimeToDateTimeUTC(endTimeQPC).ToLocalTime(); } }
    // /// <summary>
    // /// The time when the thread ended.  Returns the time the trace ended if the thread existed when the trace ended.
    // /// Returned as the number of MSec from the beginning of the trace.
    // /// </summary>
    // public double EndTimeRelativeMSec { get { return process.Log.QPCTimeToRelMSec(endTimeQPC); } }
    // /// <summary>
    // /// The amount of CPU time spent on this thread based on the kernel CPU sampling events.
    // /// </summary>
    // public float CPUMSec { get { return (float)(cpuSamples * Process.Log.SampleProfileInterval.TotalMilliseconds); } }
    // /// <summary>
    // /// Filters events to only those for a particular thread.
    // /// </summary>
    // public TraceEvents EventsInThread
    // {
    //     get
    //     {
    //         return Process.Log.Events.Filter(startTimeQPC, endTimeQPC, delegate (TraceEvent anEvent)
    //         {
    //             return anEvent.ThreadID == ThreadID;
    //         });
    //     }
    // }
    // /// <summary>
    // /// Filters events to only those that occurred during the time a the thread was alive.
    // /// </summary>
    // public TraceEvents EventsDuringThread
    // {
    //     get
    //     {
    //         return Process.Log.Events.FilterByTime(StartTimeRelativeMSec, EndTimeRelativeMSec);
    //     }
    // }

    /// <summary>
    /// REturns the activity this thread was working on at the time instant 'relativeMsec'
    /// </summary>
    [Obsolete("Likely to be removed Replaced by ActivityMap.GetActivity(TraceThread, double)")]
    public ActivityIndex GetActivityIndex(double relativeMSec)
    {

        throw new InvalidOperationException("Don't use activities right now");
    }
    /// <summary>
    /// Represents the "default" activity for the thread, the activity that no one has set
    /// </summary>
    [Obsolete("Likely to be removed Replaced by ActivityComputer.GetDefaultActivity(TraceThread)")]
    public ActivityIndex DefaultActivityIndex
    {
        get
        {
            throw new InvalidOperationException("Don't use activities right now");
        }
    }

    // internal void ThreadEnd(ThreadTraceData data, TraceProcess process)
    // {
    // }

    /// <summary>
    /// ThreadInfo is a string that identifies the thread symbolically.   (e.g. .NET Threadpool, .NET GC)  It may return null if there is no useful symbolic name.
    /// </summary>
    public string? ThreadInfo { get { return threadInfo; } }
    // /// <summary>
    // /// VerboseThreadName is a name for the thread including the ThreadInfo and the CPU time used.
    // /// </summary>
    // public string VerboseThreadName
    // {
    //     get
    //     {
    //         if (verboseThreadName == null)
    //         {
    //             if (CPUMSec != 0)
    //             {
    //                 verboseThreadName = string.Format("Thread ({0}) CPU={1:f0}ms", ThreadID, CPUMSec);
    //             }
    //             else
    //             {
    //                 verboseThreadName = string.Format("Thread ({0})", ThreadID);
    //             }

    //             if (ThreadInfo != null)
    //             {
    //                 verboseThreadName += " (" + ThreadInfo + ")";
    //             }
    //         }
    //         return verboseThreadName;
    //     }
    // }

    /// <summary>
    /// The base of the thread's stack.  This is just past highest address in memory that is part of the stack
    /// (we don't really know the lower bound (userStackLimit is this lower bound at the time the thread was created
    /// which is not very useful).
    /// </summary>
    public Address UserStackBase { get { return userStackBase; } }

    #region Private
    /// <summary>
    /// Create a new TraceProcess.  It should only be done by log.CreateTraceProcess because
    /// only TraceLog is responsible for generating a new ProcessIndex which we need.   'processIndex'
    /// is a index that is unique for the whole log file (where as processID can be reused).
    /// </summary>
    internal TraceThread(int threadID, TraceProcess process, ThreadIndex threadIndex)
    {
        this.threadID = threadID;
        this.threadIndex = threadIndex;
        this.process = process;
        endTimeQPC = long.MaxValue;
        lastBlockingCSwitchEventIndex = EventIndex.Invalid;
    }


    private int threadID;
    private ThreadIndex threadIndex;
    internal TraceProcess process;
    internal long startTimeQPC;
    internal long endTimeQPC;
    internal int cpuSamples;
    internal string? threadInfo;
    internal Address userStackBase;
    private string? verboseThreadName;
    /// <summary>
    /// This is a list of the activities (snippet of threads) that have run on this
    /// thread.   They are ordered by time so you can binary search for your activity based
    /// on timestamp.
    /// </summary>
    internal GrowableArray<ActivityIndex> activityIds;

    /// <summary>
    /// We want to have the stack for when CSwtichs BLOCK as well as when they unblock.
    /// this variable keeps track of the last blocking CSWITCH on this thread so that we can
    /// compute this.   It is only used during generation of a TraceLog file.
    /// </summary>
    internal EventIndex lastBlockingCSwitchEventIndex;
    #endregion
}
