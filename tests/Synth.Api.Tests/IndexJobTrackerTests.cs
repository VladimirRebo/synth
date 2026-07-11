using Synth.Api.Indexing;
using Synth.Core.Indexing;

namespace Synth.Api.Tests;

// Proves SYNTH-30: the in-memory job tracker reports Idle before anything runs, starts exactly one
// job at a time (rejecting a concurrent start without disturbing the in-flight job), and transitions
// state + populates fields through ReportProgress/Complete/Fail. This is the primitive SYNTH-31 will
// wire POST /index onto.
public class IndexJobTrackerTests
{
    [Fact]
    public void Current_is_Idle_before_anything_has_run()
    {
        var tracker = new InMemoryIndexJobTracker();

        var status = tracker.Current;

        Assert.Equal(IndexJobState.Idle, status.State);
        Assert.Equal(string.Empty, status.Collection);
        Assert.Equal(string.Empty, status.Source);
        Assert.Null(status.TotalFiles);
        Assert.Null(status.StartedAt);
        Assert.Null(status.FinishedAt);
        Assert.Null(status.Error);
        Assert.Equal(0, status.FilesIndexed);
        Assert.Equal(0, status.ChunksIndexed);
    }

    [Fact]
    public void TryStart_transitions_to_Running_and_records_the_source()
    {
        var tracker = new InMemoryIndexJobTracker();

        Assert.True(tracker.TryStart("my-collection", "/some/path"));

        var status = tracker.Current;
        Assert.Equal(IndexJobState.Running, status.State);
        Assert.Equal("my-collection", status.Collection);
        Assert.Equal("/some/path", status.Source);
        Assert.NotNull(status.StartedAt);
        Assert.Null(status.FinishedAt);
    }

    [Fact]
    public void TryStart_returns_false_when_already_running_and_leaves_the_job_untouched()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("first", "/first/path");
        tracker.ReportProgress(filesIndexed: 3, filesSkipped: 1, totalFiles: 10);
        var running = tracker.Current;

        // A second start while one is in progress must be rejected...
        Assert.False(tracker.TryStart("second", "/second/path"));

        // ...and must not overwrite any field of the in-flight job.
        var after = tracker.Current;
        Assert.Equal(running, after);
        Assert.Equal("first", after.Collection);
        Assert.Equal("/first/path", after.Source);
        Assert.Equal(3, after.FilesIndexed);
        Assert.Equal(1, after.FilesSkipped);
        Assert.Equal(10, after.TotalFiles);
    }

    [Fact]
    public void ReportProgress_updates_counters_of_the_in_flight_job()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("c", "s");

        tracker.ReportProgress(filesIndexed: 5, filesSkipped: 2, totalFiles: 20);

        var status = tracker.Current;
        Assert.Equal(IndexJobState.Running, status.State);
        Assert.Equal(5, status.FilesIndexed);
        Assert.Equal(2, status.FilesSkipped);
        Assert.Equal(20, status.TotalFiles);
    }

    [Fact]
    public void ReportProgress_is_a_no_op_when_no_job_is_running()
    {
        var tracker = new InMemoryIndexJobTracker();

        tracker.ReportProgress(filesIndexed: 5, filesSkipped: 2, totalFiles: 20);

        Assert.Equal(IndexJobState.Idle, tracker.Current.State);
    }

    [Fact]
    public void ReportProgress_with_null_total_keeps_the_previously_counted_total()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("c", "s");
        tracker.ReportProgress(filesIndexed: 0, filesSkipped: 0, totalFiles: 7);

        tracker.ReportProgress(filesIndexed: 1, filesSkipped: 0, totalFiles: null);

        Assert.Equal(7, tracker.Current.TotalFiles);
    }

    [Fact]
    public void Complete_transitions_to_Done_and_populates_final_counts()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("c", "s");

        tracker.Complete(filesIndexed: 8, filesSkipped: 3, chunksIndexed: 42);

        var status = tracker.Current;
        Assert.Equal(IndexJobState.Done, status.State);
        Assert.Equal(8, status.FilesIndexed);
        Assert.Equal(3, status.FilesSkipped);
        Assert.Equal(42, status.ChunksIndexed);
        Assert.NotNull(status.FinishedAt);
        Assert.Null(status.Error);
    }

    [Fact]
    public void Fail_transitions_to_Failed_and_records_the_error()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("c", "s");

        tracker.Fail("boom");

        var status = tracker.Current;
        Assert.Equal(IndexJobState.Failed, status.State);
        Assert.Equal("boom", status.Error);
        Assert.NotNull(status.FinishedAt);
    }

    [Fact]
    public void A_finished_job_can_be_replaced_by_a_new_run()
    {
        var tracker = new InMemoryIndexJobTracker();
        tracker.TryStart("first", "s1");
        tracker.Complete(filesIndexed: 1, filesSkipped: 0, chunksIndexed: 1);

        // Once done, TryStart is allowed again (this is the "current-or-last" single-job model).
        Assert.True(tracker.TryStart("second", "s2"));
        var status = tracker.Current;
        Assert.Equal(IndexJobState.Running, status.State);
        Assert.Equal("second", status.Collection);
        Assert.Null(status.FinishedAt);
    }
}
