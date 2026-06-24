using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace pdf_studio.Services;

/// <summary>
/// Multi-process PDF rendering server that distributes page rendering across
/// multiple worker processes via anonymous pipes. Used to accelerate batch
/// rendering (e.g., placeholder initialization) since MuPDF is not thread-safe.
/// </summary>
public sealed class MultiProcessRenderServer : IDisposable
{
    private readonly string _pdfFilePath;
    private readonly int _workerCount;
    private readonly string? _customWorkerPath;
    private bool _disposed;

    public MultiProcessRenderServer(string pdfFilePath, int workerCount = 8, string? customWorkerPath = null)
    {
        _pdfFilePath = pdfFilePath ?? throw new ArgumentNullException(nameof(pdfFilePath));
        _workerCount = workerCount > 0 ? workerCount : throw new ArgumentOutOfRangeException(nameof(workerCount));
        _customWorkerPath = customWorkerPath;
    }

    /// <summary>
    /// Renders all pages of the PDF at the specified DPI using multiple worker processes.
    /// Returns a result indicating success/completeness along with the rendered page data.
    /// </summary>
    public async Task<MultiProcessRenderResult> RenderAllPagesAsync(
        int dpi,
        CancellationToken cancellationToken = default)
    {
        string? workerExe = LocateWorkerExecutable(_customWorkerPath);
        if (workerExe == null)
            throw new WorkerNotFoundException("pdf_rendering.exe not found. Ensure PdfRenderCLI/ is deployed with the application.");

        byte[] pdfBytes = await File.ReadAllBytesAsync(_pdfFilePath, cancellationToken);

        // Use a temporary Document to get page count
        int totalPages;
        using (var doc = new MuPDF.NET.Document(stream: pdfBytes, fileType: "pdf"))
        {
            totalPages = doc.PageCount;
        }

        if (totalPages == 0)
            return new MultiProcessRenderResult(Array.Empty<PageRenderResult>(), true);

        // Distribute pages evenly across workers
        int pagesPerWorker = totalPages / _workerCount;
        int remainder = totalPages % _workerCount;

        var groups = new List<(int start, int end)>();
        int currentStart = 0;
        for (int w = 0; w < _workerCount; w++)
        {
            int count = pagesPerWorker + (w < remainder ? 1 : 0);
            if (count == 0) break;
            groups.Add((currentStart, currentStart + count - 1));
            currentStart += count;
        }

        Debug.WriteLine($"[MultiProcess] Rendering {totalPages} pages @ {dpi} DPI with {groups.Count} workers");
        Debug.WriteLine($"[MultiProcess] Worker exe: {workerExe}");

        // Launch all worker groups in parallel
        var tasks = groups.Select((g, i) =>
            Task.Run(() => ProcessGroupAsync(i, g.start, g.end, dpi, pdfBytes, workerExe, cancellationToken)));

        var groupResults = await Task.WhenAll(tasks);

        // Aggregate and sort results
        var allPages = groupResults
            .Where(r => r.Success)
            .SelectMany(r => r.Pages)
            .OrderBy(p => p.PageNumber)
            .ToList();

        bool isComplete = groupResults.All(r => r.Success) && allPages.Count == totalPages;

        if (!isComplete)
        {
            int failedGroups = groupResults.Count(r => !r.Success);
            Debug.WriteLine($"[MultiProcess] WARNING: {failedGroups}/{groupResults.Length} groups failed, got {allPages.Count}/{totalPages} pages");
        }

        return new MultiProcessRenderResult(allPages, isComplete);
    }

    // ══════════════════════════════════════════════════════
    //  Worker executable location
    // ══════════════════════════════════════════════════════

