using Microsoft.Extensions.Logging.Abstractions;

namespace AwesomeImapMcp.ImapClient.Tests;

/// <summary>
/// A cache clear pauses sync internally and resumes it afterwards; a pause the user
/// makes while the clear runs must survive that resume.
/// </summary>
public class SyncManagerPauseTests
{
    // Only the pause state is exercised; none of the collaborators are touched.
    private static SyncManager Create() => new(null!, null!, null!, null!, null!, null!, null!, null!,
        null!, null!, null!, NullLogger<SyncManager>.Instance);

    [Fact]
    public void ResumeIfNotPausedSince_NoOtherPause_Resumes()
    {
        var sync = Create();
        sync.StopSync();
        var ours = sync.PauseGeneration;

        Assert.True(sync.ResumeIfNotPausedSince(ours));
        Assert.False(sync.IsPaused);
    }

    [Fact]
    public void ResumeIfNotPausedSince_UserPausedMeanwhile_StaysPaused()
    {
        var sync = Create();
        sync.StopSync();
        var ours = sync.PauseGeneration;

        sync.StopSync(); // the user clicks Stop Sync during the clear

        Assert.False(sync.ResumeIfNotPausedSince(ours));
        Assert.True(sync.IsPaused);
    }
}
