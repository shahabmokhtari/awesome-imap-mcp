using UltimateImapMcp.ImapClient;

namespace UltimateImapMcp.ImapClient.Tests;

public class ThreadBuilderTests
{
    [Fact]
    public void ComputeThreadId_NoReferences_UsesMessageId()
    {
        var threadId = ThreadBuilder.ComputeThreadId("<msg1@test.com>", null);
        Assert.NotNull(threadId);
        Assert.NotEmpty(threadId);
    }

    [Fact]
    public void ComputeThreadId_WithReferences_UsesFirstReference()
    {
        var threadId1 = ThreadBuilder.ComputeThreadId("<msg3@test.com>",
            "<root@test.com> <msg2@test.com>");
        var threadId2 = ThreadBuilder.ComputeThreadId("<msg4@test.com>",
            "<root@test.com> <msg3@test.com>");

        // Both should produce same thread_id since root is the same
        Assert.Equal(threadId1, threadId2);
    }

    [Fact]
    public void ComputeThreadId_DifferentRoots_DifferentThreadIds()
    {
        var threadId1 = ThreadBuilder.ComputeThreadId("<msg1@a.com>", null);
        var threadId2 = ThreadBuilder.ComputeThreadId("<msg1@b.com>", null);

        Assert.NotEqual(threadId1, threadId2);
    }

    [Fact]
    public void ComputeThreadId_NullMessageId_ReturnsNull()
    {
        var threadId = ThreadBuilder.ComputeThreadId(null, null);
        Assert.Null(threadId);
    }

    [Fact]
    public void ComputeThreadId_Deterministic()
    {
        var id1 = ThreadBuilder.ComputeThreadId("<msg@test.com>", null);
        var id2 = ThreadBuilder.ComputeThreadId("<msg@test.com>", null);
        Assert.Equal(id1, id2);
    }
}
