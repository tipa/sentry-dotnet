using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Address = System.UInt64;

namespace Sentry.Profiling.TraceEvent;

/// <summary>
/// A TraceManagedModule represents the loading of a .NET module into .NET AppDomain.
/// It represents the time that that module an be used in the AppDomain.
/// </summary>
internal sealed class TraceManagedModule : TraceLoadedModule
{
    /// <summary>
    /// The module ID that the .NET Runtime uses to identify the file (module) associated with this managed module
    /// </summary>
    public override long ModuleID { get { return (long)key; } }
    /// <summary>
    /// The Assembly ID that the .NET Runtime uses to identify the assembly associated with this managed module.
    /// </summary>
    public long AssemblyID { get { return assemblyID; } }
    /// <summary>
    /// Returns true if the managed module was loaded AppDOmain Neutral (its code can be shared by all appdomains in the process.
    /// </summary>
    public bool IsAppDomainNeutral { get { return (flags & ModuleFlags.DomainNeutral) != 0; } }
    /// <summary>
    /// If the managed module is an IL module that has an NGEN image, return it.
    /// </summary>
    public TraceLoadedModule? NativeModule { get { return nativeModule; } }

    #region Private
    internal TraceManagedModule(TraceProcess process, TraceModuleFile moduleFile, long moduleID)
        : base(process, moduleFile, moduleID)
    { }

    internal TraceLoadedModule? nativeModule;        // non-null for IL managed modules
    internal long assemblyID;
    internal ModuleFlags flags;

    #endregion
}
