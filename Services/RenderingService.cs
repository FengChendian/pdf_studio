using Microsoft.UI.Xaml.Media.Imaging;
using MuPDF.NET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace pdf_studio.Services
{
    public class RenderingService : IDisposable
    {
        //private readonly ConcurrentDictionary<string, Document> _documents = new();
        private readonly Document _documents;
        public readonly string FileName;
        public readonly int _PageCount;

        //public string fileName { get => FileName; }

        public RenderingService(string fileName)
        {
            _documents = new Document(fileName);
            FileName = fileName;
            _PageCount = _documents.PageCount;
        }

        public Pixmap RenderPage(int pageNumber, int dpi = 72)
        {
            var page = _documents.LoadPage(pageNumber);
            return page.GetPixmap(dpi: dpi, colorSpace: "RGB", annots: false);
        }

        /// <summary>CPU 密集型渲染，返回 PNG 字节，可在后台线程调用</summary>
        public byte[] RenderPageToBytes(int pageNumber, int dpi = 72)
        {
            var page = _documents.LoadPage(pageNumber);
            var pixmap = page.GetPixmap(dpi: dpi, colorSpace: "RGB", annots: false);
            return pixmap.ToBytes();
        }

        /// <summary>创建 BitmapImage（需在 UI 线程调用）</summary>
        public async Task<BitmapImage> RenderPageToBitmap(int pageNumber, int dpi = 72)
        {
            var page = _documents.LoadPage(pageNumber);

            //var matrix = new Matrix(zoom, zoom);

            var pixmap = page.GetPixmap(dpi: dpi, colorSpace: "RGB", annots: false);


                //var image = ConvertSafe(pixmap);
                var png = pixmap.ToBytes();
                var bitmap = new BitmapImage();
                using (InMemoryRandomAccessStream stream = new())
                {
                    // 将 byte[] 写入随机访问流
                    await stream.WriteAsync(png.AsBuffer());
                    stream.Seek(0); // 重置流位置到开头

                    // 设置源
                    await bitmap.SetSourceAsync(stream);
                }
                return bitmap;

        }

        public int PageCount => _PageCount;

        public void Dispose()
        {
            _documents.Dispose();
        }
    }
}
