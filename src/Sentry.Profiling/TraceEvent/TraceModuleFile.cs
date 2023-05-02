using Microsoft.Diagnostics.Tracing.Etlx;
using Address = System.UInt64;

/// <summary>
/// The TraceModuleFile represents a executable file that can be loaded into memory (either an EXE or a
/// DLL).  It represents the path on disk as well as the location in memory where it loads (or
/// its ModuleID if it is a managed module), but not the load or unload time or the process in which
/// it was loaded (this allows them to be shared within the trace).
/// </summary>
internal sealed class TraceModuleFile
{
    /// <summary>
    /// The ModuleFileIndex ID that uniquely identifies this module file.
    /// </summary>
    public ModuleFileIndex ModuleFileIndex { get { return moduleFileIndex; } }
    /// <summary>
    /// The moduleFile name associated with the moduleFile.  May be the empty string if the moduleFile has no moduleFile
    /// (dynamically generated).  For managed code, this is the IL moduleFile name.
    /// </summary>
    public string FilePath
    {
        get
        {
            if (fileName == null)
            {
                return "ManagedModule";
            }

            return fileName;
        }
    }
    /// <summary>
    /// This is the short name of the moduleFile (moduleFile name without extension).
    /// </summary>
    public string Name
    {
        get
        {
            if (name == null)
            {
                name = FilePath; // TraceLog.GetFileNameWithoutExtensionNoIllegalChars(FilePath);
            }
            return name;
        }
    }
    /// <summary>
    /// Returns the address in memory where the dll was loaded.
    /// </summary>
    public Address ImageBase { get { return imageBase; } }
    /// <summary>
    /// Returns the size of the DLL when loaded in memory
    /// </summary>
    public int ImageSize { get { return imageSize; } }
    /// <summary>
    /// Returns the address just past the memory the module uses.
    /// </summary>
    public Address ImageEnd { get { return (Address)((ulong)imageBase + (uint)imageSize); } }

    /// <summary>
    /// The name of the symbol file (PDB file) associated with the DLL
    /// </summary>
    public string PdbName { get { return pdbName; } }
    /// <summary>
    /// Returns the GUID that uniquely identifies the symbol file (PDB file) for this DLL
    /// </summary>
    public Guid PdbSignature { get { return pdbSignature; } }
    /// <summary>
    /// Returns the age (which is a small integer), that is also needed to look up the symbol file (PDB file) on a symbol server.
    /// </summary>
    public int PdbAge { get { return pdbAge; } }

    /// <summary>
    /// Returns the file version string that is optionally embedded in the DLL's resources.   Returns the empty string if not present.
    /// </summary>
    public string FileVersion { get { return fileVersion; } }

    /// <summary>
    /// Returns the product name  recorded in the file version information.     Returns empty string if not present
    /// </summary>
    public string ProductName { get { return productName; } }

    /// <summary>
    /// Returns a version string for the product as a whole (could include GIT source code hash).    Returns empty string if not present
    /// </summary>
    public string ProductVersion { get { return productVersion; } }

    /// <summary>
    /// This is the checksum value in the PE header. Can be used to validate
    /// that the file on disk is the same as the file from the trace.
    /// </summary>
    public int ImageChecksum { get { return imageChecksum; } }

    /// <summary>
    /// This used to be called TimeDateStamp, but linkers may not use it as a
    /// timestamp anymore because they want deterministic builds.  It still is
    /// useful as a unique ID for the image.
    /// </summary>
    public int ImageId { get { return timeDateStamp; } }

    /// <summary>
    /// Tells if the module file is ReadyToRun (the has precompiled code for some managed methods)
    /// </summary>
    public bool IsReadyToRun { get { return isReadyToRun; } }

    /// <summary>
    /// If the Product Version fields has a GIT Commit Hash component, this returns it,  Otherwise it is empty.
    /// </summary>
    public string GitCommitHash
    {
        get
        {
            // First see if the commit hash is on the file version
            if (!string.IsNullOrEmpty(fileVersion))
            {
                Match m = Regex.Match(fileVersion, @"Commit Hash:\s*(\S+)", RegexOptions.CultureInvariant);
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
            // or the product version.
            if (!string.IsNullOrEmpty(productVersion))
            {
                Match m = Regex.Match(productVersion, @"Commit Hash:\s*(\S+)", RegexOptions.CultureInvariant);
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
            return "";
        }
    }

    // /// <summary>
    // /// Returns the time the DLL was built as a DateTime.   Note that this may not
    // /// work if the build system uses deterministic builds (in which case timestamps
    // /// are not allowed.   We may not be able to tell if this is a bad timestamp
    // /// but we include it because when it is timestamp it is useful.
    // /// </summary>
    // public DateTime BuildTime
    // {
    //     get
    //     {
    //         var ret = PEFile.PEHeader.TimeDateStampToDate(timeDateStamp);
    //         if (ret > DateTime.Now)
    //         {
    //             ret = DateTime.MinValue;
    //         }

    //         return ret;
    //     }
    // }

    /// <summary>
    /// The number of code addresses included in this module.  This is useful for determining if
    /// this module is worth having its symbolic information looked up or not.   It is not
    /// otherwise a particularly interesting metric.
    /// <para>
    /// This number is defined as the number of appearances this module has in any stack
    /// or any event with a code address (If the modules appears 5 times in a stack that
    /// counts as 5 even though it is just one event's stack).
    /// </para>
    /// </summary>
    public int CodeAddressesInModule { get { return codeAddressesInModule; } }
    /// <summary>
    /// If the module file was a managed native image, this is the IL file associated with it.
    /// </summary>
    public TraceModuleFile? ManagedModule { get { return managedModule; } }

    #region Private
    internal TraceModuleFile(string fileName, Address imageBase, ModuleFileIndex moduleFileIndex)
    {
        if (fileName != null)
        {
            this.fileName = fileName.ToLowerInvariant();        // Normalize to lower case.
        }

        this.imageBase = imageBase;
        this.moduleFileIndex = moduleFileIndex;
        fileVersion = "";
        productVersion = "";
        productName = "";
        pdbName = "";
    }

    internal string? fileName;
    internal int imageSize;
    internal Address imageBase;
    internal string? name;
    private ModuleFileIndex moduleFileIndex;
    internal bool isReadyToRun;
    internal TraceModuleFile? next;          // Chain of modules that have the same path (But different image bases)

    internal string pdbName;
    internal Guid pdbSignature;
    internal int pdbAge;
    internal string fileVersion;
    internal string productName;
    internal string productVersion;
    internal int timeDateStamp;
    internal int imageChecksum;                  // used to validate if the local file is the same as the one from the trace.
    internal int codeAddressesInModule;
    internal TraceModuleFile? managedModule;

    #endregion
}
