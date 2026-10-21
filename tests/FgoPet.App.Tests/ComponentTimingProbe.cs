using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Tests;

// Opt-in test diagnostics. Keep original factories, returned objects, lifetimes and disposal order.
internal sealed class ComponentTimingProbe : IDisposable
{
    private static readonly ConcurrentQueue<Entry> Events = new();
    private static readonly ConcurrentQueue<object> Costs = new();
    private readonly long _action = StaTest.ActiveDiagnosticCorrelation;
    private readonly ThreadLocal<Stack<long>> _stack = new(() => new());
    private readonly Dictionary<object, int> _objects = new(ReferenceEqualityComparer.Instance);
    private static long _nextSpan;
    private volatile bool _closed;
    private ActivityListener? _operations;
    private long _recordTicks, _recordCalls, _callbackTicks, _callbackCalls, _preparationTicks, _disposalPreparationTicks;

    private ComponentTimingProbe()
    {
        if (Environment.GetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE") != "operations") return;
        _operations = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("FgoPet.TestTiming.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                !_closed && StaTest.ActiveDiagnosticCorrelation == _action ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                var tick = Stopwatch.GetTimestamp();
                activity.SetCustomProperty("FgoPet.Timing.Span", Begin("operation", activity.OperationName, -1));
                _callbackTicks += Stopwatch.GetTimestamp() - tick;
                _callbackCalls++;
            },
            ActivityStopped = activity =>
            {
                var tick = Stopwatch.GetTimestamp();
                if (activity.GetCustomProperty("FgoPet.Timing.Span") is Span span) End(span, "scope_exited", null);
                _callbackTicks += Stopwatch.GetTimestamp() - tick;
                _callbackCalls++;
            },
        };
        ActivitySource.AddActivityListener(_operations);
    }

    internal static ComponentTimingProbe? TryStart(IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS"))) return null;
        if (Environment.GetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE") == "coarse") return null;
        var tick = Stopwatch.GetTimestamp();
        var probe = new ComponentTimingProbe();
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            var slot = index;
            if (descriptor.IsKeyedService && descriptor.KeyedImplementationFactory is { } keyed)
                services[index] = new ServiceDescriptor(descriptor.ServiceType, descriptor.ServiceKey,
                    (provider, key) => probe.Factory(descriptor.ServiceType, slot, () => keyed(provider, key)), descriptor.Lifetime);
            else if (!descriptor.IsKeyedService && descriptor.ImplementationFactory is { } factory)
                services[index] = ServiceDescriptor.Describe(descriptor.ServiceType,
                    provider => probe.Factory(descriptor.ServiceType, slot, () => factory(provider)), descriptor.Lifetime);
            // Leave implementation types/instances unchanged, including ValidateOnBuild's inspection.
        }
        probe._preparationTicks = Stopwatch.GetTimestamp() - tick;
        return probe;
    }

    private object Factory(Type serviceType, int slot, Func<object> original)
    {
        if (_closed || StaTest.ActiveDiagnosticCorrelation != _action) return original();
        var span = Begin("factory", serviceType, slot);
        object? value = null;
        string outcome = "returned";
        try { return value = original(); }
        catch (Exception error) { outcome = error.GetType().Name; throw; }
        finally { End(span, outcome, value); }
    }

    internal void PrepareDisposal(ServiceProvider provider)
    {
        var tick = Stopwatch.GetTimestamp();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var root = typeof(ServiceProvider).GetProperty("Root", flags)?.GetValue(provider)
            ?? throw new NotSupportedException("DI_ROOT_DIAGNOSTIC_UNAVAILABLE");
        var sync = root.GetType().GetProperty("Sync", flags)?.GetValue(root)
            ?? throw new NotSupportedException("DI_SYNC_DIAGNOSTIC_UNAVAILABLE");
        var field = root.GetType().GetField("_disposables", flags)
            ?? throw new NotSupportedException("DI_DISPOSABLES_DIAGNOSTIC_UNAVAILABLE");
        lock (sync)
        {
            if (root.GetType().GetField("_disposed", flags)?.GetValue(root) is not false)
                throw new InvalidOperationException("DI_ROOT_NOT_LIVE_FOR_DIAGNOSTIC");
            if (field.GetValue(root) is not List<object> captured) return;
            var before = captured.ToArray();
            for (var index = 0; index < before.Length; index++)
            {
                var item = before[index];
                if (item is TimedDisposable) throw new InvalidOperationException("DI_DISPOSAL_ALREADY_INSTRUMENTED");
                Inventory(item, index, item is IDisposable ? "sync_wrapped" : "original_async_only_preserved");
                if (item is IDisposable disposable)
                    captured[index] = new TimedDisposable(this, disposable, index);
            }
            if (captured.Count != before.Length) throw new InvalidOperationException("DI_DISPOSAL_COUNT_CHANGED");
            for (var index = 0; index < before.Length; index++)
            {
                var actual = captured[index] is TimedDisposable timed ? timed.Original : captured[index];
                if (!ReferenceEquals(actual, before[index])) throw new InvalidOperationException("DI_DISPOSAL_ORDER_CHANGED");
            }
        }
        _disposalPreparationTicks += Stopwatch.GetTimestamp() - tick;
    }

