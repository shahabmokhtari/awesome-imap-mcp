using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.ImapClient.Tests.Repositories;

public class AccountRepositoryTests : IDisposable
{
    private readonly string _accountsPath;
    private readonly AccountsStore _store;
    private readonly AccountRepository _repo;

    public AccountRepositoryTests()
    {
        _accountsPath = Path.Combine(Path.GetTempPath(), $"test_accounts_{Guid.NewGuid()}.json");
        _store = new AccountsStore(_accountsPath);
        _repo = new AccountRepository(_store);
    }

    [Fact]
    public void Insert_And_GetById_ReturnsAccount()
    {
        _repo.Insert("personal", "Personal Gmail", "imap.gmail.com", 993,
            "smtp.gmail.com", 465, true, "test@gmail.com", "app_password",
            "encrypted_creds", "gmail", null);

        var account = _repo.GetById("personal");
        Assert.NotNull(account);
        Assert.Equal("Personal Gmail", account.Name);
        Assert.Equal("imap.gmail.com", account.ImapHost);
        Assert.Equal("test@gmail.com", account.Username);
    }

    [Fact]
    public void GetAll_ReturnsAllAccounts()
    {
        _repo.Insert("a1", "Account 1", "imap.a.com", 993,
            null, 465, true, "u1@a.com", "password", "enc1", "generic", null);
        _repo.Insert("a2", "Account 2", "imap.b.com", 993,
            null, 465, true, "u2@b.com", "password", "enc2", "generic", null);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        var account = _repo.GetById("nonexistent");
        Assert.Null(account);
    }

    public void Dispose()
    {
        if (File.Exists(_accountsPath)) File.Delete(_accountsPath);
    }
}
