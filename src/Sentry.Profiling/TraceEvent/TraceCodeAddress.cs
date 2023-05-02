using FastSerialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

namespace Sentry.Profiling.DiagnosticsTracing;

/// <summary>
/// Conceptually a TraceCodeAddress represents a particular point of execution within a particular
/// line of code in some source code.    As a practical matter, they are represented two ways
/// depending on whether the code is managed or not.
/// <para>* For native code (or NGened code), it is represented as a virtual address along with the loaded native
/// module that includes that address along with its load address.  A code address does NOT
/// know its process because they can be shared among all processes that load a particular module
/// at a particular location.   These code addresses will not have methods associated with them
/// unless symbols information (PDBS) are loaded for the module using the LookupSymbolsForModule.
/// </para>
/// <para> * For JIT compiled managed code, the address in a process is eagerly resolved into a method, module
/// and an IL offset and that is stored in the TraceCodeAddress.
/// </para>
///<para> Sometimes it is impossible to even determine the module associated with a virtual
///address in a process.   These are represented as simply the virtual address.
///</para>
///<para>
///Because code addresses are so numerous, consider using CodeAddressIndex instead of TraceCodeAddress
///to represent a code address.   Methods on TraceLog.CodeAddresses can access all the information
///that would be in a TraceCodeAddress from a CodeAddressIndex without the overhead of creating
///a TraceCodeAddress object.
///</para>
/// </summary>
internal sealed class TraceCodeAddress
{
    /// <summary>
    /// The CodeAddressIndex that uniquely identifies the same code address as this TraceCodeAddress
    /// </summary>
    public CodeAddressIndex CodeAddressIndex { get { return codeAddressIndex; } }
    /// <summary>
    /// The Virtual address of the code address in the process.  (Note that the process is unknown by the code address to allow for sharing)
    /// </summary>
    public Address Address { get { return codeAddresses.Address(codeAddressIndex); } }
    /// <summary>
    /// The full name (Namespace name.class name.method name) of the method associated with this code address.
    /// Returns the empty string if no method is associated with the code address.
    /// </summary>
    public string FullMethodName
    {
        get
        {
            MethodIndex methodIndex = codeAddresses.MethodIndex(codeAddressIndex);
            if (methodIndex == MethodIndex.Invalid)
            {
                return "";
            }

            return codeAddresses.Methods.FullMethodName(methodIndex);
        }
    }
    /// <summary>
    /// Returns the TraceMethod associated with this code address or null if there is none.
    /// </summary>
    public TraceMethod? Method
    {
        get
        {
            MethodIndex methodIndex = codeAddresses.MethodIndex(codeAddressIndex);
            if (methodIndex == MethodIndex.Invalid)
            {
                return null;
            }
            else
            {
                return codeAddresses.Methods[methodIndex];
            }
        }
    }
    /// <summary>
    /// If the TraceCodeAddress is associated with managed code, return the IL offset within the method.    If the method
    /// is unmanaged -1 is returned.   To determine the IL offset the PDB for the NGEN image (for NGENed code) or the
    /// correct .NET events (for JIT compiled code) must be present.   If this information is not present -1 is returned.
    /// </summary>
    public int ILOffset { get { return codeAddresses.ILOffset(codeAddressIndex); } }
    // /// <summary>
    // /// A TraceCodeAddress can contain a method name, but does not contain number information.   To
    // /// find line number information you must read the PDB again and fetch it.   This is what
    // /// GetSoruceLine does.
    // /// <para>
    // /// Given a SymbolReader (which knows how to look up PDBs) find a SourceLocation (which represents a
    // /// particular line number in a particular source file associated with the current TraceCodeAddress.
    // /// Returns null if anything goes wrong (and diagnostic information will be written to the
    // /// log file associated with the SymbolReader.
    // /// </para>
    // /// </summary>
    // public SourceLocation GetSourceLine(SymbolReader reader) { return codeAddresses.GetSourceLine(reader, codeAddressIndex); }

    /// <summary>
    /// Returns the TraceModuleFile representing the DLL path associated with this code address (or null if not known)
    /// </summary>
    public TraceModuleFile? ModuleFile
    {
        get
        {
            ModuleFileIndex moduleFileIndex = codeAddresses.ModuleFileIndex(codeAddressIndex);
            if (moduleFileIndex == ModuleFileIndex.Invalid)
            {
                return null;
            }
            else
            {
                return codeAddresses.ModuleFiles[moduleFileIndex];
            }
        }
    }
    /// <summary>
    /// ModuleName is the name of the file without path or extension.
    /// </summary>
    public string ModuleName
    {
        get
        {
            return ModuleFile?.Name ?? "";
        }
    }
    /// <summary>
    /// The full path name of the DLL associated with this code address.  Returns empty string if not known.
    /// </summary>
    public string ModuleFilePath
    {
        get
        {
            return ModuleFile?.FilePath ?? "";
        }
    }
    /// <summary>
    /// The CodeAddresses container that this Code Address lives within
    /// </summary>
    public TraceCodeAddresses CodeAddresses { get { return codeAddresses; } }

    #region private
    internal TraceCodeAddress(TraceCodeAddresses codeAddresses, CodeAddressIndex codeAddressIndex)
    {
        this.codeAddresses = codeAddresses;
        this.codeAddressIndex = codeAddressIndex;
    }

    private TraceCodeAddresses codeAddresses;
    private CodeAddressIndex codeAddressIndex;
    #endregion
}
