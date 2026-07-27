using Microsoft.UI.Xaml.Media.Imaging;
using mupdf;
using PDFiumCore;
using pdf_studio.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly ConcurrentDictionary<string, FpdfDocumentT> _pdfDocumentCache = new();

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
                // Hide highlight annotations so they never bake into the
                // rendered bitmap — the viewer draws them itself on an
                // overlay (single source of truth, no double-draw).
                HideHighlightAnnotations(page);
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

            if (!_pdfDocumentCache.TryGetValue(docKey, out var document))
            {
                document = fpdfview.FPDF_LoadDocument(filePath, null);
                if (document == null)
                    throw new Exception($"Failed to open PDF for text: {filePath}");
                _pdfDocumentCache[docKey] = document;
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

            foreach (var kv in _pdfDocumentCache)
            {
                fpdfview.FPDF_CloseDocument(kv.Value);
            }
            _pdfDocumentCache.Clear();
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
        //  Annotations (highlight) & bookmarks (TOC)
        // ════════════════════════════════════════════════════════════════

        private const int FPDF_ANNOT_HIGHLIGHT = 9;
        private const int FPDF_ANNOT_FLAG_HIDDEN = 1 << 1;
        private const int PDFACTION_GOTO = 1;

        /// <summary>
        /// Sets the HIDDEN flag on all highlight annotations of a page so
        /// PDFium does not render them into the bitmap.  Only affects the
        /// transient document used for this render call — the file and any
        /// other document instance are untouched.  Caller must hold
        /// <see cref="_pageLock"/>.
        /// </summary>
        private static void HideHighlightAnnotations(FpdfPageT page)
        {
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (annot == null) continue;
                if (fpdf_annot.FPDFAnnotGetSubtype(annot) == FPDF_ANNOT_HIGHLIGHT)
                    fpdf_annot.FPDFAnnotSetFlags(annot, FPDF_ANNOT_FLAG_HIDDEN);
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }

        // ── Bookmarks (table of contents) ─────────────────────────────

        /// <summary>Loads the document outline as a tree. Empty when the document has no bookmarks.</summary>
        public List<TocItem> GetBookmarks(string filePath)
        {
            var result = new List<TocItem>();
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            if (document == null) return result;
            try
            {
                lock (_pageLock)
                {
                    var root = fpdf_doc.FPDFBookmarkGetFirstChild(document, null);
                    BuildBookmarks(document, root, result);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
            return result;
        }

        private static void BuildBookmarks(
            FpdfDocumentT document, FpdfBookmarkT first, List<TocItem> list)
        {
            var bookmark = first;
            while (bookmark != null)
            {
                var item = new TocItem
                {
                    Title = GetBookmarkTitle(bookmark),
                    PageIndex = GetBookmarkPageIndex(document, bookmark),
                };
                list.Add(item);

                var child = fpdf_doc.FPDFBookmarkGetFirstChild(document, bookmark);
                if (child != null)
                    BuildBookmarks(document, child, item.Children);

                bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(document, bookmark);
            }
        }

        private static string GetBookmarkTitle(FpdfBookmarkT bookmark)
        {
            ulong len = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
            if (len <= 2) return string.Empty;

            var buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, len);
                // UTF-16LE, includes null terminator.
                return Marshal.PtrToStringUni(buffer, (int)len / 2 - 1) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int GetBookmarkPageIndex(FpdfDocumentT document, FpdfBookmarkT bookmark)
        {
            var dest = fpdf_doc.FPDFBookmarkGetDest(document, bookmark);
            if (dest == null)
            {
                var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
                if (action != null && fpdf_doc.FPDFActionGetType(action) == PDFACTION_GOTO)
                    dest = fpdf_doc.FPDFActionGetDest(document, action);
            }
            return dest != null ? fpdf_doc.FPDFDestGetDestPageIndex(document, dest) : -1;
        }

        // ── Highlight annotations: load ───────────────────────────────

        /// <summary>Loads all highlight annotations from the document.</summary>
        public List<PdfHighlight> LoadHighlights(string filePath)
        {
            var result = new List<PdfHighlight>();
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            if (document == null) return result;
            try
            {
                int pageCount = fpdfview.FPDF_GetPageCount(document);
                for (int p = 0; p < pageCount; p++)
                {
                    FpdfPageT page;
                    lock (_pageLock)
                    {
                        page = fpdfview.FPDF_LoadPage(document, p);
                    }
                    if (page == null) continue;
                    try
                    {
                        lock (_pageLock)
                        {
                            int annotCount = fpdf_annot.FPDFPageGetAnnotCount(page);
                            for (int i = 0; i < annotCount; i++)
                            {
                                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                                if (annot == null) continue;
                                try
                                {
                                    if (fpdf_annot.FPDFAnnotGetSubtype(annot) != FPDF_ANNOT_HIGHLIGHT)
                                        continue;

                                    uint r = 0, g = 0, b = 0, a = 0;
                                    if (fpdf_annot.FPDFAnnotGetColor(annot,
                                            FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                                            ref r, ref g, ref b, ref a) == 0)
                                    {
                                        // No /C entry — the producer baked the color into
                                        // the /AP appearance stream (or omitted it entirely).
                                        // Fall back to the de-facto standard highlighter
                                        // yellow instead of the (0,0,0) the call leaves behind.
                                        r = 255; g = 235; b = 59;
                                    }

                                    var rects = new List<PdfRect>();
                                    ulong quadCount = fpdf_annot.FPDFAnnotCountAttachmentPoints(annot);
                                    for (ulong q = 0; q < quadCount; q++)
                                    {
                                        using var quad = new FS_QUADPOINTSF();
                                        if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annot, q, quad) != 0)
                                            rects.Add(QuadToRect(quad));
                                    }

                                    if (rects.Count > 0)
                                    {
                                        result.Add(new PdfHighlight(p, rects,
                                            Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b)));
                                    }
                                }
                                finally
                                {
                                    fpdf_annot.FPDFPageCloseAnnot(annot);
                                }
                            }
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
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
            return result;
        }

        private static PdfRect QuadToRect(FS_QUADPOINTSF quad)
        {
            // QuadPoints order (per PDF spec): top-left, top-right, bottom-left, bottom-right.
            double left = Math.Min(quad.X1, quad.X3);
            double right = Math.Max(quad.X2, quad.X4);
            double top = Math.Max(quad.Y1, quad.Y2);
            double bottom = Math.Min(quad.Y3, quad.Y4);
            return new PdfRect(left, top, right, bottom);
        }

        // ── Highlight annotations: save ───────────────────────────────

        /// <summary>
        /// Rewrites all highlight annotations in the document from
        /// <paramref name="highlights"/> (non-highlight annotations are
        /// preserved) and saves a full copy to <paramref name="tempPath"/>.
        /// The caller is responsible for atomically replacing the original
        /// file with the temp file.  Thread-safe (guarded by _pageLock).
        /// </summary>
        public void SaveHighlightsToTemp(
            string filePath, IReadOnlyList<PdfHighlight> highlights, string tempPath)
        {
            var document = fpdfview.FPDF_LoadDocument(filePath, null);
            if (document == null)
                throw new InvalidOperationException($"无法打开 PDF 文档: {filePath}");
            try
            {
                var byPage = highlights
                    .GroupBy(h => h.PageIndex)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<PdfHighlight>)g.ToList());

                int pageCount = fpdfview.FPDF_GetPageCount(document);
                for (int p = 0; p < pageCount; p++)
                {
                    byPage.TryGetValue(p, out var pageHighlights);

                    FpdfPageT page;
                    lock (_pageLock)
                    {
                        page = fpdfview.FPDF_LoadPage(document, p);
                    }
                    if (page == null) continue;
                    try
                    {
                        lock (_pageLock)
                        {
                            // Remove existing highlight annotations (iterate backwards).
                            for (int i = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; i >= 0; i--)
                            {
                                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                                if (annot == null) continue;
                                int subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                                fpdf_annot.FPDFPageCloseAnnot(annot);
                                if (subtype == FPDF_ANNOT_HIGHLIGHT)
                                    fpdf_annot.FPDFPageRemoveAnnot(page, i);
                            }

                            // Recreate from the in-memory model.
                            if (pageHighlights != null)
                            {
                                foreach (var highlight in pageHighlights)
                                    AppendHighlightAnnot(page, highlight);
                            }
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

                SaveDocumentAsCopy(document, tempPath);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }

        private static void AppendHighlightAnnot(FpdfPageT page, PdfHighlight highlight)
        {
            var annot = fpdf_annot.FPDFPageCreateAnnot(page, FPDF_ANNOT_HIGHLIGHT);
            if (annot == null) return;
            try
            {
                fpdf_annot.FPDFAnnotSetColor(annot,
                    FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                    highlight.Color.R, highlight.Color.G, highlight.Color.B, 255);

                double left = highlight.Rects.Min(r => r.L);
                double right = highlight.Rects.Max(r => r.R);
                double top = highlight.Rects.Max(r => r.T);
                double bottom = highlight.Rects.Min(r => r.B);

                using var rect = new FS_RECTF_
                {
                    Left = (float)left,
                    Top = (float)top,
                    Right = (float)right,
                    Bottom = (float)bottom,
                };
                fpdf_annot.FPDFAnnotSetRect(annot, rect);

                foreach (var r in highlight.Rects)
                {
                    using var quad = new FS_QUADPOINTSF
                    {
                        X1 = (float)r.L, Y1 = (float)r.T, // top-left
                        X2 = (float)r.R, Y2 = (float)r.T, // top-right
                        X3 = (float)r.L, Y3 = (float)r.B, // bottom-left
                        X4 = (float)r.R, Y4 = (float)r.B, // bottom-right
                    };
                    fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, quad);
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }

        private static void SaveDocumentAsCopy(FpdfDocumentT document, string tempPath)
        {
            using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var fileWrite = new FPDF_FILEWRITE_();

            fileWrite.WriteBlock = (pThis, pData, size) =>
            {
                int length = (int)size;
                var bytes = new byte[length];
                Marshal.Copy(pData, bytes, 0, length);
                stream.Write(bytes, 0, length);
                return 1;
            };

            int ok;
            lock (_pageLock)
            {
                ok = fpdf_save.FPDF_SaveAsCopy(document, fileWrite, 0);
            }
            if (ok == 0)
                throw new InvalidOperationException("FPDF_SaveAsCopy 保存失败");
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
