using Microsoft.Extensions.Options;
using TalentLens.Infrastructure.Security;

namespace TalentLens.Web.Services;

/// <summary>
/// Every uploaded CV goes through here before it is stored or sent to the AI service:
/// 1. UploadValidator — extension, size, and real file signature (magic bytes);
/// 2. IMalwareScanner — ClamAV in production, "not scanned" in development.
/// Returns a UploadValidator.Result whose ErrorKey is a UiText key.
/// </summary>
public sealed class UploadScreener
{
    private readonly IMalwareScanner _scanner;
    private readonly MalwareScanOptions _options;
    private readonly ILogger<UploadScreener> _logger;

    public UploadScreener(IMalwareScanner scanner, IOptions<MalwareScanOptions> options, ILogger<UploadScreener> logger)
    {
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UploadValidator.Result> ScreenAsync(IFormFile file, Stream stream, int maxMb, CancellationToken ct)
    {
        var check = await UploadValidator.ValidateAsync(file, stream, maxMb);
        if (!check.Ok) return check;

        var scan = await _scanner.ScanAsync(stream, ct);
        switch (scan.Verdict)
        {
            case MalwareScanVerdict.Clean:
            case MalwareScanVerdict.NotScanned:
                return check;

            case MalwareScanVerdict.Infected:
                // Log the threat, not the file name (CV file names often contain people's names).
                _logger.LogWarning("Upload rejected: malware detected ({Threat})", scan.ThreatName);
                return check with { Ok = false, ErrorKey = "UploadInfected" };

            default: // Error
                if (_options.FailClosed)
                {
                    _logger.LogWarning("Upload rejected: malware scan failed ({Error})", scan.Error);
                    return check with { Ok = false, ErrorKey = "UploadScanFailed" };
                }
                _logger.LogWarning("Malware scan failed, accepting upload unscanned (FailClosed=false): {Error}", scan.Error);
                return check;
        }
    }
}
