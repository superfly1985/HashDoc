using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace HashDoc
{
    /// <summary>
    /// PDF 渲染后端。翻页 / 缩放这套界面只写一份，后端可替换（现在用 Windows 自带引擎，
    /// 将来若要支持 Win7，再加一个 pdfium 实现即可，上层一行不用改）。
    /// </summary>
    public interface IPdfRender : IDisposable
    {
        int PageCount { get; }
        /// <summary>页面尺寸（点，1pt = 1/72 英寸）。</summary>
        SizeF PageSize(int index);
        /// <summary>把第 index 页渲染成位图，宽度为 targetWidth 像素。</summary>
        Bitmap Render(int index, int targetWidth);
    }

    public static class PdfRenderFactory
    {
        /// <summary>
        /// 尝试打开 PDF。成功返回渲染器；失败返回 null 并给出可读原因——
        /// 预览区据此降级为提示文字，绝不让异常冒到界面上。
        /// </summary>
        public static IPdfRender TryLoad(string path, out string error)
        {
            error = null;
            try
            {
                return WindowsPdfRender.Load(path);
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return null;
            }
        }

        /// <summary>把底层异常翻译成用户看得懂的一句话。</summary>
        static string Describe(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;

            string t = e.GetType().Name;
            if (t == "TypeLoadException" || t == "FileNotFoundException" ||
                t == "DllNotFoundException" || t == "MissingMethodException")
                return "当前系统没有 PDF 渲染组件（需要 Windows 10 及以上）";

            string msg = e.Message ?? "";
            if (msg.Length > 80) msg = msg.Substring(0, 80) + "…";
            return string.IsNullOrEmpty(msg) ? "无法解析该 PDF" : msg;
        }
    }

    /// <summary>
    /// 用 Windows 自带的 PDF 引擎（Windows.Data.Pdf）渲染，不额外携带任何 DLL。
    /// 读取走内存流而不是 StorageFile，避免网络共享（UNC）路径打不开的老问题。
    /// 所有方法都是同步阻塞的——调用方负责放到后台线程执行。
    /// </summary>
    internal sealed class WindowsPdfRender : IPdfRender
    {
        PdfDocument _doc;
        readonly object _gate = new object();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WindowsPdfRender Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            // 用 DataWriter 灌流：AsStreamForWrite 的包装流 Dispose 时会把底层 RAS 一起关掉，
            // 而 PdfDocument 之后还要从它读，关早了就报"该对象已关闭"(0x80000013)。
            var ras = new InMemoryRandomAccessStream();
            var writer = new DataWriter(ras);
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            writer.DetachStream();      // 断开后才 Dispose，底层流保持打开
            writer.Dispose();
            ras.Seek(0);

            var doc = PdfDocument.LoadFromStreamAsync(ras)
                .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

            return new WindowsPdfRender { _doc = doc };
        }

        public int PageCount
        {
            get { return _doc == null ? 0 : (int)_doc.PageCount; }
        }

        public SizeF PageSize(int index)
        {
            lock (_gate)
            {
                var page = _doc.GetPage((uint)index);
                return new SizeF((float)page.Size.Width, (float)page.Size.Height);
            }
        }

        public Bitmap Render(int index, int targetWidth)
        {
            lock (_gate)
            {
                // 页面对象是 WinRT 对象，由 GC 释放；文档从内存流加载，不占文件句柄
                var page = _doc.GetPage((uint)index);

                using (var output = new InMemoryRandomAccessStream())
                {
                    var opt = new PdfPageRenderOptions();
                    if (targetWidth > 0)
                        opt.DestinationWidth = (uint)Math.Min(targetWidth, 8000);

                    page.RenderToStreamAsync(output, opt)
                        .AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

                    output.Seek(0);
                    using (var src = output.AsStreamForRead())
                    using (var ms = new MemoryStream())
                    {
                        src.CopyTo(ms);
                        ms.Position = 0;
                        using (var tmp = Image.FromStream(ms))
                            return new Bitmap(tmp);   // 脱离流，避免 GDI+ 依赖流生命周期
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) { _doc = null; }
        }
    }
}
