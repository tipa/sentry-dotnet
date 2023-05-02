using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// A TraceMethod represents the symbolic information for a particular method.   To maximizes haring a TraceMethod
/// has very little state, just the module and full method name.
/// </summary>
internal sealed class TraceMethod
{
    /// <summary>
    /// Each Method in the TraceLog is given an index that uniquely identifies it.  This return this index for this TraceMethod
    /// </summary>
    public MethodIndex MethodIndex { get { return methodIndex; } }
    /// <summary>
    /// The full name of the method (Namespace.ClassName.MethodName).
    /// </summary>
    public string FullMethodName { get { return methods.FullMethodName(methodIndex); } }
    /// <summary>
    /// .Net runtime methods have a token (32 bit number) that uniquely identifies it in the meta data of the managed DLL.
    /// This property returns this token. Returns 0 for unmanaged code or method not found.
    /// </summary>
    public int MethodToken { get { return methods.MethodToken(methodIndex); } }
    /// <summary>
    /// For native code the RVA (relative virtual address, which is the offset from the base of the file in memory)
    /// for the method in the file. Returns 0 for managed code or method not found;
    /// </summary>
    public int MethodRva { get { return methods.MethodRva(methodIndex); } }
    /// <summary>
    /// Returns the index for the DLL ModuleFile (which represents its file path) associated with this method
    /// </summary>
    public ModuleFileIndex MethodModuleFileIndex { get { return methods.MethodModuleFileIndex(methodIndex); } }
    /// <summary>
    /// Returns the ModuleFile (which represents its file path) associated with this method
    /// </summary>
    public TraceModuleFile? MethodModuleFile { get { return methods.codeAddresses.ModuleFiles[MethodModuleFileIndex]; } }

    #region private
    internal TraceMethod(TraceMethods methods, MethodIndex methodIndex)
    {
        this.methods = methods;
        this.methodIndex = methodIndex;
    }

    /// <summary>
    /// Returns a new string prefixed with the optimization tier if it would be useful. Typically used to adorn a method's
    /// name with the optimization tier of the specific code version of the method.
    /// </summary>
    internal static string PrefixOptimizationTier(string str, OptimizationTier optimizationTier)
    {
        if (optimizationTier == OptimizationTier.Unknown || string.IsNullOrWhiteSpace(str))
        {
            return str;
        }
        return $"[{optimizationTier}]{str}";
    }

    private TraceMethods methods;
    private MethodIndex methodIndex;
    #endregion
}
