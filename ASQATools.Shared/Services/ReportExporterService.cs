using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ASQATools.Shared.Models;

namespace ASQATools.Shared.Services;

public class ReportExporterService
{
    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 20,
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ASQATools-ReportExporter/1.0");
        return client;
    }

    public static char DetectDelimiter(string rawData, string delimiterMode)
    {
        return (delimiterMode?.ToLowerInvariant()) switch
        {
            "tab" => '\t',
            "comma" => ',',
            "semicolon" => ';',
            _ => AutoDetectDelimiterFromText(rawData)
        };
    }

    private static char AutoDetectDelimiterFromText(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData)) return ',';
        using var reader = new StringReader(rawData);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains('\t')) return '\t';
            if (line.Contains(',')) return ',';
            if (line.Contains(';')) return ';';
            break;
        }
        return ',';
    }

    public static List<List<string>> ParseRows(
        string rawData,
        string delimiterMode,
        bool skipHeaderRow,
        out char usedDelimiter,
        out List<string> headerColumns)
    {
        headerColumns = new List<string>();
        usedDelimiter = DetectDelimiter(rawData, delimiterMode);
        var rows = new List<List<string>>();

        if (string.IsNullOrWhiteSpace(rawData)) return rows;

        var lines = rawData.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool firstNonEmptySeen = false;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var fields = ParseDelimitedLine(rawLine, usedDelimiter);
            if (fields.Count == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;

            if (!firstNonEmptySeen)
            {
                firstNonEmptySeen = true;
                if (skipHeaderRow)
                {
                    headerColumns = fields;
                    continue;
                }
            }

            rows.Add(fields);
        }

        return rows;
    }

    public static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString().Trim());
        return result;
    }

    public static string ResolveUrlTemplate(string templateUrl, IReadOnlyList<string> values)
    {
        if (string.IsNullOrWhiteSpace(templateUrl)) return string.Empty;
        string trimmedTemplate = templateUrl.Trim();

        return Regex.Replace(trimmedTemplate, @"\{(\d+)\}", match =>
        {
            if (int.TryParse(match.Groups[1].Value, out int index) && index >= 0 && index < values.Count)
            {
                string rawVal = values[index].Trim();
                // If {0} is at the very start of the template and already contains http(s)://, do not escape the full URL
                if (match.Index == 0 && (rawVal.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                         rawVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    return rawVal;
                }
                return Uri.EscapeDataString(rawVal);
            }
            return match.Value;
        });
    }

    public static string ResolveRelativeFilePath(string filePathTemplate, IReadOnlyList<string> values, int rowNumber)
    {
        string template = string.IsNullOrWhiteSpace(filePathTemplate) ? "file_{0}.pdf" : filePathTemplate.Trim();

        string substituted = Regex.Replace(template, @"\{(\d+|row|index)\}", match =>
        {
            string token = match.Groups[1].Value;
            if (token.Equals("row", StringComparison.OrdinalIgnoreCase) || token.Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                return rowNumber.ToString();
            }
            if (int.TryParse(token, out int index) && index >= 0 && index < values.Count)
            {
                return SanitizePathSegment(values[index]);
            }
            return $"col{token}";
        }, RegexOptions.IgnoreCase);

        var rawSegments = substituted
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathSegment)
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "." && s != "..")
            .ToList();

        if (rawSegments.Count == 0)
        {
            rawSegments.Add($"report_{rowNumber}.pdf");
        }

        string lastSegment = rawSegments[^1];
        if (!lastSegment.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            rawSegments[^1] = lastSegment + ".pdf";
        }

        return string.Join("/", rawSegments);
    }

    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "empty";
        string cleaned = input.Trim();
        char[] invalidChars = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n', '\t' };
        var sb = new StringBuilder(cleaned.Length);
        foreach (char c in cleaned)
        {
            if (Array.IndexOf(invalidChars, c) >= 0 || char.IsControl(c))
                sb.Append('_');
            else
                sb.Append(c);
        }
        string res = sb.ToString().Trim().Trim('.');
        return string.IsNullOrWhiteSpace(res) ? "unnamed" : res;
    }

    public async Task<ReportExportBatchResult> ExecuteBatchExportAsync(
        ReportExportRequest req,
        string defaultBaseRoot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.TemplateUrl))
            throw new ArgumentException("Template PDF URL is required.");

        var rows = ParseRows(req.RawData, req.DelimiterMode, req.SkipHeaderRow, out char delimiter, out _);
        if (rows.Count == 0)
            throw new InvalidOperationException("No valid data rows found to process.");

        string outputRoot = ResolveOutputDirectory(req.ParentFolder, defaultBaseRoot);
        Directory.CreateDirectory(outputRoot);
        string canonicalOutputRoot = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string delimName = delimiter == '\t' ? "TAB" : delimiter == ';' ? "SEMICOLON" : "COMMA";
        int concurrency = Math.Clamp(req.MaxConcurrency, 1, 10);

        Log($"=== Initiating PDF Report Batch Extractor ===");
        Log($"📂 Output Parent Folder: {canonicalOutputRoot}");
        Log($"📊 Parsed {rows.Count} data row(s) [Delimiter: {delimName}, Concurrency: {concurrency}, Duplicate Mode: {req.DuplicateMode.ToUpperInvariant()}]");

        // Pre-plan target file paths deterministically
        var plannedItems = new List<(int RowNumber, List<string> Values, string Url, string RelativePath, string FullPath, bool PreSkipped)>(rows.Count);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dupMode = (req.DuplicateMode ?? "overwrite").ToLowerInvariant();

        for (int i = 0; i < rows.Count; i++)
        {
            int rowNum = i + 1;
            var rowValues = rows[i];
            string url = ResolveUrlTemplate(req.TemplateUrl, rowValues);
            string relPath = ResolveRelativeFilePath(req.FilePathTemplate, rowValues, rowNum);

            string osRelPath = relPath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(canonicalOutputRoot, osRelPath));

            if (!fullPath.StartsWith(canonicalOutputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Path traversal blocked for row #{rowNum}: {relPath}");
            }

            bool preSkipped = false;
            if (dupMode == "skip")
            {
                if (File.Exists(fullPath) || !reservedPaths.Add(fullPath))
                {
                    preSkipped = true;
                }
            }
            else if (dupMode == "autonumber")
            {
                string dir = Path.GetDirectoryName(fullPath)!;
                string baseName = Path.GetFileNameWithoutExtension(fullPath);
                string ext = Path.GetExtension(fullPath);
                int counter = 1;
                while (File.Exists(fullPath) || reservedPaths.Contains(fullPath))
                {
                    fullPath = Path.Combine(dir, $"{baseName} ({counter}){ext}");
                    counter++;
                }
                reservedPaths.Add(fullPath);
                relPath = Path.GetRelativePath(canonicalOutputRoot, fullPath).Replace('\\', '/');
            }
            else
            {
                reservedPaths.Add(fullPath);
            }

            plannedItems.Add((rowNum, rowValues, url, relPath, fullPath, preSkipped));
        }

        using var limiter = new SemaphoreSlim(concurrency);
        var tasks = plannedItems.Select(async plan =>
        {
            if (plan.PreSkipped)
            {
                long existingLen = File.Exists(plan.FullPath) ? new FileInfo(plan.FullPath).Length : 0;
                return (
                    Result: new ReportExportItemResult
                    {
                        RowNumber = plan.RowNumber,
                        Values = plan.Values,
                        ResolvedUrl = plan.Url,
                        RelativeFilePath = plan.RelativePath,
                        FullSavedPath = plan.FullPath,
                        Status = "Skipped",
                        FileSizeBytes = existingLen
                    },
                    LogLine: $"  ⏭️ [Row #{plan.RowNumber}] Skipped existing: {plan.RelativePath}"
                );
            }

            await limiter.WaitAsync(ct);
            try
            {
                if (!Uri.TryCreate(plan.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return (
                        Result: new ReportExportItemResult
                        {
                            RowNumber = plan.RowNumber,
                            Values = plan.Values,
                            ResolvedUrl = plan.Url,
                            RelativeFilePath = plan.RelativePath,
                            FullSavedPath = plan.FullPath,
                            Status = "Failed",
                            ErrorMessage = "Invalid HTTP/HTTPS URL"
                        },
                        LogLine: $"  ❌ [Row #{plan.RowNumber}] Invalid URL: {plan.Url}"
                    );
                }

                using var resp = await SharedHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return (
                        Result: new ReportExportItemResult
                        {
                            RowNumber = plan.RowNumber,
                            Values = plan.Values,
                            ResolvedUrl = plan.Url,
                            RelativeFilePath = plan.RelativePath,
                            FullSavedPath = plan.FullPath,
                            Status = "Failed",
                            ErrorMessage = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})"
                        },
                        LogLine: $"  ❌ [Row #{plan.RowNumber}] Failed ({(int)resp.StatusCode} {resp.ReasonPhrase}) -> {plan.RelativePath}"
                    );
                }

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0)
                {
                    return (
                        Result: new ReportExportItemResult
                        {
                            RowNumber = plan.RowNumber,
                            Values = plan.Values,
                            ResolvedUrl = plan.Url,
                            RelativeFilePath = plan.RelativePath,
                            FullSavedPath = plan.FullPath,
                            Status = "Failed",
                            ErrorMessage = "Empty response body (0 bytes)"
                        },
                        LogLine: $"  ❌ [Row #{plan.RowNumber}] Empty response (0 bytes) -> {plan.RelativePath}"
                    );
                }

                string? targetDir = Path.GetDirectoryName(plan.FullPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await File.WriteAllBytesAsync(plan.FullPath, bytes, ct);

                return (
                    Result: new ReportExportItemResult
                    {
                        RowNumber = plan.RowNumber,
                        Values = plan.Values,
                        ResolvedUrl = plan.Url,
                        RelativeFilePath = plan.RelativePath,
                        FullSavedPath = plan.FullPath,
                        Status = "Downloaded",
                        FileSizeBytes = bytes.Length
                    },
                    LogLine: $"  ✔ [Row #{plan.RowNumber}] Saved: {plan.RelativePath} ({(bytes.Length / 1024.0):N1} KB)"
                );
            }
            catch (Exception ex)
            {
                return (
                    Result: new ReportExportItemResult
                    {
                        RowNumber = plan.RowNumber,
                        Values = plan.Values,
                        ResolvedUrl = plan.Url,
                        RelativeFilePath = plan.RelativePath,
                        FullSavedPath = plan.FullPath,
                        Status = "Failed",
                        ErrorMessage = ex.Message
                    },
                    LogLine: $"  ❌ [Row #{plan.RowNumber}] Error downloading {plan.RelativePath}: {ex.Message}"
                );
            }
            finally
            {
                limiter.Release();
            }
        }).ToArray();

        var completed = await Task.WhenAll(tasks);

        var batchResult = new ReportExportBatchResult
        {
            OutputDirectory = canonicalOutputRoot,
            TotalRows = rows.Count
        };

        foreach (var item in completed.OrderBy(c => c.Result.RowNumber))
        {
            Log(item.LogLine);
            batchResult.Items.Add(item.Result);
            if (item.Result.Status == "Downloaded")
            {
                batchResult.DownloadedCount++;
                batchResult.TotalBytesDownloaded += item.Result.FileSizeBytes;
            }
            else if (item.Result.Status == "Skipped")
            {
                batchResult.SkippedCount++;
            }
            else
            {
                batchResult.FailedCount++;
            }
        }

        if (req.CreateZipArchive && (batchResult.DownloadedCount > 0 || batchResult.SkippedCount > 0))
        {
            try
            {
                string zipDir = Path.Combine(defaultBaseRoot, "_zips");
                Directory.CreateDirectory(zipDir);
                string folderName = Path.GetFileName(canonicalOutputRoot);
                string zipPath = Path.Combine(zipDir, $"{folderName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);

                ZipFile.CreateFromDirectory(canonicalOutputRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
                batchResult.ZipFilePath = zipPath;
                Log($"📦 Created downloadable ZIP archive: {Path.GetFileName(zipPath)}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not package ZIP archive: {ex.Message}");
            }
        }

        Log($"✔ Batch Complete: {batchResult.DownloadedCount} downloaded, {batchResult.SkippedCount} skipped, {batchResult.FailedCount} failed ({(batchResult.TotalBytesDownloaded / 1024.0):N1} KB total).");
        return batchResult;
    }

    private static string ResolveOutputDirectory(string parentFolder, string defaultBaseRoot)
    {
        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            return Path.Combine(defaultBaseRoot, $"Reports_{DateTime.Now:yyyy_MM_dd}");
        }

        string trimmed = parentFolder.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        // Relative folder name under defaultBaseRoot
        string safeRelative = string.Join(
            Path.DirectorySeparatorChar.ToString(),
            trimmed.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(SanitizePathSegment)
                   .Where(s => s != "." && s != ".."));

        if (string.IsNullOrWhiteSpace(safeRelative))
            safeRelative = $"Reports_{DateTime.Now:yyyy_MM_dd}";

        return Path.GetFullPath(Path.Combine(defaultBaseRoot, safeRelative));
    }
}