    private sealed class TimedDisposable(ComponentTimingProbe owner, IDisposable original, int slot) : IDisposable
    {
        internal IDisposable Original { get; } = original;
        public void Dispose()
        {
            var span = owner.Begin("dispose", Original.GetType(), slot, Original);
            var outcome = "returned";
            try { Original.Dispose(); }
            catch (Exception error) { outcome = error.GetType().Name; throw; }
            finally { owner.End(span, outcome, Original); }
        }
    }

    private Span Begin(string kind, Type type, int slot, object? instance = null)
        => Begin(kind, type.FullName ?? type.Name, slot, instance);

    private Span Begin(string kind, string component, int slot, object? instance = null)
    {
        var stack = _stack.Value!;
        var parent = stack.TryPeek(out var current) ? current : 0;
        var span = new Span(Interlocked.Increment(ref _nextSpan), parent, kind, component, slot,
            Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId);
        stack.Push(span.Id);
        Record(span, "started", "observed", instance);
        return span;
    }

    private void End(Span span, string outcome, object? instance)
    {
        Record(span, "completed", outcome, instance);
        if (_stack.Value!.Pop() != span.Id) throw new InvalidOperationException("DI_DIAGNOSTIC_SPAN_ORDER");
    }

    private int ObjectId(object? value)
    {
        if (value is null) return 0;
        lock (_objects)
        {
            if (!_objects.TryGetValue(value, out var id)) _objects.Add(value, id = _objects.Count + 1);
            return id;
        }
    }

    private void Record(Span span, string phase, string outcome, object? instance)
    {
        var tick = Stopwatch.GetTimestamp();
        Events.Enqueue(new(DateTimeOffset.UtcNow, tick, Stopwatch.Frequency, Environment.CurrentManagedThreadId,
            _action, span.Id, span.Parent, span.Kind, phase, span.Component, span.Slot, ObjectId(instance),
            instance?.GetType().FullName, outcome, ThreadCpu100ns()));
        _recordTicks += Stopwatch.GetTimestamp() - tick;
        _recordCalls++;
    }

    private void Inventory(object value, int slot, string outcome) => Events.Enqueue(new(
        DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency, Environment.CurrentManagedThreadId,
        _action, 0, 0, "inventory", "observed", value.GetType().FullName ?? value.GetType().Name,
        slot, ObjectId(value), value.GetType().FullName, outcome));

    public void Dispose()
    {
        _closed = true;
        var tick = Stopwatch.GetTimestamp();
        _operations?.Dispose();
        Costs.Enqueue(new
        {
            action = _action, frequency = Stopwatch.Frequency, recordTicks = _recordTicks, recordCalls = _recordCalls,
            callbackTicks = _callbackTicks, callbackCalls = _callbackCalls, preparationTicks = _preparationTicks,
            disposalPreparationTicks = _disposalPreparationTicks, listenerDisposeTicks = Stopwatch.GetTimestamp() - tick,
            note = "record and callback intervals overlap; includes scheduling; excludes caller JIT and ActivitySource internals",
        });
    }

