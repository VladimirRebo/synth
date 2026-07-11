using Synth.Application.Embeddings;

namespace Synth.Application.Tests;

// Proves the in-memory Ollama pull tracker (SYNTH-50): Idle before anything runs, starts exactly one
// pull at a time (rejecting a concurrent start without disturbing the in-flight one), and transitions
// state + records fields through ReportProgress/Complete/Fail. Mirrors IndexJobTrackerTests' style.
public class OllamaPullTrackerTests
{
    [Fact]
    public void Current_is_Idle_before_anything_has_run()
    {
        var tracker = new InMemoryOllamaPullTracker();

        var status = tracker.Current;

        Assert.Equal(OllamaPullState.Idle, status.State);
        Assert.Equal(string.Empty, status.Model);
        Assert.Equal(string.Empty, status.Status);
        Assert.Null(status.Error);
    }

    [Fact]
    public void TryStart_transitions_to_Running_and_records_the_model()
    {
        var tracker = new InMemoryOllamaPullTracker();

        Assert.True(tracker.TryStart("nomic-embed-text"));

        var status = tracker.Current;
        Assert.Equal(OllamaPullState.Running, status.State);
        Assert.Equal("nomic-embed-text", status.Model);
    }

    [Fact]
    public void TryStart_returns_false_when_already_running_and_leaves_the_pull_untouched()
    {
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("first-model");
        tracker.ReportProgress("pulling manifest");
        var running = tracker.Current;

        // A second start while one is in progress must be rejected...
        Assert.False(tracker.TryStart("second-model"));

        // ...and must not overwrite any field of the in-flight pull.
        var after = tracker.Current;
        Assert.Equal(running, after);
        Assert.Equal("first-model", after.Model);
        Assert.Equal("pulling manifest", after.Status);
    }

    [Fact]
    public void ReportProgress_updates_the_status_of_the_in_flight_pull()
    {
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("m");

        tracker.ReportProgress("downloading (42%)");

        var status = tracker.Current;
        Assert.Equal(OllamaPullState.Running, status.State);
        Assert.Equal("downloading (42%)", status.Status);
    }

    [Fact]
    public void ReportProgress_is_a_no_op_when_no_pull_is_running()
    {
        var tracker = new InMemoryOllamaPullTracker();

        tracker.ReportProgress("downloading");

        Assert.Equal(OllamaPullState.Idle, tracker.Current.State);
    }

    [Fact]
    public void Complete_transitions_to_Done()
    {
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("m");
        tracker.ReportProgress("downloading");

        tracker.Complete();

        var status = tracker.Current;
        Assert.Equal(OllamaPullState.Done, status.State);
        Assert.Equal("m", status.Model);
        Assert.Null(status.Error);
    }

    [Fact]
    public void Fail_transitions_to_Failed_and_records_the_error()
    {
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("m");

        tracker.Fail("model 'm' not found");

        var status = tracker.Current;
        Assert.Equal(OllamaPullState.Failed, status.State);
        Assert.Equal("model 'm' not found", status.Error);
    }

    [Fact]
    public void A_finished_pull_can_be_replaced_by_a_new_run()
    {
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("first");
        tracker.Complete();

        // Once done, TryStart is allowed again (the "current-or-last" single-pull model).
        Assert.True(tracker.TryStart("second"));
        var status = tracker.Current;
        Assert.Equal(OllamaPullState.Running, status.State);
        Assert.Equal("second", status.Model);
    }
}
