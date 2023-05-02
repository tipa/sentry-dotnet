using FastSerialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// Call stacks are so common in most traces, that having a .NET object (a TraceEventCallStack) for
/// each one is often too expensive.   As optimization, TraceLog also assigns a call stack index
/// to every call stack and this index uniquely identifies the call stack in a very light weight fashion.
/// <para>
/// To be useful, however you need to be able to ask questions about a call stack index without creating
/// a TraceEventCallStack.   This is the primary purpose of a TraceCallStacks (accessible from TraceLog.CallStacks).
/// It has a set of
/// methods that take a CallStackIndex and return properties of the call stack (like its caller or
/// its code address).
/// </para>
/// </summary>
internal sealed class TraceCallStacks : IEnumerable<TraceCallStack>
{
    /// <summary>
    /// Returns the count of call stack indexes (all Call Stack indexes are strictly less than this).
    /// </summary>
    public int Count { get { return callStacks.Count; } }
    /// <summary>
    /// Given a call stack index, return the code address index representing the top most frame associated with it
    /// </summary>
    public CodeAddressIndex CodeAddressIndex(CallStackIndex stackIndex) { return callStacks[(int)stackIndex].codeAddressIndex; }
    /// <summary>
    /// Given a call stack index, look up the call stack  index for caller.  Returns CallStackIndex.Invalid at top of stack.
    /// </summary>
    public CallStackIndex Caller(CallStackIndex stackIndex)
    {
        CallStackIndex ret = callStacks[(int)stackIndex].callerIndex;
        Debug.Assert(ret < stackIndex);         // Stacks should be getting 'smaller'
        if (ret < 0)                            // We encode the threads of the stack as the negative thread index.
        {
            ret = CallStackIndex.Invalid;
        }

        return ret;
    }
    /// <summary>
    /// Given a call stack index, returns the number of callers for the call stack
    /// </summary>
    public int Depth(CallStackIndex stackIndex)
    {
        int ret = 0;
        while (stackIndex >= 0)
        {
            Debug.Assert(ret < 1000000);       // Catches infinite recursion
            ret++;
            stackIndex = callStacks[(int)stackIndex].callerIndex;
        }
        return ret;
    }

    /// <summary>
    /// Given a call stack index, returns a TraceCallStack for it.
    /// </summary>
    public TraceCallStack? this[CallStackIndex callStackIndex]
    {
        get
        {
            // We don't bother interning.
            if (callStackIndex == CallStackIndex.Invalid)
            {
                return null;
            }

            return new TraceCallStack(this, callStackIndex);
        }
    }
    /// <summary>
    /// Returns the TraceCodeAddresses instance that can resolve CodeAddressIndexes in the TraceLog
    /// </summary>
    public TraceCodeAddresses CodeAddresses { get { return codeAddresses; } }
    /// <summary>
    /// Given a call stack index, returns the ThreadIndex which represents the thread for the call stack
    /// </summary>
    public ThreadIndex ThreadIndex(CallStackIndex stackIndex)
    {
        // Go to the thread of the stack
        while (stackIndex >= 0)
        {
            Debug.Assert(callStacks[(int)stackIndex].callerIndex < stackIndex);
            stackIndex = callStacks[(int)stackIndex].callerIndex;
        }
        // The threads of the stack is marked by a negative number, which is the thread index -2
        ThreadIndex ret = (ThreadIndex)((-((int)stackIndex)) - 2);
        Debug.Assert(-1 <= (int)ret && (int)ret < log.Threads.Count);
        return ret;
    }
    /// <summary>
    /// Given a call stack index, returns the TraceThread which represents the thread for the call stack
    /// </summary>
    public TraceThread? Thread(CallStackIndex stackIndex)
    {
        return log.Threads[ThreadIndex(stackIndex)];
    }

