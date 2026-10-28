namespace AwesomeImapMcp.McpServer.Tools;

/// <summary>
/// Decides where <c>download_attachment</c> may write.
///
/// The caller is usually an LLM agent acting on untrusted mail, so both the
/// file name (from the sender) and the target path (possibly steered by a
/// prompt-injected message) are treated as hostile.
/// </summary>
public static class AttachmentSavePath
{
    /// <summary>
    /// Resolves the destination file. Returns the full path, or an error message
    /// when the write must not happen.
    /// </summary>
    /// <param name="savePath">A directory (the attachment's own name is appended) or a full file path.</param>
    /// <param name="attachmentFilename">The sender-supplied file name; may contain path tricks.</param>
    /// <param name="attachmentId">Used to name the file when the sender gave no usable name.</param>
    /// <param name="overwrite">Replace an existing file. Off by default so an
    /// injected instruction cannot silently clobber a shell profile or config file.</param>
    public static (string? Path, string? Error) Resolve(
        string savePath, string? attachmentFilename, int attachmentId, bool overwrite)
    {
        var resolvedPath = savePath;
        if (Directory.Exists(savePath)
            || savePath.EndsWith(System.IO.Path.DirectorySeparatorChar)
            || savePath.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
        {
            // Sanitize filename to prevent path traversal from untrusted email data
            var rawFilename = attachmentFilename ?? $"attachment_{attachmentId}";
            var filename = System.IO.Path.GetFileName(rawFilename);
            if (string.IsNullOrWhiteSpace(filename))
                filename = $"attachment_{attachmentId}";
            resolvedPath = System.IO.Path.Combine(savePath, filename);
        }

        // Verify resolved path is within the intended directory
        var fullResolved = System.IO.Path.GetFullPath(resolvedPath);
        var fullSaveDir = System.IO.Path.GetFullPath(Directory.Exists(savePath)
            ? savePath
            : System.IO.Path.GetDirectoryName(savePath) ?? savePath);
        if (!fullResolved.StartsWith(fullSaveDir, StringComparison.Ordinal))
            return (null, "Resolved path is outside the target directory.");

        if (Directory.Exists(fullResolved))
            return (null, $"'{fullResolved}' is a directory.");

        if (!overwrite && File.Exists(fullResolved))
            return (null, $"'{fullResolved}' already exists. Pass overwrite=true to replace it.");

        return (resolvedPath, null);
    }
}
