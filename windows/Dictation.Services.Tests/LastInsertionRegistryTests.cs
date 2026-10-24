using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// dictation "scratch that" — the one slot the undo acts on.
///
/// <para>
/// Three claims, and each of them is a defect somebody would otherwise ship.
/// The record survives whoever wrote it (process-wide, not per instance — the
/// macOS bug). There is exactly one of it (a stack would let a second "scratch
/// that" walk backwards into text nothing has age-checked). And taking it
/// removes it in the same breath (two undos racing must not both come away
/// holding the same insertion).
/// </para>
/// </summary>
[Collection(LastInsertionCollection.Name)]
public class LastInsertionRegistryTests : IDisposable
{
    public LastInsertionRegistryTests() => LastInsertionRegistry.Clear();

    public void Dispose() => LastInsertionRegistry.Clear();

    private static DictationInsertionRecord Record(string text = "hello") =>
        new(text, 0x505b8, "RichEditD2DPT", DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_fresh_registry_has_nothing_to_undo()
    {
        // NothingRecorded rather than a delete of zero characters — the policy
        // reads this null and says so.
        Assert.Null(LastInsertionRegistry.Peek());
        Assert.Null(LastInsertionRegistry.Take());
    }

    [Fact]
    public void What_was_recorded_comes_back_whole()
    {
        var record = Record("the spoken sentence");

        LastInsertionRegistry.Record(record);

        // Whole, not a copy of the text: the handle and the class are what
        // DictationUndoPolicy compares, and the timestamp is what expires.
        Assert.Same(record, LastInsertionRegistry.Peek());
    }

    [Fact]
    public void Peek_does_not_spend_the_record()
    {
        // The HUD asks "is there anything to undo?" without undoing it.
        LastInsertionRegistry.Record(Record());

        Assert.NotNull(LastInsertionRegistry.Peek());
        Assert.NotNull(LastInsertionRegistry.Peek());
    }

    [Fact]
    public void Take_spends_it()
    {
        LastInsertionRegistry.Record(Record());

        Assert.NotNull(LastInsertionRegistry.Take());
        // THE POINT. A second "scratch that" must find nothing rather than
        // walking backwards into text from an earlier insertion, which no gate
        // in DictationUndoPolicy has ever looked at.
        Assert.Null(LastInsertionRegistry.Take());
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public void There_is_one_slot_and_the_newest_insertion_owns_it()
    {
        // Not a stack, deliberately. Every guard the policy holds — two
        // minutes, one window, a grapheme count — is stated about ONE
        // insertion, so there is no honest way to age-check a second entry.
        LastInsertionRegistry.Record(Record("first"));
        LastInsertionRegistry.Record(Record("second"));

        Assert.Equal("second", LastInsertionRegistry.Take()?.Text);
        Assert.Null(LastInsertionRegistry.Take());
    }

    [Fact]
    public void Clear_forgets_it_without_reading_it()
    {
        LastInsertionRegistry.Record(Record());

        LastInsertionRegistry.Clear();

        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public void The_record_outlives_the_object_that_wrote_it()
    {
        // The macOS defect, pinned. TextInserter.swift:508-517 held the record
        // on the inserter; anything that rebuilt the inserter emptied it, and
        // the user's experience of that was not "undo is unavailable" but
        // "scratch that worked yesterday". Static means the record's lifetime
        // matches the TEXT's — which is sitting in somebody's document — rather
        // than some object graph's.
        static void WriteFromSomewhereElse() =>
            LastInsertionRegistry.Record(Record("typed by an inserter that is now gone"));

        WriteFromSomewhereElse();
        GC.Collect();

        Assert.Equal("typed by an inserter that is now gone", LastInsertionRegistry.Peek()?.Text);
    }

    [Fact]
    public void Two_takers_do_not_both_get_the_same_insertion()
    {
        // Read-and-remove under one lock. If this split into a read and a
        // clear, two undos racing would each send the same run of backspaces
        // and the second would eat text the user wrote by hand.
        LastInsertionRegistry.Record(Record());

        var taken = 0;
        Parallel.For(0, 64, _ =>
        {
            if (LastInsertionRegistry.Take() is not null)
            {
                Interlocked.Increment(ref taken);
            }
        });

        Assert.Equal(1, taken);
    }
}
