using FastSerialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceCallStack is a structure that represents a call stack as a linked list. Each TraceCallStack
/// contains two properties, the CodeAddress for the current frame, and the TraceCallStack of the
/// caller of this frame.   The Caller property will return null at the thread start frame.
/// </summary>
internal sealed class TraceCallStack
{
    /// <summary>
    ///  Return the CallStackIndex that uniquely identifies this call stack in the TraceLog.
    /// </summary>
    public CallStackIndex CallStackIndex { get { return stackIndex; } }
    /// <summary>
    /// Returns the TraceCodeAddress for the current method frame in the linked list of frames.
    /// </summary>
    public TraceCodeAddress? CodeAddress { get { return callStacks.CodeAddresses[callStacks.CodeAddressIndex(stackIndex)]; } }
    /// <summary>
    /// The TraceCallStack for the caller of of the method represented by this call stack.  Returns null at the end of the list.
    /// </summary>
    public TraceCallStack? Caller { get { return callStacks[callStacks.Caller(stackIndex)]; } }
    /// <summary>
    /// The depth (count of callers) of this call stack.
    /// </summary>
    public int Depth { get { return callStacks.Depth(stackIndex); } }
    #region private
    internal TraceCallStack(TraceCallStacks stacks, CallStackIndex stackIndex)
    {
        callStacks = stacks;
        this.stackIndex = stackIndex;
    }

    private TraceCallStacks callStacks;
    private CallStackIndex stackIndex;
    #endregion
}
