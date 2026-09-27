namespace TalentLens.Web.Services;

/// <summary>Checks an uploaded CV: extension, size, and that the bytes really are PDF/DOCX.</summary>
public static class UploadValidator
{
    private static readonly Dictionary<string, (string ContentType, byte[] Magic)> Allowed = new()
    {
        [".pdf"] = ("application/pdf", "%PDF"u8.ToArray()),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [0x50, 0x4B, 0x03, 0x04]), // "PK.." — DOCX is a zip file
    };

    public sealed record Result(bool Ok, string Extension, string ContentType, string? ErrorKey);

    /// <summary>Validates and rewinds the stream. ErrorKey is a UiText key when not Ok.</summary>
    public static async Task<Result> ValidateAsync(IFormFile file, Stream stream, int maxMb)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Allowed.TryGetValue(ext, out var allowed))
            return new Result(false, ext, "", "UnsupportedType");
        if (file.Length == 0 || file.Length > maxMb * 1024L * 1024L)
            return new Result(false, ext, "", "FileTooLarge");

        var buffer = new byte[allowed.Magic.Length];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == buffer.Length && buffer.AsSpan().SequenceEqual(allowed.Magic)
            ? new Result(true, ext, allowed.ContentType, null)
            : new Result(false, ext, "", "ContentMismatch");
    }
}
