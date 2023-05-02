using Microsoft.Diagnostics.Tracing.Etlx;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceThreads represents the list of threads in a process.
/// </summary>
internal sealed class TraceThreads : IEnumerable<TraceThread>
{
    /// <summary>
    /// Enumerate all the threads that occurred in the trace log. It does so in order of their thread
    /// offset events in the log.
    /// </summary>
    IEnumerator<TraceThread> IEnumerable<TraceThread>.GetEnumerator()
    {
        for (int i = 0; i < threads.Count; i++)
        {
            yield return threads[i];
        }
    }
    /// <summary>
    /// The count of the number of TraceThreads in the trace log.
    /// </summary>
    public int Count { get { return threads.Count; } }
    /// <summary>
    /// Each thread that occurs in the log is given a unique index (which unlike the PID is unique), that
    /// ranges from 0 to Count - 1.   Return the TraceThread for the given index.
    /// </summary>
    public TraceThread? this[ThreadIndex threadIndex]
    {
        get
        {
            if (threadIndex == ThreadIndex.Invalid)
            {
                return null;
            }

            return threads[(int)threadIndex];
        }
    }

    /// <summary>
    /// Given an OS thread ID and a time, return the last TraceThread that has the same thread ID,
    /// and whose start time is less than 'timeRelativeMSec'. If 'timeRelativeMSec' is during the thread's lifetime this
    /// is guaranteed to be the correct thread.
    /// </summary>
    public TraceThread? GetThread(int threadID, double timeRelativeMSec)
    {
        long timeQPC = log.RelativeMSecToQPC(timeRelativeMSec);
        InitThread();
        TraceThread? ret = null;
        threadIDtoThread!.TryGetValue(threadID, timeQPC, out ret);
        return ret;
    }
    #region Private
    internal TraceThread? GetThread(int threadID, long timeQPC)
    {
        InitThread();
        TraceThread? ret = null;
        threadIDtoThread!.TryGetValue(threadID, timeQPC, out ret);
        return ret;
    }

    /// <summary>
    /// TraceThreads   represents the collection of threads in a process.
    ///
    /// </summary>
    internal TraceThreads(TraceLog log)
    {
        this.log = log;
    }
    private void InitThread()
    {
        // Create a cache for this because it can be common
        if (threadIDtoThread == null)
        {
            threadIDtoThread = new HistoryDictionary<int, TraceThread>(1000);
            for (int i = 0; i < threads.Count; i++)
            {
                var thread = threads[i];
                threadIDtoThread.Add(thread.ThreadID, thread.startTimeQPC, thread);
            }
        }
    }

    /// <summary>
    /// Get the thread for threadID and timeQPC.   Create if necessary.  If 'isThreadCreateEvent' is true,
    /// then force  the creation of a new thread EVEN if the thread exist since we KNOW it is a new thread
    /// (and somehow we missed the threadEnd event).   Process is the process associated with the thread.
    /// It can be null if you really don't know the process ID.  We will try to fill it in on another event
    /// where we DO know the process id (ThreadEnd event).
    /// </summary>
    internal TraceThread GetOrCreateThread(int threadID, long timeQPC, TraceProcess process, bool isThreadCreateEvent = false)
    {
        TraceThread? retThread = GetThread(threadID, timeQPC);

        // ThreadIDs are machine wide, however, they are also reused. Thus GetThread CAN give you an OLD thread IF
        // we are missing thread Death and creation events (thus silently it gets reused on another process). This
        // can happen easily because Kernel events (which log thread creation and deaths), have a circular buffer that
        // might get exhausted before user mode events (e.g. CLR events). Thus try to keep as much sanity as possible
        // by confirming that if the thread we get back had a process, the process is the same as the process for
        // the current event. If not, we assume that there were silent thread deaths and creations and simply create
        // a new TraceThread. Note that this problem mostly goes away when we have just a single circular buffer since
        // we won't lose the Thread death and creation events.
        if (process.ProcessID != -1 && retThread != null && retThread.process.ProcessID != -1 && process.ProcessID != retThread.process.ProcessID)
        {
            retThread = null;
        }

        if (retThread == null || isThreadCreateEvent)
        {
            InitThread();

            retThread = new TraceThread(threadID, process, (ThreadIndex)threads.Count);
            if (isThreadCreateEvent)
            {
                retThread.startTimeQPC = timeQPC;
            }

            threads.Add(retThread);
            threadIDtoThread!.Add(threadID, timeQPC, retThread);
        }

        // Set the process if we had to set this threads process ID to the 'unknown' process.
        if (process != null && retThread.process.ProcessID == -1)
        {
            retThread.process = process;
        }

        return retThread;
    }

    // State variables.
    private GrowableArray<TraceThread> threads;          // The threads ordered in time.
    private TraceLog log;
    internal HistoryDictionary<int, TraceThread>? threadIDtoThread;

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }
    #endregion
}
