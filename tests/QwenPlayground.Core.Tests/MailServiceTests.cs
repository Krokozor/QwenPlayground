using QwenPlayground.Core.Mail;

namespace QwenPlayground.Core.Tests;

public sealed class MailServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qwen-mail-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(@"..\..\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData(@"C:\temp\outside.txt")]
    public void AttachmentPath_UntrustedName_StaysInsideDestination(string fileName)
    {
        var path = MailService.ResolveAttachmentPath(_dir, fileName, 0);

        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.Equal("outside.txt", Path.GetFileName(path));
    }

    [Fact]
    public void AttachmentPath_EmptyName_UsesIndexFallback()
    {
        var path = MailService.ResolveAttachmentPath(_dir, "", 3);
        Assert.Equal("attachment_3", Path.GetFileName(path));
    }

    [Fact]
    public void AttachmentPath_ExistingFile_GetsUniqueName()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "report.pdf"), "original");

        var path = MailService.ResolveAttachmentPath(_dir, "report.pdf", 0);

        Assert.Equal("report (2).pdf", Path.GetFileName(path));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_dir, "report.pdf")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
