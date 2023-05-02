using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Sentry.Internal;
using Sentry.Protocol;

namespace Sentry.Profiling.DiagnosticsTracing;

// A list of frame indexes.
using SentryProfileStackTrace = HashableGrowableArray<int>;

/// <summary>
/// Processes EventPipeEventSource to produce a SampleProfile.
/// </summary>
internal class EventPipeProcessor
{
    private readonly SentryOptions _options;
    private readonly EventPipeEventSource _eventSource;

    /// Output profile being built.
    private readonly SampleProfile _profile = new();

    // A sparse array that maps from StackSourceFrameIndex to an index in the output Profile.frames.
    private readonly SparseScalarArray<int> _frameIndexes = new(-1, 1000);

    // A dictionary from a StackTrace sealed array to an index in the output Profile.stacks.
    private readonly Dictionary<SentryProfileStackTrace, int> _stackIndexes = new(100);

    // A sparse array mapping from a ThreadIndex to an index in Profile.Threads.
    private readonly SparseScalarArray<int> _threadIndexes = new(-1, 20);

    // A sparse array mapping from an ActivityIndex to an index in Profile.Threads.
    private readonly SparseScalarArray<int> _activityIndexes = new(-1, 100);

    public double MaxTimestampMs { get; set; } = double.MaxValue;

    public EventPipeProcessor(SentryOptions options, EventPipeEventSource eventSource)
    {
        _options = options;
        _eventSource = eventSource;
        var sampleEventParser = new SampleProfilerTraceEventParser(_eventSource);
        // TODO sampleEventParser.ThreadSample += OnSampledProfile;
    }

    public SampleProfile Process(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(_eventSource.StopProcessing);
        _eventSource.Process();
        registration.Unregister();
        return _profile;
    }

    // private void AddSample(TraceThread thread, TraceActivity activity, StackSourceCallStackIndex callstackIndex, double timestampMs)
    // {
    //     if (thread.ThreadIndex == ThreadIndex.Invalid || callstackIndex == StackSourceCallStackIndex.Invalid)
    //     {
    //         return;
    //     }

    //     // Trim samples coming after the profiling has been stopped (i.e. after the Stop() IPC request has been sent).
    //     if (timestampMs > MaxTimestampMs)
    //     {
    //         // We can completely stop processing after the first sample that is after the timeout. Samples are
    //         // ordered (I've checked this manually so I hope that assumption holds...) so no need to go through the rest.
    //         _eventSource.StopProcessing();
    //         return;
    //     }

    //     var stackIndex = AddStackTrace(callstackIndex);
    //     if (stackIndex < 0)
    //     {
    //         return;
    //     }

    //     var threadIndex = AddThreadOrActivity(thread, activity);
    //     if (threadIndex < 0)
    //     {
    //         return;
    //     }

    //     _profile.Samples.Add(new()
    //     {
    //         Timestamp = (ulong)(timestampMs * 1_000_000),
    //         StackId = stackIndex,
    //         ThreadId = threadIndex
    //     });
    // }

    // /// <summary>
    // /// Adds stack trace and frames, if missing.
    // /// </summary>
    // /// <returns>The index into the Profile's stacks list</returns>
    // private int AddStackTrace(StackSourceCallStackIndex callstackIndex)
    // {
    //     SentryProfileStackTrace stackTrace = new(10);
    //     StackSourceFrameIndex tlFrameIndex;
    //     while (callstackIndex != StackSourceCallStackIndex.Invalid)
    //     {
    //         tlFrameIndex = _stackSource.GetFrameIndex(callstackIndex);

    //         if (tlFrameIndex == StackSourceFrameIndex.Invalid)
    //         {
    //             break;
    //         }

    //         // "tlFrameIndex" may point to "CodeAddresses" or "Threads" or "Processes"
    //         // See TraceEventStackSource.GetFrameName() code for details.
    //         // We only care about the CodeAddresses bit because we don't want to show Threads and Processes in the stack trace.
    //         CodeAddressIndex codeAddressIndex = _stackSource.GetFrameCodeAddress(tlFrameIndex);
    //         if (codeAddressIndex != CodeAddressIndex.Invalid)
    //         {
    //             stackTrace.Add(AddStackFrame(codeAddressIndex));
    //             callstackIndex = _stackSource.GetCallerIndex(callstackIndex);
    //         }
    //         else
    //         {
    //             // No need to traverse further up the stack when we're on the thread/process.
    //             break;
    //         }
    //     }

    //     int result = -1;
    //     if (stackTrace.Count > 0)
    //     {
    //         stackTrace.Seal();
    //         if (!_stackIndexes.TryGetValue(stackTrace, out result))
    //         {
    //             stackTrace.Trim(10);
    //             _profile.Stacks.Add(stackTrace);
    //             result = _profile.Stacks.Count - 1;
    //             _stackIndexes[stackTrace] = result;
    //         }
    //     }

    //     return result;
    // }

    // /// <summary>
    // /// Check if the frame is already stored in the output Profile, or adds it.
    // /// </summary>
    // /// <returns>The index to the output Profile frames array.</returns>
    // private int AddStackFrame(CodeAddressIndex codeAddressIndex)
    // {
    //     var key = (int)codeAddressIndex;

