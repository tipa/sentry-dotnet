using FastSerialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// Methods are so common in most traces, that having a .NET object (a TraceMethod) for
/// each one is often too expensive.   As optimization, TraceLog also assigns a method index
/// to every method and this index uniquely identifies the method in a very light weight fashion.
/// <para>
/// To be useful, however you need to be able to ask questions about a method index without creating
/// a TraceMethod.   This is the primary purpose of a TraceMethods (accessible from TraceLog.CodeAddresses.Methods).
/// It has a set of
/// methods that take a MethodIndex and return properties of the method (like its name, and module file)
/// </para>
/// </summary>
internal sealed class TraceMethods : IEnumerable<TraceMethod>
{
    /// <summary>
    /// Returns the count of method indexes.  All MethodIndexes are strictly less than this.
    /// </summary>
    public int Count { get { return methods.Count; } }

    /// <summary>
    /// Given a method index, if the method is managed return the IL meta data MethodToken (returns 0 for native code)
    /// </summary>
    public int MethodToken(MethodIndex methodIndex)
    {
        if (methodIndex == MethodIndex.Invalid)
        {
            return 0;
        }
        else
        {
            var value = methods[(int)methodIndex].methodDefOrRva;
            if (value < 0)
            {
                value = 0;      // unmanaged code, return 0
            }

            return value;
        }
    }
    /// <summary>
    /// Given a method index, return the Method's RVA (offset from the base of the DLL in memory)  (returns 0 for managed code)
    /// </summary>
    public int MethodRva(MethodIndex methodIndex)
    {
        if (methodIndex == MethodIndex.Invalid)
        {
            return 0;
        }
        else
        {
            var value = methods[(int)methodIndex].methodDefOrRva;
            if (value > 0)
            {
                value = 0;      // managed code, return 0
            }

            return -value;
        }
    }
    /// <summary>
    /// Given a method index, return the index for the ModuleFile associated with the Method Index.
    /// </summary>
    public ModuleFileIndex MethodModuleFileIndex(MethodIndex methodIndex)
    {
        if (methodIndex == MethodIndex.Invalid)
        {
            return ModuleFileIndex.Invalid;
        }
        else
        {
            return methods[(int)methodIndex].moduleIndex;
        }
    }
    /// <summary>
    /// Given a method index, return the Full method name (Namespace.ClassName.MethodName) associated with the Method Index.
    /// </summary>
    public string FullMethodName(MethodIndex methodIndex)
    {
        if (methodIndex == MethodIndex.Invalid)
        {
            return "";
        }
        else
        {
            return methods[(int)methodIndex].fullMethodName;
        }
    }

    /// <summary>
    /// Given a method index, return a TraceMethod that also represents the method.
    /// </summary>
    public TraceMethod? this[MethodIndex methodIndex]
    {
        get
        {
            if (methodObjects == null || (int)methodIndex >= methodObjects.Length)
            {
                methodObjects = new TraceMethod[(int)methodIndex + 16];
            }

            if (methodIndex == MethodIndex.Invalid)
            {
                return null;
            }

            TraceMethod ret = methodObjects[(int)methodIndex];
            if (ret == null)
            {
                ret = new TraceMethod(this, methodIndex);
                methodObjects[(int)methodIndex] = ret;
            }
            return ret;
        }
    }

    #region private
    internal TraceMethods(TraceCodeAddresses codeAddresses) { this.codeAddresses = codeAddresses; }

    /// <summary>
    /// IEnumerable support
    /// </summary>
    /// <returns></returns>
    public IEnumerator<TraceMethod> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[(MethodIndex)i]!;
        }
    }

    // Positive is a token, negative is an RVA
    internal MethodIndex NewMethod(string fullMethodName, ModuleFileIndex moduleIndex, int methodTokenOrRva)
    {
        MethodIndex ret = (MethodIndex)methods.Count;
        methods.Add(new MethodInfo(fullMethodName, moduleIndex, methodTokenOrRva));
        return ret;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException(); // GetEnumerator
    }

    private struct MethodInfo
    {
        // Positive is a token, negative is an RVA
        internal MethodInfo(string fullMethodName, ModuleFileIndex moduleIndex, int methodTokenOrRva)
        {
            this.fullMethodName = fullMethodName;
            this.moduleIndex = moduleIndex;
            methodDefOrRva = methodTokenOrRva;
        }
        internal string fullMethodName;
        internal ModuleFileIndex moduleIndex;
        internal int methodDefOrRva;               // For managed code, this is the token, (positive) for unmanaged it is -rva (rvas have to be < 2Gig).
    }

    // private DeferedRegion lazyMethods;
    private GrowableArray<MethodInfo> methods;
    private TraceMethod[]? methodObjects;
    internal TraceCodeAddresses codeAddresses;
    #endregion
}
