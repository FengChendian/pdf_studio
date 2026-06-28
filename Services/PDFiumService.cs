using Microsoft.UI.Xaml.Media.Imaging;
using mupdf;
using PDFiumCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;

namespace pdf_studio.Services
{
    public class PDFiumService : IDisposable
    {
        private bool _disposed;

        // ── Library lifecycle (reference-counted — init once, destroy on last Dispose) ──
        private static int _libraryRefCount;
        private static readonly object _libraryLock = new();

        // ── Text-page cache ──────────────────────────────────────────
        // Key: filePath|pageIndex  — simple string key to avoid holding
        // document/page handles across calls.
        private readonly ConcurrentDictionary<string, FpdfTextpageT> _textPageCache = new();
        private readonly ConcurrentDictionary<string, FpdfDocumentT> _textDocCache = new();

        public PDFiumService()
        {
            lock (_libraryLock)
            {
                if (_libraryRefCount == 0)
                    fpdfview.FPDF_InitLibrary();
                _libraryRefCount++;
            }
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
        /// Renders multiple pages into raw byte buffers.
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
        //  Batch convenience (UI-thread only — creates WriteableBitmaps)
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
        //  Core rendering (raw bytes only, no UI types)
        // ════════════════════════════════════════════════════════════════

        private void RenderPagesToRawBuffersInternal(
            string filePath,
            int[] pageNumbers,
            (int width, int height, byte[] pixels)[] output,
            float scale)
        {
            if (pageNumbers.Length == 0) return;

            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                for (int i = 0; i < pageNumbers.Length; i++)
                {
                    output[i] = RenderSinglePageToBuffer(document, pageNumbers[i], scale);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
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
        //  Text page APIs (text selection / highlighting)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loads a text page for the given PDF page.  Opens the document
        /// on first call and caches both the document handle and the text
        /// page.  Call <see cref="ReleaseTextPage"/> when done.
        /// </summary>
        public FpdfTextpageT LoadTextPage(string filePath, int pageIndex)
        {
            string docKey = filePath;
            string pageKey = $"{filePath}|{pageIndex}";

            if (_textPageCache.TryGetValue(pageKey, out var cached))
                return cached;

            if (!_textDocCache.TryGetValue(docKey, out var document))
            {
                document = fpdfview.FPDF_LoadDocument(filePath, null);
                if (document == null)
                    throw new Exception($"Failed to open PDF for text: {filePath}");
                _textDocCache[docKey] = document;
            }

            FpdfTextpageT textPage;
            lock (_pageLock)
            {
                var page = fpdfview.FPDF_LoadPage(document, pageIndex);
                try
                {
                    textPage = fpdf_text.FPDFTextLoadPage(page);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            _textPageCache[pageKey] = textPage;
            return textPage;
        }

        /// <summary>
        /// Releases a single cached text page.
        /// </summary>
        public void ReleaseTextPage(string filePath, int pageIndex)
        {
            string pageKey = $"{filePath}|{pageIndex}";
            if (_textPageCache.TryRemove(pageKey, out var textPage))
            {
                fpdf_text.FPDFTextClosePage(textPage);
            }
        }

        /// <summary>
        /// Releases all cached text pages and document handles.
        /// Called when the PDF viewer page is unloaded.
        /// </summary>
        public void ReleaseAllTextPages()
        {
            foreach (var kv in _textPageCache)
            {
                fpdf_text.FPDFTextClosePage(kv.Value);
            }
            _textPageCache.Clear();

            foreach (var kv in _textDocCache)
            {
                fpdfview.FPDF_CloseDocument(kv.Value);
            }
            _textDocCache.Clear();
        }

        /// <summary>
        /// Releases all text pages for a given file (keeps the document
        /// open if it may be reused).
        /// </summary>
        public void ReleaseTextPagesForFile(string filePath)
        {
            var keysToRemove = new List<string>();
            foreach (var key in _textPageCache.Keys)
            {
                if (key.StartsWith(filePath + "|"))
                    keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
            {
                if (_textPageCache.TryRemove(key, out var textPage))
                    fpdf_text.FPDFTextClosePage(textPage);
            }
        }

        // ── Lightweight wrappers (delegate directly to fpdf_text) ─────

        public static int TextGetCharIndexAtPos(FpdfTextpageT textPage, double x, double y, double xTol, double yTol)
            => fpdf_text.FPDFTextGetCharIndexAtPos(textPage, x, y, xTol, yTol);

        public static int TextCountRects(FpdfTextpageT textPage, int startIndex, int count)
            => fpdf_text.FPDFTextCountRects(textPage, startIndex, count);

        public static bool TextGetRect(FpdfTextpageT textPage, int rectIndex, out double left, out double top, out double right, out double bottom)
        {
            left = top = right = bottom = 0;
            return fpdf_text.FPDFTextGetRect(textPage, rectIndex, ref left, ref top, ref right, ref bottom) != 0;
        }

        public static int TextCountChars(FpdfTextpageT textPage)
            => fpdf_text.FPDFTextCountChars(textPage);

        /// <summary>
        /// Extracts the selected text (UTF-16) and returns it as a .NET string.
        /// </summary>
        public static unsafe string TextGetText(FpdfTextpageT textPage, int startIndex, int count)
        {
            if (count <= 0) return string.Empty;

            var buffer = new ushort[count + 1]; // +1 for null terminator
            int written;
            fixed (ushort* ptr = buffer)
            {
                written = fpdf_text.FPDFTextGetText(textPage, startIndex, count, ref ptr[0]);
            }

            if (written <= 0) return string.Empty;

            // written includes the null terminator
            int charCount = written - 1;
            if (charCount <= 0) return string.Empty;

            return new string(
                Array.ConvertAll(buffer.AsSpan(0, charCount).ToArray(), u => (char)u));
        }

        /// <summary>
        /// Gets page dimensions in PDF points (1/72 inch) from an
        /// already-open document.
        /// </summary>
        public static (double Width, double Height) GetPageSizePt(FpdfDocumentT document, int pageIndex)
        {
            double w = 0, h = 0;
            fpdfview.FPDF_GetPageSizeByIndex(document, pageIndex, ref w, ref h);
            return (w, h);
        }

        // ════════════════════════════════════════════════════════════════
        //  Page bounding-box (CropBox / MediaBox origins)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the full bounding box (left, bottom, right, top) for every
        /// page in the document.  Uses the CropBox when present; falls back
        /// to the MediaBox.  The origin (<c>left</c>, <c>bottom</c>) is
        /// essential for correct coordinate mapping when the visible page
        /// area does not start at (0, 0).
        /// </summary>
        public (double Left, double Bottom, double Right, double Top)[] GetAllPageBBoxes(
            string filePath)
        {
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            try
            {
                int count = fpdfview.FPDF_GetPageCount(document);
                var result = new (double, double, double, double)[count];
                for (int i = 0; i < count; i++)
                {
                    FpdfPageT page;
                    lock (_pageLock)
                    {
                        page = fpdfview.FPDF_LoadPage(document, i);
                    }
                    try
                    {
                        float left = 0, bottom = 0, right = 0, top = 0;
                        if (fpdf_transformpage.FPDFPageGetCropBox(page, ref left, ref bottom,
                                ref right, ref top) == 0)
                        {
                            fpdf_transformpage.FPDFPageGetMediaBox(page, ref left, ref bottom,
                                ref right, ref top);
                        }
                        result[i] = (left, bottom, right, top);
                    }
                    finally
                    {
                        lock (_pageLock)
                        {
                            fpdfview.FPDF_ClosePage(page);
                        }
                    }
                }
                return result;
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  IDisposable
        // ════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseAllTextPages();

            lock (_libraryLock)
            {
                _libraryRefCount--;
                if (_libraryRefCount == 0)
                    fpdfview.FPDF_DestroyLibrary();
            }
        }
    }
}