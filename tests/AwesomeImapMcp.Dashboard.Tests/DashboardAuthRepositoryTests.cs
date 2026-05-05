using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Dashboard;

namespace AwesomeImapMcp.Dashboard.Tests;

public class DashboardAuthRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AppConfig _config;
    private readonly DashboardAuthRepository _repo;

    public DashboardAuthRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _config = new AppConfig();
        _repo = new DashboardAuthRepository(_db, _config);
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

        var pinHash = _repo.GetPinHash();
        Assert.NotNull(pinHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(pin, pinHash));
    }

    [Fact]
    public void VerifyPin_WrongPin_ReturnsFalse()
    {
        _repo.UpsertPin(BCrypt.Net.BCrypt.HashPassword("correct-pin"));

        var pinHash = _repo.GetPinHash();
        Assert.NotNull(pinHash);
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-pin", pinHash));
    }

    [Fact]
    public void CreateSession_ReturnsValidToken()
    {
        var token = _repo.CreateSession(TimeSpan.FromHours(1));
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(token.Length > 20);
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
        var token = "expired-test-token-" + Guid.NewGuid();
        _db.ExecuteWrite(conn =>
        {
            using var createCmd = conn.CreateCommand();
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS dashboard_sessions (
                    token TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    expires_at TEXT NOT NULL
                );
                """;
            createCmd.ExecuteNonQuery();

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

    [Fact]
    public void PinHash_StoredInConfig_NotDb()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("9999");
        _repo.UpsertPin(hash);

        // Verify it's in config
        Assert.Equal(hash, _config.Server.DashboardPinHash);
        Assert.Equal("pin", _config.Server.DashboardAuth);
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
