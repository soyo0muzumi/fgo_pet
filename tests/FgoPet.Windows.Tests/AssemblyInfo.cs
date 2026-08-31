using Xunit;

// WPF pack/BAML caches and dispatcher state are process-wide across STA fixtures;
// serialize this test assembly to avoid false failures from concurrent XAML loads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
