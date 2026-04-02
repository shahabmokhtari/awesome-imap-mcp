using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Core.Tests.Repositories;

public class LocalLabelRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LabelsDatabase _db;
    private readonly LocalLabelRepository _repo;

    public LocalLabelRepositoryTests()
    {
        _dbPath = Path.GetTempFileName();
        _db = new LabelsDatabase(_dbPath);
        _repo = new LocalLabelRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void AddLabel_StoresLabel()
    {
        _repo.AddLabel("acct1", "msg-id-1", "important");
        var labels = _repo.GetLabels("acct1", "msg-id-1");
        Assert.Single(labels);
        Assert.Equal("important", labels[0]);
    }

    [Fact]
    public void AddLabel_DuplicateIgnored()
    {
        _repo.AddLabel("acct1", "msg-id-1", "urgent");
        _repo.AddLabel("acct1", "msg-id-1", "urgent");
        var labels = _repo.GetLabels("acct1", "msg-id-1");
        Assert.Single(labels);
    }

    [Fact]
    public void AddLabel_MultipleLabelsOnSameMessage()
    {
        _repo.AddLabel("acct1", "msg-id-1", "urgent");
        _repo.AddLabel("acct1", "msg-id-1", "work");
        var labels = _repo.GetLabels("acct1", "msg-id-1");
        Assert.Equal(2, labels.Count);
        Assert.Contains("urgent", labels);
        Assert.Contains("work", labels);
    }

    [Fact]
    public void RemoveLabel_RemovesCorrectLabel()
    {
        _repo.AddLabel("acct1", "msg-id-1", "urgent");
        _repo.AddLabel("acct1", "msg-id-1", "work");
        _repo.RemoveLabel("acct1", "msg-id-1", "urgent");
        var labels = _repo.GetLabels("acct1", "msg-id-1");
        Assert.Single(labels);
        Assert.Equal("work", labels[0]);
    }

    [Fact]
    public void GetByAccount_ReturnsGroupedLabels()
    {
        _repo.AddLabel("acct1", "msg-1", "urgent");
        _repo.AddLabel("acct1", "msg-1", "work");
        _repo.AddLabel("acct1", "msg-2", "personal");
        _repo.AddLabel("acct2", "msg-3", "other");

        var result = _repo.GetByAccount("acct1");
        Assert.Equal(2, result.Count); // 2 messages
        Assert.Equal(2, result["msg-1"].Count);
        Assert.Single(result["msg-2"]);
    }

    [Fact]
    public void GetLabels_EmptyForUnknownMessage()
    {
        var labels = _repo.GetLabels("acct1", "nonexistent");
        Assert.Empty(labels);
    }
}
