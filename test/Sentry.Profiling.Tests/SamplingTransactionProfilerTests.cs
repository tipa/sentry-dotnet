using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Sentry.Internal.Http;

namespace Sentry.Profiling.Tests;

// Note: we must not run tests in parallel because we only support profiling one transaction at a time.
// That means setting up a test-collection with parallelization disabled and NOT using any async test functions.
[CollectionDefinition("SamplingProfiler tests", DisableParallelization = true)]
public class SamplingTransactionProfilerTests
{
    private readonly IDiagnosticLogger _testOutputLogger;

    public SamplingTransactionProfilerTests(ITestOutputHelper output)
    {
        _testOutputLogger = new TestOutputDiagnosticLogger(output);
    }

    private void ValidateProfile(SampleProfile profile, ulong maxTimestampNs)
    {
        profile.Samples.Should().NotBeEmpty();
        profile.Frames.Should().NotBeEmpty();
        profile.Stacks.Should().NotBeEmpty();

        // Verify that downsampling works.
        var previousSamplesByThread = new Dictionary<int, SampleProfile.Sample>();

        foreach (var sample in profile.Samples)
        {
            sample.Timestamp.Should().BeInRange(0, maxTimestampNs);
            sample.StackId.Should().BeInRange(0, profile.Stacks.Count);
            sample.ThreadId.Should().BeInRange(0, profile.Threads.Count);

            if (previousSamplesByThread.TryGetValue(sample.ThreadId, out var prevSample))
            {
                sample.Timestamp.Should().BeGreaterThan(prevSample.Timestamp + 8_000_000,
                    "Downsampling: there must be at least 9ms between samples on the same thread.");
            }
            previousSamplesByThread[sample.ThreadId] = sample;
        }

        foreach (var thread in profile.Threads)
        {
            thread.Name.Should().NotBeNullOrEmpty();
        }

        // We can't check that all Frame names are filled because there may be native frames which we currently don't filter out.
        // Let's just check there are some frames with names...
        profile.Frames.Where((frame) => frame.Function is not null).Should().NotBeEmpty();
    }

