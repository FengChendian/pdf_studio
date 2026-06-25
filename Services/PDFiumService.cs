using PDFiumCore;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace pdf_studio.Services
{
    internal class PDFiumService : IDisposable
    {
        /// <summary>
        /// Document pool: filePath → array of independent document instances.
        /// Each array slot acts as a "thread slot" — PDFium requires a separate
        /// document handle per concurrent thread.
        /// </summary>
        private readonly Dictionary<string, FpdfDocumentT[]> _documentPool = new();
        private readonly object _poolLock = new();
        private bool _disposed;

        public PDFiumService()
        {
            fpdfview.FPDF_InitLibrary();
        }

        // ════════════════════════════════════════════════════════════════
        //  Single-page render (opens a temporary document)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders a single page of a PDF directly into a <see cref="WriteableBitmap"/>.
        /// Opens and closes the document within the call — suitable for one-off renders.
        /// For batch rendering use <see cref="RenderPagesToWriteableBitmaps"/>.
        /// </summary>
        /// <param name="pageNumber">Zero-based page index (0 = first page).</param>
        public WriteableBitmap RenderPageToWriteableBitmap(
            string filePath,
            int pageNumber = 0,
            float scale = 1f)
        {
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                return RenderSinglePage(document, pageNumber, scale);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Batch parallel render — returns Dictionary
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders multiple pages in parallel using a pool of independent document
        /// instances. Results are returned as a dictionary keyed by page number.
        /// </summary>
        /// <param name="filePath">Path to the PDF file.</param>
        /// <param name="pageNumbers">Zero-based page indices to render.</param>
        /// <param name="scale">Render scale factor (1.0 = 72 DPI).</param>
        /// <returns>Dictionary mapping each requested page number to its rendered bitmap.</returns>
        public Dictionary<int, WriteableBitmap> RenderPagesToWriteableBitmaps(
            string filePath,
            int[] pageNumbers,
            float scale = 1f)
        {
            var output = new WriteableBitmap[pageNumbers.Length];
            RenderPagesInternal(filePath, pageNumbers, output, scale);

            var result = new Dictionary<int, WriteableBitmap>(pageNumbers.Length);
            for (int i = 0; i < pageNumbers.Length; i++)
                result[pageNumbers[i]] = output[i];
            return result;
        }

        // ════════════════════════════════════════════════════════════════
        //  Batch parallel render — fills a pre-allocated array
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders multiple pages in parallel, writing results directly into a
        /// pre-allocated <see cref="WriteableBitmap"/> array in the same order
        /// as <paramref name="pageNumbers"/>.
        /// </summary>
        /// <param name="filePath">Path to the PDF file.</param>
        /// <param name="pageNumbers">Zero-based page indices to render.</param>
        /// <param name="outputBitmaps">
        /// Pre-allocated array to receive the rendered bitmaps.
        /// Must be at least as long as <paramref name="pageNumbers"/>.
        /// </param>
        /// <param name="scale">Render scale factor (1.0 = 72 DPI).</param>
        public void RenderPagesToWriteableBitmaps(
            string filePath,
            int[] pageNumbers,
            WriteableBitmap[] outputBitmaps,
            float scale = 1f)
        {
            if (outputBitmaps.Length < pageNumbers.Length)
                throw new ArgumentException(
                    $"Output array size ({outputBitmaps.Length}) is smaller than page count ({pageNumbers.Length}).",
                    nameof(outputBitmaps));

            RenderPagesInternal(filePath, pageNumbers, outputBitmaps, scale);
        }

        // ════════════════════════════════════════════════════════════════
        //  Core parallel rendering
        // ════════════════════════════════════════════════════════════════

        private void RenderPagesInternal(
            string filePath,
            int[] pageNumbers,
            WriteableBitmap[] output,
            float scale)
        {
            if (pageNumbers.Length == 0) return;

            // Determine parallelism: one document instance per concurrent thread.
            int threadCount = Math.Min(pageNumbers.Length, Environment.ProcessorCount);
            threadCount = Math.Max(threadCount, 1);

            var documents = GetOrCreateDocumentPool(filePath, threadCount);

            // Thread-local doc slot assignment via Interlocked — each worker thread
            // gets its own document instance so PDFium calls stay thread-safe.
            int slotCounter = -1;

            Parallel.For(0, pageNumbers.Length,
                new ParallelOptions { MaxDegreeOfParallelism = threadCount },
                () => Interlocked.Increment(ref slotCounter) % documents.Length,
                (i, loopState, docIndex) =>
                {
                    output[i] = RenderSinglePage(documents[docIndex], pageNumbers[i], scale);
                    return docIndex;
                },
                _ => { });
        }

        // ════════════════════════════════════════════════════════════════
        //  Per-page render core (document must be already open)
        // ════════════════════════════════════════════════════════════════

        private static WriteableBitmap RenderSinglePage(
            FpdfDocumentT document,
            int pageNumber,
            float scale)
        {
            double pageWidth = 0;
            double pageHeight = 0;

            var page = fpdfview.FPDF_LoadPage(document, pageNumber);
            try
            {
                fpdfview.FPDF_GetPageSizeByIndex(document, pageNumber, ref pageWidth, ref pageHeight);

                int width = (int)pageWidth;
                int height = (int)pageHeight;

                var bitmap = fpdfview.FPDFBitmapCreateEx(
                    width, height,
                    (int)FPDFBitmapFormat.BGRA,
                    IntPtr.Zero, 0);

                if (bitmap == null)
                    throw new Exception("Failed to create a PDFium bitmap object.");

                try
                {
                    // White background.
                    uint color = uint.MaxValue;
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, color);

                    using var matrix = new FS_MATRIX_();
                    matrix.A = scale;
                    matrix.B = 0;
                    matrix.C = 0;
                    matrix.D = scale;
                    matrix.E = 0;
                    matrix.F = 0;

                    fpdfview.FPDF_RenderPageBitmapWithMatrix(
                        bitmap, page, matrix, null,
                        (int)RenderFlags.RenderAnnotations);

                    var scan0 = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    int rowBytes = width * 4; // BGRA = 4 bytes per pixel

                    var writeableBitmap = new WriteableBitmap(width, height);

                    unsafe
                    {
                        byte* src = (byte*)scan0;
                        using var pixelStream = writeableBitmap.PixelBuffer.AsStream();

                        if (stride == rowBytes)
                        {
                            // Rows are tightly packed — copy everything in one pass.
                            pixelStream.Write(new ReadOnlySpan<byte>(src, height * rowBytes));
                        }
                        else
                        {
                            // Stride includes padding — copy row by row.
                            for (int y = 0; y < height; y++)
                            {
                                pixelStream.Write(new ReadOnlySpan<byte>(src + y * stride, rowBytes));
                            }
                        }
                    }

                    writeableBitmap.Invalidate();
                    return writeableBitmap;
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Document pool management
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensures at least <paramref name="count"/> independent document instances
        /// are available for <paramref name="filePath"/>. Grows the pool if needed.
        /// </summary>
        private FpdfDocumentT[] GetOrCreateDocumentPool(string filePath, int count)
        {
            lock (_poolLock)
            {
                if (_documentPool.TryGetValue(filePath, out var existing))
                {
                    if (existing.Length >= count)
                        return existing;

                    // Grow the pool.
                    var expanded = new FpdfDocumentT[count];
                    Array.Copy(existing, expanded, existing.Length);
                    for (int i = existing.Length; i < count; i++)
                        expanded[i] = fpdfview.FPDF_LoadDocument(filePath, null);
                    _documentPool[filePath] = expanded;
                    return expanded;
                }
                else
                {
                    var pool = new FpdfDocumentT[count];
                    for (int i = 0; i < count; i++)
                        pool[i] = fpdfview.FPDF_LoadDocument(filePath, null);
                    _documentPool[filePath] = pool;
                    return pool;
                }
            }
        }

        /// <summary>
        /// Closes all cached document instances for the given file.
        /// Subsequent renders will re-open documents as needed.
        /// </summary>
        public void EvictDocument(string filePath)
        {
            lock (_poolLock)
            {
                if (_documentPool.Remove(filePath, out var documents))
                {
                    foreach (var doc in documents)
                        fpdfview.FPDF_CloseDocument(doc);
                }
            }
        }

        /// <summary>
        /// Closes all cached document instances across all files.
        /// </summary>
        public void EvictAllDocuments()
        {
            lock (_poolLock)
            {
                foreach (var (_, documents) in _documentPool)
                {
                    foreach (var doc in documents)
                        fpdfview.FPDF_CloseDocument(doc);
                }
                _documentPool.Clear();
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  IDisposable
        // ════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            EvictAllDocuments();
            fpdfview.FPDF_DestroyLibrary();
        }
    }
}
