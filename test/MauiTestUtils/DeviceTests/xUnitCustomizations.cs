#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.Maui
{
    /// <summary>
    /// Custom trait discoverer which adds a Category trait for filtering, etc.
    /// </summary>
    public class CategoryDiscoverer : ITraitDiscoverer
    {
        public const string Category = "Category";

        public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
        {
            var args = traitAttribute.GetConstructorArguments().FirstOrDefault();

            if (args is string[] categories)
            {
                foreach (var category in categories)
                {
                    yield return new KeyValuePair<string, string>(Category, category.ToString());
                }
            }
        }
    }

    /// <summary>
    /// Custom fact discoverer which wraps the resulting test cases to provide custom names
    /// </summary>
    public class FactDiscoverer : Xunit.Sdk.FactDiscoverer
    {
        public FactDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
        {
        }

        public override IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
            ITestMethod testMethod, IAttributeInfo factAttribute)
        {
            var cases = base.Discover(discoveryOptions, testMethod, factAttribute);

            foreach (var testCase in cases)
            {
                yield return new DeviceTestCase(testCase);
            }
        }
    }

    /// <summary>
    /// Custom theory discoverer which wraps the resulting test cases to provide custom names
    /// </summary>
    public class TheoryDiscoverer : Xunit.Sdk.TheoryDiscoverer
    {
        public TheoryDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
        {
        }

        public override IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions,
            ITestMethod testMethod, IAttributeInfo factAttribute)
        {
            var testCases = base.Discover(discoveryOptions, testMethod, factAttribute);

            foreach (var testCase in testCases)
            {
                yield return new DeviceTestCase(testCase);
            }
        }
    }

    /// <summary>
    /// Conveninence attribute for setting a Category trait on a test or test class
    /// </summary>
    [TraitDiscoverer("Microsoft.Maui.CategoryDiscoverer", "Microsoft.Maui.TestUtils.DeviceTests")]
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
    public class CategoryAttribute : Attribute, ITraitAttribute
    {
        // Yes, it looks like the cateory parameter is not used. Don't worry, CategoryDiscoverer uses it. 
        public CategoryAttribute(params string[] categories) { }
    }

    /// <summary>
    /// Custom Fact attribute which defaults to using the test method name for the DisplayName property
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [XunitTestCaseDiscoverer("Microsoft.Maui.FactDiscoverer", "Microsoft.Maui.TestUtils.DeviceTests")]
    public class FactAttribute : Xunit.FactAttribute
    {
        public FactAttribute([CallerMemberName] string displayName = "")
        {
            base.DisplayName = displayName;
        }
    }

    /// <summary>
    /// Custom Theory attribute which defaults to using the test method name for the DisplayName property
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [XunitTestCaseDiscoverer("Microsoft.Maui.TheoryDiscoverer", "Microsoft.Maui.TestUtils.DeviceTests")]
    public class TheoryAttribute : Xunit.TheoryAttribute
    {
        public TheoryAttribute([CallerMemberName] string displayName = "")
        {
            base.DisplayName = displayName;
        }
    }

#pragma warning disable xUnit3000 // Test case classes must derive directly or indirectly from Xunit.LongLivedMarshalByRefObject
    // If we try to actually subclass Xunit.LongLivedMarshalByRefObject here, we get a version conflict between the XUnit and 
    // the runner. LLMBRO is only a concern if we're worried about app domains, and for the moment we aren't. If we have to 
    // worry about that later, we can work out the conflict issues.
    public class DeviceTestCase : IXunitTestCase
#pragma warning restore xUnit3000 // Test case classes must derive directly or indirectly from Xunit.LongLivedMarshalByRefObject

    /* Unmerged change from project 'TestUtils.DeviceTests(net8.0-ios)'
    Before:
            readonly IXunitTestCase _inner = null!;
    After:
            private readonly IXunitTestCase _inner = null!;
    */

    /* Unmerged change from project 'TestUtils.DeviceTests(net8.0-maccatalyst)'
    Before:
            readonly IXunitTestCase _inner = null!;
    After:
            private readonly IXunitTestCase _inner = null!;
    */
    {
        private readonly IXunitTestCase _inner = null!;

        public DeviceTestCase() { }

        public DeviceTestCase(IXunitTestCase inner)
        {
            _inner = inner;
        }

        public Exception InitializationException => _inner.InitializationException;

        public IMethodInfo Method => _inner.Method;

        public int Timeout => _inner.Timeout;

        private string? _categoryPrefix;

        private string GetCategoryPrefix()
        {
            if (_categoryPrefix == null)
            {
                if (!Traits.ContainsKey(CategoryDiscoverer.Category))
                {
                    _categoryPrefix = string.Empty;
                }
                else
                {
                    _categoryPrefix = $"[{string.Join(", ", Traits[CategoryDiscoverer.Category])}] ";
                }
            }

            return _categoryPrefix;

            /* Unmerged change from project 'TestUtils.DeviceTests(net8.0-ios)'
            Before:
                    string? _displayName;
            After:
                    private string? _displayName;
            */

            /* Unmerged change from project 'TestUtils.DeviceTests(net8.0-maccatalyst)'
            Before:
                    string? _displayName;
            After:
                    private string? _displayName;
            */
        }

        private string? _displayName;

        public string DisplayName
        {
            get
            {
                _displayName = _displayName ?? $"{GetCategoryPrefix()}{_inner.DisplayName}";
                return _displayName;
            }
        }

        public string SkipReason => _inner.SkipReason;

        public ISourceInformation SourceInformation { get => _inner.SourceInformation; set => _inner.SourceInformation = value; }

        public ITestMethod TestMethod => _inner.TestMethod;

        public object[] TestMethodArguments => _inner.TestMethodArguments;

        public Dictionary<string, List<string>> Traits => _inner.Traits;

        public string UniqueID => _inner.UniqueID;

        public void Deserialize(IXunitSerializationInfo info)
        {
            _inner.Deserialize(info);
        }

        public Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        {
            return _inner.RunAsync(diagnosticMessageSink, messageBus, constructorArguments, aggregator, cancellationTokenSource);
        }

        public void Serialize(IXunitSerializationInfo info)
        {
            _inner.Serialize(info);
        }
    }
}