    private void RunForMs(int milliseconds)
    {
        for (int i = 0; i < milliseconds / 20; i++)
        {
            _testOutputLogger.LogDebug("sleeping...");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Profiler_StartedNormally_Works()
    {
        var hub = Substitute.For<IHub>();
        var transactionTracer = new TransactionTracer(hub, "test", "");

        var factory = new SamplingTransactionProfilerFactory(Path.GetTempPath(), new SentryOptions { DiagnosticLogger = _testOutputLogger });
        var clock = SentryStopwatch.StartNew();
        var sut = factory.Start(transactionTracer, CancellationToken.None);
        transactionTracer.TransactionProfiler = sut;
        RunForMs(100);
        sut.Finish();
        var elapsedNanoseconds = (ulong)((clock.CurrentDateTimeOffset - clock.StartDateTimeOffset).TotalMilliseconds * 1_000_000);

        var transaction = new Transaction(transactionTracer);
        var collectTask = sut.CollectAsync(transaction);
        collectTask.Wait();
        var profileInfo = collectTask.Result;
        Assert.NotNull(profileInfo);
        ValidateProfile(profileInfo.Profile, elapsedNanoseconds);
    }

    [Fact]
    public void Profiler_AfterTimeout_Stops()
    {
        var hub = Substitute.For<IHub>();
        var options = new SentryOptions { DiagnosticLogger = _testOutputLogger };
        var sut = new SamplingTransactionProfiler(Path.GetTempPath(), options, CancellationToken.None);
        var limitMs = 50;
        sut.Start(limitMs);
        RunForMs(limitMs * 4);
        sut.Finish();

        var collectTask = sut.CollectAsync(new Transaction("foo", "bar"));
        collectTask.Wait();
        var profileInfo = collectTask.Result;

        Assert.NotNull(profileInfo);
        ValidateProfile(profileInfo.Profile, (ulong)(limitMs * 1_000_000));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProfilerIntegration_FullRoundtrip_Works(bool offlineCaching)
    {
        var tcs = new TaskCompletionSource<string>();
        async Task VerifyAsync(HttpRequestMessage message)
        {
            var payload = await message.Content!.ReadAsStringAsync();
            // We're actually looking for type:profile but it must be sent in the same envelope as the transaction.
            if (payload.Contains("\"type\":\"transaction\""))
            {
                tcs.TrySetResult(payload);
            }
        }

        var cts = new CancellationTokenSource();
        cts.Token.Register(() => tcs.TrySetCanceled());

        // envelope cache dir
        var fileSystem = new FakeFileSystem();
        using var cacheDirectory = offlineCaching ? new TempDirectory(fileSystem) : null;

        // profiler temp dir (doesn't support `FileSystem`)
        var tempDir = new TempDirectory();

        var options = new SentryOptions
        {
            Dsn = ValidDsn,
            // To go through a round trip serialization of cached envelope
            CacheDirectoryPath = cacheDirectory?.Path,
            FileSystem = fileSystem,
            // So we don't need to deal with gzip'ed payload
            RequestBodyCompressionLevel = CompressionLevel.NoCompression,
            CreateHttpClientHandler = () => new CallbackHttpClientHandler(VerifyAsync),
            // Not to send some session envelope
            AutoSessionTracking = false,
            Debug = true,
            DiagnosticLogger = _testOutputLogger,
            TracesSampleRate = 1.0,
        };

        // Disable process exit flush to resolve "There is no currently active test." errors.
        options.DisableAppDomainProcessExitFlush();

        options.AddIntegration(new ProfilingIntegration(tempDir.Path));

        try
        {
            using var hub = new Hub(options);

            var clock = SentryStopwatch.StartNew();
            var tx = hub.StartTransaction("name", "op");
            RunForMs(100);
            tx.Finish();
            var elapsedNanoseconds = (ulong)((clock.CurrentDateTimeOffset - clock.StartDateTimeOffset).TotalMilliseconds * 1_000_000);

            hub.FlushAsync().Wait();

            // Synchronizing in the tests to go through the caching and http transports
            cts.CancelAfter(options.FlushTimeout + TimeSpan.FromSeconds(1));
            var ex = Record.Exception(() => tcs.Task.Wait());
            Assert.Null(ex);
            Assert.True(tcs.Task.IsCompleted);

            var envelopeLines = tcs.Task.Result.Split('\n');
            envelopeLines.Length.Should().Be(6);

            // header rows before payloads
            envelopeLines[1].Should().StartWith("{\"type\":\"transaction\"");
            envelopeLines[3].Should().StartWith("{\"type\":\"profile\"");

            var transaction = Json.Parse(envelopeLines[2], Transaction.FromJson);

            // TODO do we want to bother with JSON parsing just to do this? Doing at least simple checks for now...
            // var profileInfo = Json.Parse(envelopeLines[4], ProfileInfo.FromJson);
            // ValidateProfile(profileInfo.Profile, elapsedNanoseconds);
            envelopeLines[4].Should().Contain("\"profile\":{");
            envelopeLines[4].Should().Contain($"\"id\":\"{transaction.EventId}\"");
            envelopeLines[4].Length.Should().BeGreaterThan(10000);

            Directory.GetFiles(tempDir.Path).Should().BeEmpty("When profiling is done, the temp dir should be empty.");
        }
        finally
        {
            // ensure the task is complete before leaving the test
            tcs.TrySetResult("");
            tcs.Task.Wait();

            if (options.Transport is CachingTransport cachingTransport)
            {
                // Disposing the caching transport will ensure its worker
                // is shut down before we try to dispose and delete the temp folder
                cachingTransport.Dispose();
            }
        }
    }

    [Fact]
    public void ContinousProfiling()
    {
        EventPipeProvider[] providers = new[]
        {
            // Note: all events we need issued by "DotNETRuntime" provider are at "EventLevel.Informational"
            // see https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/r  untime-events
            new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
            new EventPipeProvider(SampleProfilerTraceEventParser.ProviderName, EventLevel.Informational),
            // new EventPipeProvider("System.Threading.Tasks.TplEventSource", EventLevel.Informational, (long)TplEtwProviderTraceEventParser.Keywords.Default)
        };
        var client = new DiagnosticsClient(Process.GetCurrentProcess().Id);
        using (var session = client.StartEventPipeSession(providers, true))
        {

            Task streamTask = Task.Run(() =>
            {
                var source = new EventPipeEventSource(session.EventStream);
                var sampleEventParser = new SampleProfilerTraceEventParser(source);
                sampleEventParser.ThreadSample += (ClrThreadSampleTraceData obj) =>
                {
                    // Console.WriteLine($"{obj.TimeStampRelativeMSec} {obj.EventName} - thread {obj.ThreadID}");
                    // TODO see ProcessExtendedData()
                };

                source.Clr.ClrStackWalk += (ClrStackWalkTraceData data) =>
                {
                    Console.WriteLine($"ClrStackWalk {data.TimeStampRelativeMSec} {data.EventName}");
                };

                source.Clr.LoaderModuleLoad += delegate (ModuleLoadUnloadTraceData data)
                {
                    Console.WriteLine($"LoaderModuleLoad {data.TimeStampRelativeMSec} {data.EventName}");
                    // processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC).LoadedModules.ManagedModuleLoadOrUnload(data, true, false);
                };
                source.Clr.LoaderModuleUnload += delegate (ModuleLoadUnloadTraceData data)
                {
                    Console.WriteLine($"LoaderModuleUnload {data.TimeStampRelativeMSec} {data.EventName}");
                    // processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC).LoadedModules.ManagedModuleLoadOrUnload(data, false, false);
                };
                source.Clr.LoaderModuleDCStopV2 += delegate (ModuleLoadUnloadTraceData data)
                {
                    Console.WriteLine($"LoaderModuleDCStopV2 {data.TimeStampRelativeMSec} {data.EventName}");
                    // processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC).LoadedModules.ManagedModuleLoadOrUnload(data, false, true);
                };

                var ClrRundownParser = new ClrRundownTraceEventParser(source);
                Action<ModuleLoadUnloadTraceData> onLoaderRundown = delegate (ModuleLoadUnloadTraceData data)
                {
                    Console.WriteLine($"onLoaderRundown {data.TimeStampRelativeMSec} {data.EventName}");
                    // processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC).LoadedModules.ManagedModuleLoadOrUnload(data, false, true);
                };

                ClrRundownParser.LoaderModuleDCStop += onLoaderRundown;
                ClrRundownParser.LoaderModuleDCStart += onLoaderRundown;

                Action<MethodLoadUnloadVerboseTraceData> onMethodStart = delegate (MethodLoadUnloadVerboseTraceData data)
                    {
                        Console.WriteLine($"onMethodStart {data.TimeStampRelativeMSec} {data.EventName}");
                        // // We only capture data on unload, because we collect the addresses first.
                        // if (!data.IsDynamic && !data.IsJitted)
                        // {
                        //     bookKeepingEvent = true;
                        // }

                        // if ((int)data.ID == 139)       // MethodDCStartVerboseV2
                        // {
                        //     bookKeepingEvent = true;
                        // }

                        // if (data.IsJitted)
                        // {
                        //     TraceProcess process = processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
                        //     process.InsertJITTEDMethod(data.MethodStartAddress, data.MethodSize, delegate ()
                        //     {
                        //         TraceManagedModule module = process.LoadedModules.GetOrCreateManagedModule(data.ModuleID, data.TimeStampQPC);
                        //         MethodIndex methodIndex = CodeAddresses.Methods.NewMethod(TraceLog.GetFullName(data), module.ModuleFile.ModuleFileIndex, data.MethodToken);
                        //         return new TraceProcess.MethodLookupInfo(data.MethodStartAddress, data.MethodSize, methodIndex);
                        //     });

                        //     jittedMethods.Add((MethodLoadUnloadVerboseTraceData)data.Clone());
                        // }
                    };
                source.Clr.MethodLoadVerbose += onMethodStart;
                source.Clr.MethodDCStartVerboseV2 += onMethodStart;
                ClrRundownParser.MethodDCStartVerbose += onMethodStart;

                source.Clr.MethodUnloadVerbose += delegate (MethodLoadUnloadVerboseTraceData data)
                {
                    Console.WriteLine($"MethodUnloadVerbose {data.TimeStampRelativeMSec} {data.EventName}");
                    // codeAddresses.AddMethod(data);
                    // if (!data.IsJitted)
                    // {
                    //     bookKeepingEvent = true;
                    // }
                };
                source.Clr.MethodILToNativeMap += delegate (MethodILToNativeMapTraceData data)
                {
                    Console.WriteLine($"MethodILToNativeMap {data.TimeStampRelativeMSec} {data.EventName}");
                    // codeAddresses.AddILMapping(data);
                    // bookKeepingEvent = true;
                };

                ClrRundownParser.MethodILToNativeMapDCStop += delegate (MethodILToNativeMapTraceData data)
                {
                    Console.WriteLine($"MethodILToNativeMapDCStop {data.TimeStampRelativeMSec} {data.EventName}");
                    // codeAddresses.AddILMapping(data);
                    // bookKeepingEvent = true;
                };

                try
                {
                    source.Process();
                }
                // NOTE: This exception does not currently exist. It is something that needs to be added to TraceEvent.
                catch (Exception e)
                {
                    Console.WriteLine("Error encountered while processing events");
                    Console.WriteLine(e.ToString());
                }
            });

            RunForMs(100);
            session.Stop();
            streamTask.Wait();
        }
    }

    // /// <summary>
    // /// Process any extended data (like Win7 style stack traces) associated with 'data'
    // /// returns true if the event should be considered a bookkeeping event.
    // /// </summary>
    // internal bool ProcessExtendedData(TraceEvent data, ushort extendedDataCount)
    // {
    //     var isBookkeepingEvent = false;
    //     var extendedData = data.eventRecord->ExtendedData;
    //     Debug.Assert(extendedData != null && extendedDataCount != 0);
    //     Guid* relatedActivityIDPtr = null;
    //     string containerID = null;
    //     for (int i = 0; i < extendedDataCount; i++)
    //     {
    //         if (extendedData[i].ExtType == TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_STACK_TRACE64 ||
    //             extendedData[i].ExtType == TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_STACK_TRACE32)
    //         {
    //             int pointerSize = (extendedData[i].ExtType == TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_STACK_TRACE64) ? 8 : 4;
    //             var stackRecord = (TraceEventNativeMethods.EVENT_EXTENDED_ITEM_STACK_TRACE64*)extendedData[i].DataPtr;
    //             // TODO Debug.Assert(stackRecord->MatchId == 0);
    //             ulong* addresses = &stackRecord->Address[0];
    //             int addressesCount = (extendedData[i].DataSize - sizeof(ulong)) / pointerSize;

    //             TraceProcess process = processes.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
    //             TraceThread thread = Threads.GetOrCreateThread(data.ThreadIDforStacks(), data.TimeStampQPC, process);
    //             EventIndex eventIndex = (EventIndex)eventCount;

    //             ulong sampleAddress;
    //             byte* lastAddressPtr = (((byte*)addresses) + (extendedData[i].DataSize - sizeof(ulong) - pointerSize));
    //             if (pointerSize == 4)
    //             {
    //                 sampleAddress = *((uint*)lastAddressPtr);
    //             }
    //             else
    //             {
    //                 sampleAddress = *((ulong*)lastAddressPtr);
    //             }

    //             // Note that I use the pointer size for the log, not the event, since the kernel events
    //             // might differ in pointer size from the user mode event.
    //             if (PointerSize == 4)
    //             {
    //                 sampleAddress &= 0xFFFFFFFF00000000;
    //             }

    //             if (process.IsKernelAddress(sampleAddress, PointerSize) && data.ProcessID != 0 && data.ProcessID != 4)
    //             {
    //                 // If this is a kernel event, we have to defer making the stack (it is incomplete).
    //                 // Make a new IncompleteStack to track that (unlike other stack events we don't need to go looking for it.
    //                 IncompleteStack stackInfo = AllocateIncompleteStack(eventIndex, thread, EventIndex.Invalid);    // Blocking stack can be invalid because CSWitches don't use this path.
    //                 Debug.Assert(!(data is CSwitchTraceData));        // CSwtiches don't use this form of call stacks.  When they do set setackInfo.IsCSwitch.

    //                 // Remember the kernel frames
    //                 if (!stackInfo.LogKernelStackFragment(addresses, addressesCount, pointerSize, data.TimeStampQPC, this))
    //                 {
    //                     stackInfo.AddEntryToThread(ref thread.lastEntryIntoKernel);     // If not done remember to complete it
    //                 }

    //                 if (countForEvent != null)
    //                 {
    //                     countForEvent.m_stackCount++;   // Update stack counts
    //                 }
    //             }
    //             else
    //             {
    //                 CallStackIndex callStackIndex = callStacks.GetStackIndexForStackEvent(
    //                     addresses, addressesCount, pointerSize, thread);
    //                 Debug.Assert(callStacks.Depth(callStackIndex) == addressesCount);

    //                 // Is this the special ETW_TASK_STACK_TRACE/ETW_OPCODE_USER_MODE_STACK_TRACE which is just
    //                 // there to attach to a kernel event if so attach it to all IncompleteStacks on this thread.
    //                 if (data.ID == (TraceEventID)18 && data.Opcode == (TraceEventOpcode)24 &&
    //                     data.ProviderGuid == KernelTraceEventParser.EventTracingProviderGuid)
    //                 {
    //                     isBookkeepingEvent = true;
    //                     EmitStackOnExitFromKernel(ref thread.lastEntryIntoKernel, callStackIndex, null);
    //                     thread.lastEmitStackOnExitFromKernelQPC = data.TimeStampQPC;
    //                 }
    //                 else
    //                 {
    //                     // If this is not the special user mode stack event that fires on exit from the kernel
    //                     // we don't need any IncompleteStack structures, we can just attach the stack to the
    //                     // current event and be done.

    //                     // Note that we don't interfere with the splicing of kernel and user mode stacks because we do
    //                     // see user mode stacks delayed and have a new style user mode stack spliced in.
    //                     AddStackToEvent(eventIndex, callStackIndex);
    //                     if (countForEvent != null)
    //                     {
    //                         countForEvent.m_stackCount++;   // Update stack counts
    //                     }
    //                 }
    //             }
    //         }
    //         else if (extendedData[i].ExtType == TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_RELATED_ACTIVITYID)
    //         {
    //             relatedActivityIDPtr = (Guid*)(extendedData[i].DataPtr);
    //         }
    //         else if (extendedData[i].ExtType == TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_CONTAINER_ID)
    //         {
    //             containerID = Marshal.PtrToStringAnsi((IntPtr)extendedData[i].DataPtr, (int)extendedData[i].DataSize);
    //         }
    //     }

    //     if (relatedActivityIDPtr != null)
    //     {
    //         if (relatedActivityIDs.Count == 0)
    //         {
    //             // Insert a synthetic value since 0 represents "no related activity ID".
    //             relatedActivityIDs.Add(Guid.Empty);
    //         }

    //         // TODO This is a bit of a hack.   We wack this field in place.
    //         // We encode this as index into the relatedActivityID GrowableArray.
    //         if (IntPtr.Size == 8)
    //         {
    //             data.eventRecord->ExtendedData = (TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM*)(relatedActivityIDs.Count << 4);
    //         }
    //         else
    //         {
    //             data.eventRecord->ExtendedData = (TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM*)relatedActivityIDs.Count;
    //         }
    //         relatedActivityIDs.Add(*relatedActivityIDPtr);
    //     }
    //     else
    //     {
    //         data.eventRecord->ExtendedData = null;
    //     }

    //     if (containerID != null)
    //     {
    //         // TODO This is a bit of a hack.   We wack this field in place.
    //         // We encode this as index into the containerIDs GrowableArray.
    //         if (containerIDs.Count == 0)
    //         {
    //             // Insert a synthetic value since 0 represents "no container ID".
    //             containerIDs.Add(null);
    //         }

    //         // Look for the container ID.
    //         bool found = false;
    //         for (int i = 0; i < containerIDs.Count; i++)
    //         {
    //             if (containerIDs[i] == containerID)
    //             {
    //                 data.eventRecord->ExtendedDataCount = (ushort)i;
    //                 found = true;
    //                 break;
    //             }
    //         }

    //         if (!found)
    //         {
    //             data.eventRecord->ExtendedDataCount = (ushort)containerIDs.Count;
    //             containerIDs.Add(containerID);
    //         }
    //     }
    //     else
    //     {
    //         data.eventRecord->ExtendedDataCount = 0;
    //     }

    //     return isBookkeepingEvent;
    // }
}
