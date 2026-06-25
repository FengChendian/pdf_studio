using Microsoft.UI.Xaml.Media.Imaging;
using mupdf;
using PDFiumCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace pdf_studio.Services
{
    internal class PDFiumService : IDisposable
    {
        /// <summary>
        /// Holds a document handle together with the native memory buffer that
        /// backs it. PDFium reads directly from this buffer, so it must remain
        /// alive and unpinned for the document's entire lifetime.
        /// </summary>
        private sealed class DocumentSlot
        {
            public FpdfDocumentT Document;
            public IntPtr Buffer;

            public void Dispose()
            {
                fpdfview.FPDF_CloseDocument(Document);
                Marshal.FreeHGlobal(Buffer);
            }
        }

        /// <summary>
        /// Document pool: filePath → array of independent document instances.
        /// Each slot is backed by its own copy of the file bytes, allocated in
        /// native memory so PDFium can read from it without filesystem contention.
        /// </summary>
        private readonly Dictionary<string, DocumentSlot[]> _documentPool = new();
        private readonly object _poolLock = new();
        private bool _disposed;

        public PDFiumService()
        {
            fpdfview.FPDF_InitLibrary();
        }

        // ════════════════════════════════════════════════════════════════
        //  Document metadata (lightweight — opens/closes a temp document)
        // ════════════════════════════════════════════════════════════════

        public int GetPageCount(string filePath)
        {
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                return fpdfview.FPDF_GetPageCount(document);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        public (double Width, double Height) GetPageSize(string filePath, int pageNumber)
        {
            double width = 0, height = 0;
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                fpdfview.FPDF_GetPageSizeByIndex(document, pageNumber, ref width, ref height);
                return (width, height);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Thread-safe raw rendering (NO UI dependency — safe on any thread)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders a single page into a raw byte buffer.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        public (int width, int height, byte[] pixels) RenderPageToRawBuffer(
            string filePath,
            int pageNumber = 0,
            float scale = 1f)
        {
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                return RenderSinglePageToBuffer(document, pageNumber, scale);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        /// <summary>
        /// Renders multiple pages in parallel into raw byte buffers.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        public (int width, int height, byte[] pixels)[] RenderPagesToRawBuffers(
            string filePath,
            int[] pageNumbers,
            float scale = 1f)
        {
            var rawBuffers = new (int width, int height, byte[] pixels)[pageNumbers.Length];
            RenderPagesToRawBuffersInternal(filePath, pageNumbers, rawBuffers, scale);
            return rawBuffers;
        }

        // ════════════════════════════════════════════════════════════════
        //  Single-page convenience (UI-thread only — creates WriteableBitmap)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders a single page directly into a <see cref="WriteableBitmap"/>.
        /// Must be called from the UI Thread.
        /// </summary>
        public WriteableBitmap RenderPageToWriteableBitmap(
            string filePath,
            int pageNumber = 0,
            float scale = 1f)
        {
            var (width, height, pixels) = RenderPageToRawBuffer(filePath, pageNumber, scale);
            return CreateWriteableBitmapFromRawBuffer(width, height, pixels);
        }

        // ════════════════════════════════════════════════════════════════
        //  Batch parallel convenience (UI-thread only — creates WriteableBitmaps)
        // ════════════════════════════════════════════════════════════════

        public Dictionary<int, WriteableBitmap> RenderPagesToWriteableBitmaps(
            string filePath,
            int[] pageNumbers,
            float scale = 1f)
        {
            var rawBuffers = RenderPagesToRawBuffers(filePath, pageNumbers, scale);
            var result = new Dictionary<int, WriteableBitmap>(pageNumbers.Length);
            for (int i = 0; i < pageNumbers.Length; i++)
            {
                var (w, h, p) = rawBuffers[i];
                result[pageNumbers[i]] = CreateWriteableBitmapFromRawBuffer(w, h, p);
            }
            return result;
        }

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

            var rawBuffers = RenderPagesToRawBuffers(filePath, pageNumbers, scale);
            for (int i = 0; i < pageNumbers.Length; i++)
            {
                var (w, h, p) = rawBuffers[i];
                outputBitmaps[i] = CreateWriteableBitmapFromRawBuffer(w, h, p);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Core parallel rendering (thread-safe — raw bytes only, no UI types)
        // ════════════════════════════════════════════════════════════════

        private void RenderPagesToRawBuffersInternal(
            string filePath,
            int[] pageNumbers,
            (int width, int height, byte[] pixels)[] output,
            float scale)
        {
            if (pageNumbers.Length == 0) return;

            int threadCount = Math.Min(pageNumbers.Length, 2);
            threadCount = Math.Max(threadCount, 1);

            var documents = GetOrCreateDocumentPool(filePath, threadCount);

            // Manually distribute pages across N tasks, each with its own
            // dedicated document instance. No contention: every page index is
            // written by exactly one worker, every worker owns exactly one
            // document handle.
            int pagesPerThread = pageNumbers.Length / threadCount;
            int remainder = pageNumbers.Length % threadCount;

            var tasks = new Task[threadCount];
            int offset = 0;
            for (int t = 0; t < threadCount; t++)
            {
                int threadIndex = t;
                int start = offset;
                int count = pagesPerThread + (t < remainder ? 1 : 0);
                offset += count;

                int localStart = start;
                int localCount = count;

                tasks[t] = Task.Run(() =>
                {
                    var doc = documents[threadIndex].Document;
                    for (int j = 0; j < localCount; j++)
                    {
                        int pageIdx = localStart + j; // 使用快照
                        output[pageIdx] = RenderSinglePageToBuffer(
                            doc, pageNumbers[pageIdx], scale);
                    }
                });
            }

            Task.WaitAll(tasks);
        }

        // ════════════════════════════════════════════════════════════════
        //  Per-page raw rendering (Thread-agnostic)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders a single page into a raw byte array instead of a UI element.
        /// This is safe to run on background ThreadPool threads.
        /// </summary>
        /// <remarks>
        /// PDFium is not internally thread-safe — even on different document handles,
        /// concurrent calls to FPDF_LoadPage/FPDF_ClosePage can race on global state.
        /// </remarks>
        private static readonly object _pageLock = new();

        internal static (int width, int height, byte[] pixels) RenderSinglePageToBuffer(
            FpdfDocumentT document,
            int pageNumber,
            float scale)
        {
            double pageWidth = 0;
            double pageHeight = 0;

            FpdfPageT page;
            lock (_pageLock)
            {
                page = fpdfview.FPDF_LoadPage(document, pageNumber);
            }
            try
            {
                lock (_pageLock)
                {
                    fpdfview.FPDF_GetPageSizeByIndex(document, pageNumber, ref pageWidth, ref pageHeight);
                }

                int width = (int)(pageWidth * scale);
                int height = (int)(pageHeight * scale);

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
                    using var clip = new FS_RECTF_();
                    matrix.A = scale;
                    matrix.B = 0;
                    matrix.C = 0;
                    matrix.D = scale;
                    matrix.E = 0;
                    matrix.F = 0;

                    clip.Left = 0;
                    clip.Top = 0;
                    clip.Right = width;
                    clip.Bottom = height;

                    lock (_pageLock)
                    {
                        fpdfview.FPDF_RenderPageBitmapWithMatrix(
                            bitmap, page, matrix, clip,
                            (int)RenderFlags.RenderAnnotations);
                    }

                    var scan0 = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    int rowBytes = width * 4; // BGRA = 4 bytes per pixel

                    // Extract memory locally inside the thread
                    byte[] pixelData = new byte[height * rowBytes];

                    unsafe
                    {
                        byte* src = (byte*)scan0;

                        if (stride == rowBytes)
                        {
                            // Rows are tightly packed — copy everything in one pass.
                            new ReadOnlySpan<byte>(src, height * rowBytes).CopyTo(pixelData);
                        }
                        else
                        {
                            // Stride includes padding — copy row by row.
                            for (int y = 0; y < height; y++)
                            {
                                new ReadOnlySpan<byte>(src + y * stride, rowBytes)
                                    .CopyTo(new Span<byte>(pixelData, y * rowBytes, rowBytes));
                            }
                        }
                    }

                    return (width, height, pixelData);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                lock (_pageLock)
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  UI Helper (must be called on the UI thread)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a <see cref="WriteableBitmap"/> from raw BGRA pixel data.
        /// Must be called from the UI thread.
        /// </summary>
        public static WriteableBitmap CreateWriteableBitmapFromRawBuffer(int width, int height, byte[] pixelData)
        {
            var writeableBitmap = new WriteableBitmap(width, height);
            using var pixelStream = writeableBitmap.PixelBuffer.AsStream();
            pixelStream.Write(pixelData, 0, pixelData.Length);
            writeableBitmap.Invalidate();
            return writeableBitmap;
        }

        // ════════════════════════════════════════════════════════════════
        //  Document pool management
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets or creates a pool of independent document instances, each backed
        /// by its own copy of the file in native memory. This eliminates
        /// filesystem-level contention when multiple threads render concurrently.
        /// </summary>
        private DocumentSlot[] GetOrCreateDocumentPool(string filePath, int count)
        {
            lock (_poolLock)
            {
                if (_documentPool.TryGetValue(filePath, out var existing))
                {
                    if (existing.Length >= count)
                        return existing;

                    // Grow the pool — only create the additional slots.
                    var expanded = new DocumentSlot[count];
                    Array.Copy(existing, expanded, existing.Length);
                    for (int i = existing.Length; i < count; i++)
                        expanded[i] = CreateDocumentSlot(filePath);
                    _documentPool[filePath] = expanded;
                    return expanded;
                }
                else
                {
                    var pool = new DocumentSlot[count];
                    for (int i = 0; i < count; i++)
                        pool[i] = CreateDocumentSlot(filePath);
                    _documentPool[filePath] = pool;
                    return pool;
                }
            }
        }

        /// <summary>
        /// Reads the file once, copies the bytes into native memory, then opens
        /// a PDFium document from that memory. The native buffer stays alive
        /// until <see cref="DocumentSlot.Dispose"/> is called.
        /// </summary>
        private static DocumentSlot CreateDocumentSlot(string filePath)
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            int size = fileBytes.Length;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            Marshal.Copy(fileBytes, 0, buffer, size);

            var document = fpdfview.FPDF_LoadMemDocument(buffer, size, null);
            return new DocumentSlot { Document = document, Buffer = buffer };
        }

        public void EvictDocument(string filePath)
        {
            lock (_poolLock)
            {
                if (_documentPool.Remove(filePath, out var slots))
                {
                    foreach (var slot in slots)
                        slot.Dispose();
                }
            }
        }

        public void EvictAllDocuments()
        {
            lock (_poolLock)
            {
                foreach (var (_, slots) in _documentPool)
                {
                    foreach (var slot in slots)
                        slot.Dispose();
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