    //     if (!_frameIndexes.ContainsKey(key))
    //     {
    //         _profile.Frames.Add(CreateStackFrame(codeAddressIndex));
    //         _frameIndexes[key] = _profile.Frames.Count - 1;
    //     }

    //     return _frameIndexes[key];
    // }

    // /// <summary>
    // /// Check if the thread is already stored in the output Profile, or adds it.
    // /// </summary>
    // /// <returns>The index to the output Profile frames array.</returns>
    // private int AddThreadOrActivity(TraceThread thread, TraceActivity activity)
    // {
    //     if (activity.IsThreadActivity)
    //     {
    //         var key = (int)thread.ThreadIndex;

    //         if (!_threadIndexes.ContainsKey(key))
    //         {
    //             _profile.Threads.Add(new()
    //             {
    //                 Name = thread.ThreadInfo ?? $"Thread {thread.ThreadID}",
    //             });
    //             _threadIndexes[key] = _profile.Threads.Count - 1;
    //         }

    //         return _threadIndexes[key];
    //     }
    //     else
    //     {
    //         var key = (int)activity.Index;

    //         if (!_activityIndexes.ContainsKey(key))
    //         {
    //             _profile.Threads.Add(new()
    //             {
    //                 Name = $"Activity {ActivityPath(activity)}",
    //             });
    //             _activityIndexes[key] = _profile.Threads.Count - 1;
    //         }

    //         return _activityIndexes[key];
    //     }
    // }

    // private static string ActivityPath(TraceActivity activity)
    // {
    //     var creator = activity.Creator;
    //     if (creator is null || creator.IsThreadActivity)
    //     {
    //         return activity.Index.ToString();
    //     }
    //     else
    //     {
    //         return $"{ActivityPath(creator)}/{activity.Index.ToString()}";
    //     }
    // }

    // private SentryStackFrame CreateStackFrame(CodeAddressIndex codeAddressIndex)
    // {
    //     var frame = new SentryStackFrame();

    //     var methodIndex = _traceLog.CodeAddresses.MethodIndex(codeAddressIndex);
    //     if (_traceLog.CodeAddresses.Methods[methodIndex] is { } method)
    //     {
    //         frame.Function = method.FullMethodName;

    //         TraceModuleFile moduleFile = method.MethodModuleFile;
    //         if (moduleFile is not null)
    //         {
    //             frame.Module = moduleFile.Name;
    //         }

    //         // Displays the optimization tier of each code version executed for the method. E.g. "QuickJitted"
    //         // Doesn't seem very useful (not much users can do with this information) so disabling for now.
    //         // if (frame.Function is not null)
    //         // {
    //         //     var optimizationTier = _traceLog.CodeAddresses.OptimizationTier(codeAddressIndex);
    //         //     if (optimizationTier != Microsoft.Diagnostics.Tracing.Parsers.Clr.OptimizationTier.Unknown)
    //         //     {
    //         //         frame.Function = $"{frame.Function} {{{optimizationTier}}}";
    //         //     }
    //         // }

    //         frame.ConfigureAppFrame(_options);
    //     }
    //     else
    //     {
    //         // native frame
    //         frame.InApp = false;
    //     }

    //     // TODO enable this once we implement symbolication (we will need to send debug_meta too), see StackTraceFactory.
    //     // if (_traceLog.CodeAddresses.ILOffset(codeAddressIndex) is { } ilOffset && ilOffset >= 0)
    //     // {
    //     //     frame.InstructionOffset = ilOffset;
    //     // }
    //     // else if (_traceLog.CodeAddresses.Address(codeAddressIndex) is { } address)
    //     // {
    //     //     frame.InstructionAddress = $"0x{address:x}";
    //     // }

    //     return frame;
    // }

    // private void OnSampledProfile(TraceEvent data)
    // {
    //     TraceThread thread = data.Thread();
    //     if (thread != null)
    //     {
    //         TraceActivity activity = _activityComputer.GetCurrentActivity(thread);
    //         // TODO expose ActivityComputer.GetCallStackWithActivityFrames() and use it - it's a bit faster because we also need to fetch thread & activity for AddSample().
    //         StackSourceCallStackIndex stackFrameIndex = _activityComputer.GetCallStack(_stackSource, data, GetTopFramesForActivityComputerCase(data, thread));
    //         AddSample(thread, activity, stackFrameIndex, data.TimeStampRelativeMSec);
    //     }
    //     else
    //     {
    //         Debug.WriteLine("Warning, no thread at " + data.TimeStampRelativeMSec.ToString("f3"));
    //     }
    // }

    // /// <summary>
    // /// Returns a function that figures out the top (closest to stack root) frames for an event.  Often
    // /// this returns null which means 'use the normal thread-process frames'.
    // /// Normally this stack is for the current time, but if 'getAtCreationTime' is true, it will compute the
    // /// stack at the time that the current activity was CREATED rather than the current time.  This works
    // /// better for await time.
    // /// </summary>
    // private Func<TraceThread, StackSourceCallStackIndex>? GetTopFramesForActivityComputerCase(TraceEvent data, TraceThread thread, bool getAtCreationTime = false)
    // {
    //     return null;
    //     // return (topThread => _startStopActivities.GetCurrentStartStopActivityStack(_stackSource, thread, topThread, getAtCreationTime));
    // }
}
