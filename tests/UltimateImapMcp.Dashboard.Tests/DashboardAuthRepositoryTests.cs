using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Dashboard;

namespace UltimateImapMcp.Dashboard.Tests;

public class DashboardAuthRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly DashboardAuthRepository _repo;

    public DashboardAuthRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _repo = new DashboardAuthRepository(_db);
    }

    [Fact]
    public void HasPinSet_Initially_ReturnsFalse()
    {
        Assert.False(_repo.HasPinSet());
    }

    [Fact]
    public void UpsertPin_ThenHasPinSet_ReturnsTrue()
    {
        _repo.UpsertPin(BCrypt.Net.BCrypt.HashPassword("1234"));
        Assert.True(_repo.HasPinSet());
    }

    [Fact]
    public void VerifyPin_CorrectPin_ReturnsTrue()
    {
        var pin = "5678";
        _repo.UpsertPin(BCrypt.Net.BCrypt.HashPassword(pin));

        var auth = _repo.GetPinAuth();
        Assert.NotNull(auth);
        Assert.True(BCrypt.Net.BCrypt.Verify(pin, auth.Hash));
    }

    [Fact]
    public void VerifyPin_WrongPin_ReturnsFalse()
    {
        _repo.UpsertPin(BCrypt.Net.BCrypt.HashPassword("correct-pin"));

        var auth = _repo.GetPinAuth();
        Assert.NotNull(auth);
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-pin", auth.Hash));
    }

    [Fact]
    public void CreateSession_ReturnsValidToken()
    {
        var token = _repo.CreateSession(TimeSpan.FromHours(1));
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(token.Length > 20); // base64 of two GUIDs
    }

    [Fact]
    public void ValidateSession_ValidToken_ReturnsTrue()
    {
        var token = _repo.CreateSession(TimeSpan.FromHours(1));
        Assert.True(_repo.ValidateSession(token));
    }

    [Fact]
    public void ValidateSession_ExpiredToken_ReturnsFalse()
    {
        // Insert a session directly with an expires_at firmly in the past,
        // using SQLite's datetime format to avoid format mismatch issues.
        var token = "expired-test-token-" + Guid.NewGuid();
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dashboard_sessions (token, expires_at)
                VALUES ($token, datetime('now', '-1 hour'));
                """;
            cmd.Parameters.AddWithValue("$token", token);
            cmd.ExecuteNonQuery();
        });

        Assert.False(_repo.ValidateSession(token));
    }

    [Fact]
    public void ValidateSession_InvalidToken_ReturnsFalse()
    {
        Assert.False(_repo.ValidateSession("nonexistent-token"));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
