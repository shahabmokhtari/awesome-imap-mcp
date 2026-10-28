using AwesomeImapMcp.McpServer.Tools;

namespace AwesomeImapMcp.McpServer.Tests.Tools;

public sealed class AttachmentSavePathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("attach-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Directory_Target_UsesTheAttachmentName()
    {
        var (path, error) = AttachmentSavePath.Resolve(_dir, "invoice.pdf", 7, overwrite: false);

        Assert.Null(error);
        Assert.Equal(Path.Combine(_dir, "invoice.pdf"), path);
    }

    [Theory]
    [InlineData("../../.zshrc")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\evil.bat")]
    public void HostileAttachmentName_CannotEscapeTheDirectory(string senderName)
    {
        var (path, error) = AttachmentSavePath.Resolve(_dir, senderName, 7, overwrite: false);

        Assert.Null(error);
        Assert.StartsWith(_dir, Path.GetFullPath(path!));
    }

    [Fact]
    public void MissingAttachmentName_FallsBackToId()
    {
        var (path, _) = AttachmentSavePath.Resolve(_dir, "", 42, overwrite: false);

        Assert.Equal(Path.Combine(_dir, "attachment_42"), path);
    }

    [Fact]
    public void ExistingFile_IsNotOverwrittenByDefault()
    {
        // The prompt-injection case: "save the attachment to ~/.zshrc".
        var victim = Path.Combine(_dir, ".zshrc");
        File.WriteAllText(victim, "original");

        var (path, error) = AttachmentSavePath.Resolve(victim, "payload.txt", 1, overwrite: false);

        Assert.Null(path);
        Assert.Contains("already exists", error);
        Assert.Equal("original", File.ReadAllText(victim));
    }

    [Fact]
    public void ExistingFile_CanBeReplacedWhenExplicitlyAsked()
    {
        var target = Path.Combine(_dir, "report.pdf");
        File.WriteAllText(target, "old");

        var (path, error) = AttachmentSavePath.Resolve(target, "report.pdf", 1, overwrite: true);

        Assert.Null(error);
        Assert.Equal(target, path);
    }

    [Fact]
    public void ExistingFile_ViaDirectoryTarget_IsAlsoProtected()
    {
        File.WriteAllText(Path.Combine(_dir, "invoice.pdf"), "original");

        var (_, error) = AttachmentSavePath.Resolve(_dir, "invoice.pdf", 1, overwrite: false);

        Assert.Contains("already exists", error);
    }

    [Fact]
    public void FullPathToNewFile_IsAllowed()
    {
        var target = Path.Combine(_dir, "new.pdf");

        var (path, error) = AttachmentSavePath.Resolve(target, "ignored.pdf", 1, overwrite: false);

        Assert.Null(error);
        Assert.Equal(target, path);
    }
}