    internal static void FlushDiagnostics(string directory)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        if (!Events.IsEmpty)
        {
            using var writer = File.AppendText(Path.Combine(directory, "component-events.jsonl"));
            while (Events.TryDequeue(out var item)) writer.WriteLine(JsonSerializer.Serialize(item, options));
        }
        if (!Costs.IsEmpty)
        {
            using var writer = File.AppendText(Path.Combine(directory, "probe-costs.jsonl"));
            while (Costs.TryDequeue(out var item)) writer.WriteLine(JsonSerializer.Serialize(item, options));
        }
    }

    private sealed record Span(long Id, long Parent, string Kind, string Component, int Slot, long Tick, int Thread);
    private sealed record Entry(DateTimeOffset AtUtc, long Tick, long Frequency, int Thread, long Action,
        long Span, long Parent, string Kind, string Phase, string Component, int Slot, int ObjectId,
        string? ActualType, string Outcome, long? ThreadCpu100ns = null);

    // Current thread only; coarse OS CPU accounting separates long wall spans from CPU execution.
    private static long? ThreadCpu100ns() => GetThreadTimes(new IntPtr(-2), out _, out _, out var kernel, out var user)
        ? kernel + user : null;
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);

    // Separate-process calibration; not an extra xUnit case and never run before measured cases in their process.
    internal static object SelfCheck()
    {
        var checks = 0;
        void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); checks++; }
        var enabled = Environment.GetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS", null);
            var off = new ServiceCollection();
            off.AddSingleton<Fixture>(_ => new Fixture([]));
            var original = off[0];
            Check(TryStart(off) is null && ReferenceEquals(off[0], original), "DEFAULT_OFF_CHANGED");
        }
        finally { Environment.SetEnvironmentVariable("FGO_PET_STA_DIAGNOSTICS", enabled); }
        Check(!string.IsNullOrWhiteSpace(enabled), "CALIBRATION_REQUIRES_DIAGNOSTICS");
        StaTest.Run(() =>
        {
            foreach (var instrument in new[] { false, true })
            {
                var order = new List<string>();
                var shared = new Fixture(order, "shared");
                var services = new ServiceCollection();
                services.AddSingleton<IAliasA>(_ => { Busy(2); return shared; });
                services.AddSingleton<IAliasB>(_ => shared);
                services.AddTransient<Fixture>(_ => new Fixture(order, "transient"));
                services.AddScoped<Scoped>();
                services.AddKeyedSingleton<Fixture>("synthetic-key", (_, _) => new Fixture(order, "keyed"));
                services.AddSingleton(new Instance());
                var before = services.ToArray();
                using var probe = instrument ? TryStart(services) : null;
                for (var i = 0; i < services.Count; i++)
                {
                    Check(services[i].ServiceType == before[i].ServiceType && services[i].Lifetime == before[i].Lifetime &&
                        services[i].IsKeyedService == before[i].IsKeyedService &&
                        ReferenceEquals(services[i].ServiceKey, before[i].ServiceKey), "DESCRIPTOR_SHAPE_CHANGED");
                    if (!before[i].IsKeyedService && before[i].ImplementationFactory is null)
                        Check(ReferenceEquals(services[i], before[i]), "TYPE_OR_INSTANCE_REPLACED");
                }
                var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                Check(ReferenceEquals(provider.GetRequiredService<IAliasA>(), shared), "FACTORY_OBJECT_CHANGED");
                Check(ReferenceEquals(provider.GetRequiredService<IAliasB>(), shared), "ALIAS_OBJECT_CHANGED");
                _ = provider.GetRequiredService<Fixture>();
                _ = provider.GetRequiredKeyedService<Fixture>("synthetic-key");
                Check(Capture(() => provider.GetRequiredService<Scoped>()) == typeof(InvalidOperationException), "SCOPE_VALIDATION_CHANGED");
                probe?.PrepareDisposal(provider);
                provider.Dispose();
                Check(order.Count == 4 && shared.Disposals == 2, "DISPOSAL_ALIAS_COUNT_CHANGED");
                Check(order.SequenceEqual(new[] { "keyed", "transient", "shared", "shared" }), "DISPOSAL_ORDER_CHANGED");
            }
            foreach (var instrument in new[] { false, true })
            {
                var invalid = new ServiceCollection().AddSingleton<MissingDependency>();
                using var probe = instrument ? TryStart(invalid) : null;
                Check(Capture(() => invalid.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })) == typeof(AggregateException), "BUILD_VALIDATION_CHANGED");

                var failing = new ServiceCollection().AddSingleton<ThrowOnDispose>(_ => new ThrowOnDispose());
                using var failingProbe = instrument ? TryStart(failing) : null;
                var provider = failing.BuildServiceProvider();
                _ = provider.GetRequiredService<ThrowOnDispose>();
                failingProbe?.PrepareDisposal(provider);
                Check(Capture(provider.Dispose) == typeof(FixtureException), "DISPOSAL_EXCEPTION_CHANGED");

                var asynchronous = new ServiceCollection().AddSingleton<AsyncOnly>(_ => new AsyncOnly());
                using var asyncProbe = instrument ? TryStart(asynchronous) : null;
                var asyncProvider = asynchronous.BuildServiceProvider();
                _ = asyncProvider.GetRequiredService<AsyncOnly>();
                asyncProbe?.PrepareDisposal(asyncProvider);
                Check(Capture(asyncProvider.Dispose) == typeof(InvalidOperationException), "ASYNC_ONLY_POLICY_CHANGED");
            }
        });
        var benchmark = new List<object>();
        var savedMode = Environment.GetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE");
        try
        {
            StaTest.Run(() =>
            {
                using var source = new ActivitySource("FgoPet.TestTiming.Calibration");
                Environment.SetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE", "coarse");
                var untouched = new ServiceCollection().AddSingleton<Instance>();
                var originalDescriptor = untouched[0];
                Check(TryStart(untouched) is null && ReferenceEquals(originalDescriptor, untouched[0]), "COARSE_MODE_MUTATED_DI");
                Environment.SetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE", "operations");
                var previousActivity = Activity.Current;
                using (var probe = TryStart(new ServiceCollection()))
                {
                    using (var outer = source.StartActivity("calibration.outer"))
                    {
                        Check(outer is not null, "OPERATION_NOT_OBSERVED");
                        using (var inner = source.StartActivity("calibration.inner"))
                        {
                            Check(inner?.ParentSpanId == outer!.SpanId, "OPERATION_PARENT_CHANGED");
                            Busy(2);
                        }
                        Check(ReferenceEquals(Activity.Current, outer), "ACTIVITY_PARENT_NOT_RESTORED");
                        Check(Task.Run(() => source.StartActivity("calibration.foreign_thread") is null).GetAwaiter().GetResult(),
                            "FOREIGN_THREAD_INSTRUMENTED");
                    }
                    Check(ReferenceEquals(Activity.Current, previousActivity), "ACTIVITY_CONTEXT_LEAKED");
                    var expectedError = new FixtureException();
                    try { using var operation = source.StartActivity("calibration.exception"); throw expectedError; }
                    catch (FixtureException actual) { Check(ReferenceEquals(expectedError, actual), "OPERATION_EXCEPTION_CHANGED"); }
                }
                Check(source.StartActivity("calibration.after_dispose") is null, "LISTENER_NOT_REMOVED");

                void Exercise(int iterations) { for (var i = 0; i < iterations; i++) { using var operation = source.StartActivity("calibration.empty"); } }
                foreach (var mode in new[] { "no_listener_before", "operations", "no_listener_after" })
                {
                    using var probe = mode == "operations" ? TryStart(new ServiceCollection()) : null;
                    Exercise(100); // Calibration process only; never warms a measured xUnit process.
                    for (var sample = 0; sample < 5; sample++)
                    {
                        var allocated = GC.GetAllocatedBytesForCurrentThread();
                        var tick = Stopwatch.GetTimestamp();
                        Exercise(1000);
                        benchmark.Add(new { mode, sample, iterations = 1000,
                            ticks = Stopwatch.GetTimestamp() - tick, frequency = Stopwatch.Frequency,
                            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
                    }
                }
            });
        }
        finally { Environment.SetEnvironmentVariable("FGO_PET_COMPONENT_TIMING_MODE", savedMode); }
        var operationSpans = Events.Where(item => item.Kind == "operation").GroupBy(item => (item.Action, item.Span)).ToArray();
        Check(operationSpans.All(group => group.Count() == 2), "OPERATION_PAIRS_INCOMPLETE");
        var spans = Events.Where(item => item.Kind is "factory" or "dispose").GroupBy(item => (item.Action, item.Span)).ToArray();
        Check(spans.All(group => group.Count() == 2), "DIAGNOSTIC_PAIRS_INCOMPLETE");
        Check(spans.Any(group => group.First().Kind == "dispose" &&
            (group.Last().Tick - group.First().Tick) * 1000.0 / group.First().Frequency >= 2), "DISPOSAL_TIMER_NOT_OBSERVED");
        Check(spans.Any(group => group.First().Kind == "factory" &&
            (group.Last().Tick - group.First().Tick) * 1000.0 / group.First().Frequency >= 1), "FACTORY_TIMER_NOT_OBSERVED");
        return new { checks, pairedSpans = spans.Length, operationSpans = operationSpans.Length,
            benchmark, passed = true, realModelCalls = 0 };
    }

    private static Type? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error.GetType(); } }
    private static void Busy(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) Thread.SpinWait(20); }
    private interface IAliasA;
    private interface IAliasB;
    private interface IMissing;
    private sealed class Scoped;
    private sealed class Instance;
    private sealed class MissingDependency(IMissing value) { internal IMissing Value { get; } = value; }
    private sealed class Fixture(List<string> order, string label = "fixture") : IAliasA, IAliasB, IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() { Busy(3); Disposals++; order.Add(label); }
    }
    private sealed class FixtureException : Exception;
    private sealed class ThrowOnDispose : IDisposable { public void Dispose() => throw new FixtureException(); }
    private sealed class AsyncOnly : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
