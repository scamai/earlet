namespace Dictation.Services.Tests;

/// <summary>
/// The tests that share <see cref="LastInsertionRegistry"/>, run one at a time.
/// </summary>
/// <remarks>
/// The registry is process-wide static on purpose — a per-instance record is
/// empty for the next dictation the moment anything rebuilds the inserter, and
/// macOS shipped that bug. The cost of that decision lands here: xunit runs
/// test CLASSES in parallel by default, so two classes writing the one slot
/// would fail each other intermittently, which is the worst kind of red. Naming
/// a collection with parallelisation off is the cheapest fix and keeps the
/// production type free of a test-only reset hook.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LastInsertionCollection
{
    public const string Name = "dictation last-insertion registry";
}