    private static string? LocateWorkerExecutable(string? customPath)
    {
        if (customPath != null && File.Exists(customPath))
            return customPath;

        string baseDir = AppContext.BaseDirectory;

        // Primary candidates
        string[] candidates =
        [
            Path.Combine(baseDir, "PdfRenderCLI", "pdf_rendering.exe"),
            Path.Combine(baseDir, "pdf_rendering.exe"),
        ];

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Walk up from baseDir looking for PdfRenderCLI/pdf_rendering.exe
        // (handles development-time layout where output is deep under bin/)
        var dir = baseDir;
        for (int i = 0; i < 6; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
            var candidate = Path.Combine(dir, "PdfRenderCLI", "pdf_rendering.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // ══════════════════════════════════════════════════════
    //  Process a single worker group
    // ══════════════════════════════════════════════════════

    private static GroupRenderResult ProcessGroupAsync(
        int groupIndex,
        int startPage,
        int endPage,
        int dpi,
        byte[] pdfBytes,
        string workerExe,
        CancellationToken cancellationToken)
    {
        var pages = new List<PageRenderResult>();

        using var outboundPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var inboundPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var psi = new ProcessStartInfo
        {
            FileName = workerExe,
            Arguments = $"\"{outboundPipe.GetClientHandleAsString()}\" \"{inboundPipe.GetClientHandleAsString()}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Set working directory to the output root where NuGet provides
            // the native DLLs (mupdfcpp64, mupdfcsharp, libSkiaSharp). Both the
            // main app and the worker now use MuPDF.NET v3.2.16 — shared DLLs.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();

            // Release local copies of client handles (child process inherited them)
            outboundPipe.DisposeLocalCopyOfClientHandle();
            inboundPipe.DisposeLocalCopyOfClientHandle();

            Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Worker PID={proc.Id} (pages {startPage}-{endPage})");

            // ── Send init message: [pdfLen][pdfBytes][start][end][dpi] ──
            using (var writer = new BinaryWriter(outboundPipe, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(pdfBytes.Length);
                writer.Write(pdfBytes);
                writer.Write(startPage);
                writer.Write(endPage);
                writer.Write(dpi);
                writer.Flush();
            }
            // outboundPipe disposed here → close write end, signal EOF to worker

            // Read stderr in background
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd(), cancellationToken);

            // ── Read results from inbound pipe ──
            using (var reader = new BinaryReader(inboundPipe, Encoding.UTF8, leaveOpen: true))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageNumber;
                    try
                    {
                        pageNumber = reader.ReadInt32();
                    }
                    catch (EndOfStreamException)
                    {
                        Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Worker pipe closed unexpectedly.");
                        break;
                    }

                    if (pageNumber == -1) break; // Normal end sentinel

                    if (pageNumber == -2)
                    {
                        // Fatal worker error
                        int errLen = reader.ReadInt32();
                        byte[] errBytes = ReadExactBytes(reader, errLen);
                        Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Worker fatal: {Encoding.UTF8.GetString(errBytes)}");
                        break;
                    }

                    int dataLength = reader.ReadInt32();

                    if (dataLength == -1)
                    {
                        // Per-page render error
                        int errLen = reader.ReadInt32();
                        byte[] errBytes = ReadExactBytes(reader, errLen);
                        string errMsg = Encoding.UTF8.GetString(errBytes);
                        Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Page {pageNumber} error: {errMsg}");
                        continue;
                    }

                    // Normal page data
                    double width = reader.ReadDouble();
                    double height = reader.ReadDouble();
                    byte[] pngData = ReadExactBytes(reader, dataLength);
                    pages.Add(new PageRenderResult(pageNumber, pngData, width, height));
                }
            }

            // Wait for process exit (with cancellation support)
            using (var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (!proc.WaitForExit(30_000)) // 30s timeout per group
                {
                    Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Worker timeout, killing...");
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }

            // Log worker stderr
            string stderrText = stderrTask.Result;
            if (!string.IsNullOrEmpty(stderrText))
            {
                foreach (var line in stderrText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Debug.WriteLine($"  [MultiProcess][Worker {groupIndex}] {line.Trim()}");
            }

            int expectedPages = endPage - startPage + 1;
            bool success = pages.Count == expectedPages;
            if (success)
            {
                long totalBytes = pages.Sum(p => (long)p.PngBytes.Length);
                Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] DONE — {pages.Count} pages, {totalBytes / 1024} KB");
            }
            else
            {
                Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] WARNING: expected {expectedPages} pages, got {pages.Count}");
            }

            return new GroupRenderResult(groupIndex, pages, success);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] Cancelled");
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"  [MultiProcess][Group {groupIndex}] ERROR: {ex.Message}");
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            return new GroupRenderResult(groupIndex, pages, false);
        }
    }

    // ══════════════════════════════════════════════════════
    //  Helper: read exact number of bytes
    // ══════════════════════════════════════════════════════

    private static byte[] ReadExactBytes(BinaryReader reader, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = reader.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Expected {count} bytes but stream ended after {offset}.");
            offset += read;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

// ══════════════════════════════════════════════════════════
//  Public types
// ══════════════════════════════════════════════════════════

/// <summary>Result for a single rendered page.</summary>
public sealed record PageRenderResult(
    int PageNumber,
    byte[] PngBytes,
    double WidthPoints,
    double HeightPoints
);

/// <summary>Aggregated result from a multi-process render operation.</summary>
public sealed record MultiProcessRenderResult(
    IReadOnlyList<PageRenderResult> Pages,
    bool IsComplete
);

/// <summary>Thrown when the worker executable cannot be located.</summary>
public sealed class WorkerNotFoundException : Exception
{
    public WorkerNotFoundException(string message) : base(message) { }
}

// ══════════════════════════════════════════════════════════
//  Internal types
// ══════════════════════════════════════════════════════════

internal sealed record GroupRenderResult(
    int GroupIndex,
    List<PageRenderResult> Pages,
    bool Success
);