    #region private
    /// <summary>
    /// IEnumerable Support
    /// </summary>
    public IEnumerator<TraceCallStack> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[(CallStackIndex)i]!;
        }
    }

    internal TraceCallStacks(TraceLog log, TraceCodeAddresses codeAddresses)
    {
        this.log = log;
        this.codeAddresses = codeAddresses;
    }

    /// <summary>
    /// Used to 'undo' the effects of adding a eventToStack that you no longer want.  This happens when we find
    /// out that a eventToStack is actually got more callers in it (when a eventToStack is split).
    /// </summary>
    /// <param name="origSize"></param>
    internal void SetSize(int origSize)
    {
        callStacks.RemoveRange(origSize, callStacks.Count - origSize);
    }

    /// <summary>
    /// Returns an index that represents the 'threads' of the stack.  It encodes the thread which owns this stack into this.
    /// We encode this as -ThreadIndex - 2 (since -1 is the Invalid node)
    /// </summary>
    internal static CallStackIndex GetRootForThread(ThreadIndex threadIndex)
    {
        return (CallStackIndex)(-((int)threadIndex) + (int)CallStackIndex.Invalid - 1);
    }
    private static ThreadIndex GetThreadForRoot(CallStackIndex root)
    {
        ThreadIndex ret = (ThreadIndex)((-((int)root)) + (int)CallStackIndex.Invalid - 1);
        Debug.Assert(ret >= 0);
        return ret;
    }

    // internal unsafe CallStackIndex GetStackIndexForStackEvent(void* addresses,
    //     int addressCount, int pointerSize, TraceThread thread, CallStackIndex start = CallStackIndex.Invalid)
    // {
    //     if (addressCount == 0)
    //     {
    //         return CallStackIndex.Invalid;
    //     }

    //     if (start == CallStackIndex.Invalid)
    //     {
    //         start = GetRootForThread(thread.ThreadIndex);
    //     }

    //     return (pointerSize == 8) ?
    //         GetStackIndexForStackEvent64((ulong*)addresses, addressCount, thread.Process, start) :
    //         GetStackIndexForStackEvent32((uint*)addresses, addressCount, thread.Process, start);
    // }

    // private unsafe CallStackIndex GetStackIndexForStackEvent32(uint* addresses, int addressCount, TraceProcess process, CallStackIndex start)
    // {
    //     for (var it = &addresses[addressCount]; it-- != addresses;)
    //     {
    //         CodeAddressIndex codeAddress = codeAddresses.GetOrCreateCodeAddressIndex(process, *it);
    //         start = InternCallStackIndex(codeAddress, start);
    //     }

    //     return start;
    // }

    // private unsafe CallStackIndex GetStackIndexForStackEvent64(ulong* addresses, int addressCount, TraceProcess process, CallStackIndex start)
    // {
    //     for (var it = &addresses[addressCount]; it-- != addresses;)
    //     {
    //         CodeAddressIndex codeAddress = codeAddresses.GetOrCreateCodeAddressIndex(process, *it);
    //         start = InternCallStackIndex(codeAddress, start);
    //     }

    //     return start;
    // }

    internal CallStackIndex InternCallStackIndex(CodeAddressIndex codeAddressIndex, CallStackIndex callerIndex)
    {
        if (callStacks.Count == 0)
        {
            // allocate a reasonable size for the interning tables.
            callStacks = new GrowableArray<CallStackInfo>(10000);
            callees = new GrowableArray<List<CallStackIndex>?>(10000);
        }

        List<CallStackIndex> frameCallees;
        if (callerIndex < 0)        // Hit the last stack as we unwind to the root.  We need to encode the thread.
        {
            Debug.Assert(callerIndex != CallStackIndex.Invalid);        // We always end with the thread.
            int threadIndex = (int)GetThreadForRoot(callerIndex);
            if (threadIndex >= threads.Count)
            {
                threads.Count = threadIndex + 1;
            }

            frameCallees = threads[threadIndex] ?? (threads[threadIndex] = new List<CallStackIndex>());
        }
        else
        {
            frameCallees = callees[(int)callerIndex] ?? (callees[(int)callerIndex] = new List<CallStackIndex>(4));
        }

        // Search backwards, assuming that most recently added is the most likely hit.
        for (int i = frameCallees.Count - 1; i >= 0; --i)
        {
            CallStackIndex calleeIndex = frameCallees[i];
            if (callStacks[(int)calleeIndex].codeAddressIndex == codeAddressIndex)
            {
                Debug.Assert(calleeIndex > callerIndex);
                return calleeIndex;
            }
        }

        CallStackIndex ret = (CallStackIndex)callStacks.Count;
        callStacks.Add(new CallStackInfo(codeAddressIndex, callerIndex));
        frameCallees.Add(ret);
        callees.Add(null);
        Debug.Assert(callees.Count == callStacks.Count);
        return ret;
    }

    private struct CallStackInfo
    {
        internal CallStackInfo(CodeAddressIndex codeAddressIndex, CallStackIndex callerIndex)
        {
            this.codeAddressIndex = codeAddressIndex;
            this.callerIndex = callerIndex;
        }

        internal CodeAddressIndex codeAddressIndex;
        internal CallStackIndex callerIndex;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }

    // This is only used when converting maps.  Maps a call stack index to a list of call stack indexes that
    // were callees of it.    This is the list you need to search when interning.  There is also 'threads'
    // which is the list of call stack indexes where stack crawling stopped.
    private GrowableArray<List<CallStackIndex>?> callees;    // For each callstack, these are all the call stacks that it calls.
    private GrowableArray<List<CallStackIndex>> threads;    // callees for threads of stacks, one for each thread
    private GrowableArray<CallStackInfo> callStacks;        // a field on CallStackInfo
    // private DeferedRegion lazyCallStacks;
    private TraceCodeAddresses codeAddresses;
    private TraceLog log;
    #endregion
}
