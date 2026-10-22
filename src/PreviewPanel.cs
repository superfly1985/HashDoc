using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 右侧预览：标题一行、预览区（弹性，PDF 时底部带翻页/缩放条）、属性清单（固定高、可滚动）、操作按钮一行。
    /// 全部用 TableLayoutPanel 分行，行高固定值集中在一处，便于整体对齐。
    /// </summary>
    public class PreviewPanel : Panel
    {
        const int TitleHeight = 34;
        const int PropsHeight = 226;
        const int ButtonRowHeight = 42;
        const int PdfBarHeight = 30;

        static readonly string[] ZoomItems = { "适应窗口", "100%", "150%", "200%" };

        readonly Label _title = new Label
        {
            Font = UiUtil.Font9B,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        readonly Panel _picHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Visible = false,
        };
        readonly PictureBox _pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
        };
        readonly Label _ph = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
            Font = UiUtil.Font9,
        };
        readonly ListView _props = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiUtil.Font9,
            MultiSelect = false,
        };
        readonly Button _btnOpen, _btnFolder, _btnEdit;
        readonly Button _btnPrev, _btnNext;
        readonly Label _lblPage;
        readonly ComboBox _cbZoom;
        readonly FlowLayoutPanel _pdfBar;
        readonly TableLayoutPanel _previewLayout;
        readonly Timer _resizeTimer = new Timer { Interval = 260 };
        readonly Action<FileRecord> _onEdit;

        FileRecord _rec;
        Image _img;
        MemoryStream _imgStream;

        IPdfRender _pdf;
        int _pdfPage;
        float _renderScale = 1.0f;   // 渲染像素 / 请求宽度，首次渲染后按系统 DPI 自动校准
        int _loadSeq;      // 快速切换选中项时丢弃过期的加载结果
        int _renderSeq;    // 快速翻页/缩放时丢弃过期的渲染结果

        public PreviewPanel(Action<FileRecord> onEdit)
        {
            _onEdit = onEdit;
            Font = UiUtil.Font9;
            BackColor = Color.White;

            _props.Columns.Add("属性", 92);
            _props.Columns.Add("值", 200);

            // ---- 标题（下边一条细线，与图片区分开）----
            var titleHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
            titleHost.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.DrawLine(Pens.Gainsboro, 0, titleHost.Height - 1, titleHost.Width, titleHost.Height - 1);
            };
            _title.Padding = new Padding(2, 0, 2, 0);
            titleHost.Controls.Add(_title);

            // ---- PDF 翻页/缩放条（默认隐藏，行高收成 0）----
            _btnPrev = UiUtil.Btn("上一页", 58);
            _btnPrev.Margin = new Padding(0, 0, 4, 0);
            _btnPrev.Click += delegate { GoPage(-1); };
            _btnNext = UiUtil.Btn("下一页", 58);
            _btnNext.Margin = new Padding(0, 0, 8, 0);
            _btnNext.Click += delegate { GoPage(1); };
            _lblPage = new Label
            {
                AutoSize = false,
                Width = 62,
                Height = 26,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = UiUtil.Font9,
                Margin = new Padding(0, 0, 8, 0),
                Text = "1 / 1",
            };
            _cbZoom = UiUtil.Combo(96);
            _cbZoom.Items.AddRange(ZoomItems);
            _cbZoom.SelectedIndex = 0;
            _cbZoom.Margin = new Padding(0, 0, 0, 0);
            _cbZoom.SelectedIndexChanged += delegate { if (_pdf != null) RenderPageAsync(); };

            _pdfBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0),
                BackColor = Color.FromArgb(248, 248, 248),
                Visible = false,
            };
            _pdfBar.Controls.AddRange(new Control[]
            {
                _btnPrev, _lblPage, _btnNext,
                new Label { Text = "缩放", AutoSize = true, Font = UiUtil.Font9, Margin = new Padding(0, 5, 6, 0), ForeColor = Color.DimGray },
                _cbZoom,
            });

            // ---- 预览区 ----
            var stageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            _picHost.Controls.Add(_pic);
            stageHost.Controls.Add(_picHost);
            stageHost.Controls.Add(_ph);

            _previewLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            _previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            _previewLayout.Controls.Add(stageHost, 0, 0);
            _previewLayout.Controls.Add(_pdfBar, 0, 1);

            var picBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
            };
            picBorder.Controls.Add(_previewLayout);

            // ---- 按钮行 ----
            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
            };
            _btnOpen = UiUtil.Btn("打开文件", 82);
            _btnOpen.Margin = new Padding(0, 0, 8, 0);
            _btnOpen.Click += delegate { if (_rec != null) UiUtil.OpenFile(this, _rec.CurrentPath); };
            _btnFolder = UiUtil.Btn("所在文件夹", 90);
            _btnFolder.Margin = new Padding(0, 0, 8, 0);
            _btnFolder.Click += delegate { if (_rec != null) UiUtil.OpenFolder(this, _rec.CurrentPath); };
            _btnEdit = UiUtil.Btn("编辑…", 70);
            _btnEdit.Margin = Padding.Empty;
            _btnEdit.Click += delegate { if (_rec != null) _onEdit(_rec); };
            btnRow.Controls.AddRange(new Control[] { _btnOpen, _btnFolder, _btnEdit });

            // ---- 四行显式布局：标题 / 预览(弹性) / 属性 / 按钮 ----
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, TitleHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, PropsHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonRowHeight));

            layout.Controls.Add(titleHost, 0, 0);
            layout.Controls.Add(picBorder, 0, 1);
            layout.Controls.Add(_props, 0, 2);
            layout.Controls.Add(btnRow, 0, 3);

            Controls.Add(layout);

            _resizeTimer.Tick += delegate
            {
                _resizeTimer.Stop();
                if (_pdf != null && IsFit) RenderPageAsync();
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_props.Columns.Count > 1)
                _props.Columns[1].Width = Math.Max(120, _props.ClientSize.Width - _props.Columns[0].Width - 8);

            // 适应窗口模式下，窗口大小变了要按新宽度重渲染，否则放大后发虚
            if (_pdf != null && IsFit)
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _loadSeq++;
                _renderSeq++;
                DisposePdf();
                _resizeTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        bool IsFit { get { return _cbZoom.SelectedIndex <= 0; } }

        public void Show(FileRecord r)
        {
            _rec = r;
            _loadSeq++;
            _renderSeq++;
            DisposePdf();
            ClearImage();
            _props.BeginUpdate();
            _props.Items.Clear();

            if (r == null)
            {
                _title.Text = "";
                _ph.Text = "选择列表中的文件进行预览";
                ShowPlaceholder(null);
                SetButtons(false);
                _props.EndUpdate();
                return;
            }

            _title.Text = r.FileName;
            SetButtons(true);
            AddProp("状态", r.Lost ? "丢失" : "正常");
            AddProp("供应商", string.IsNullOrEmpty(r.Supplier) ? "—" : r.Supplier);
            AddProp("文档类型", string.IsNullOrEmpty(r.DocType) ? "—" : r.DocType);
            AddProp("BMG 号", string.IsNullOrEmpty(r.BmgNo) ? "—" : r.BmgNo);
            if (r.Codes.Count == 0)
                AddProp("关联编号", "（无）");
            else
                foreach (var ct in CodeTypes.All)
                {
                    var vals = r.Codes.Where(c => c.Type == ct[0]).Select(c => c.Value).ToList();
                    if (vals.Count > 0) AddProp(ct[1], string.Join("、", vals));
                }
            AddProp("大小", UiUtil.FormatSize(r.FileSize));
            AddProp("修改时间", r.FileMTime);
            AddProp("入库时间", r.ImportTime);
            AddProp("SHA-256", r.Hash);
            AddProp("路径", r.CurrentPath);
            _props.EndUpdate();

            string path = r.CurrentPath;
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            bool isImg = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" ||
                         ext == ".tif" || ext == ".tiff";
            bool isPdf = ext == ".pdf";

            if (!File.Exists(path))
            {
                _ph.Text = r.Lost ? "源文件丢失，无法预览" : "文件不在磁盘上，可执行一次目录扫描";
                ShowPlaceholder(null);
            }
            else if (isImg)
            {
                LoadImage(path);
            }
            else if (isPdf)
            {
                LoadPdfAsync(path);
            }
            else
            {
                _ph.Text = "该格式不支持内嵌预览（点【打开文件】查看）";
                ShowPlaceholder(null);
            }
        }

        // ======================== 图片 ========================

        void LoadImage(string path)
        {
            try
            {
                // 复制到内存流再解码，避免 GDI+ 锁住原文件
                byte[] bytes = File.ReadAllBytes(path);
                _imgStream = new MemoryStream(bytes);
                _img = Image.FromStream(_imgStream);   // TIFF 默认取第一页
                _pic.Dock = DockStyle.Fill;
                _pic.SizeMode = PictureBoxSizeMode.Zoom;
                _pic.Image = _img;
                _pic.Visible = true;
                _picHost.Visible = true;
                _ph.Visible = false;
                ShowPdfBar(false);
            }
            catch
            {
                ClearImage();
                _ph.Text = "图片加载失败（文件可能损坏）";
                ShowPlaceholder(null);
            }
        }

        // ======================== PDF ========================

        async void LoadPdfAsync(string path)
        {
            int seq = ++_loadSeq;
            _pdfPage = 0;
            _ph.Text = "正在读取 PDF…";
            ShowPlaceholder(null);
            ShowPdfBar(false);

            IPdfRender render = null;
            string err = null;
            await Task.Run(() => { render = PdfRenderFactory.TryLoad(path, out err); });

            if (seq != _loadSeq)
            {
                if (render != null) render.Dispose();
                return;
            }

            if (render == null)
            {
                _ph.Text = "PDF 预览不可用：" + (string.IsNullOrEmpty(err) ? "无法解析" : err) +
                           "\n（可点【打开文件】查看）";
                ShowPlaceholder(null);
                return;
            }

            _pdf = render;
            _cbZoom.SelectedIndex = 0;      // 每次打开先按适应窗口显示
            ShowPdfBar(true);
            UpdatePageLabel();
            RenderPageAsync();
        }

        async void RenderPageAsync()
        {
            if (_pdf == null) return;

            int seq = ++_renderSeq;
            int page = _pdfPage;
            int want = RenderWidth(page);
            IPdfRender doc = _pdf;
            Bitmap bmp = null;
            Exception fail = null;

            await Task.Run(() =>
            {
                try { bmp = doc.Render(page, want); }
                catch (Exception ex) { fail = ex; }
            });

            if (seq != _renderSeq)
            {
                if (bmp != null) bmp.Dispose();
                return;
            }

            if (bmp == null)
            {
                _ph.Text = "这一页渲染失败" + (fail == null ? "" : "：" + fail.Message);
                ShowPlaceholder(null);
                return;
            }

            CalibrateScale(bmp.Width, want);
            ApplyBitmap(bmp);
        }

        /// <summary>
        /// 渲染请求宽度。DestinationWidth 是 DIP，系统会按屏幕 DPI 放大成像素，
        /// 所以先用实测比例换算回像素目标，再留 1.2 倍余量供缩小显示时保持锐利。
        /// </summary>
        int RenderWidth(int page)
        {
            int display = IsFit
                ? Math.Max(320, _picHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4)
                : ZoomSize(page).Width;

            // 渲染像素比显示像素多 15%，缩小显示时不发虚；上限 2200，200% 看 A4 也不会生成十几 MP 的位图
            int pixelTarget = Math.Min(2200, Math.Max(240, (int)(display * 1.15)));
            int want = (int)Math.Round(pixelTarget / _renderScale);
            return Math.Min(3200, Math.Max(200, want));
        }

        void CalibrateScale(int gotWidth, int requested)
        {
            if (requested <= 0 || gotWidth <= 0) return;
            float s = gotWidth / (float)requested;
            if (s < 0.4f || s > 3f) return;
            _renderScale = s;
        }

        /// <summary>非"适应窗口"时按 96 DPI 把页面点值换算成显示像素，再乘缩放倍数。</summary>
        System.Drawing.Size ZoomSize(int page)
        {
            float scale = _cbZoom.SelectedIndex == 1 ? 1.0f
                        : _cbZoom.SelectedIndex == 2 ? 1.5f
                        : 2.0f;
            SizeF pt;
            try { pt = _pdf.PageSize(page); }
            catch { pt = new SizeF(595, 842); }

            int w = (int)Math.Round(pt.Width * 96.0 / 72.0 * scale);
            int h = (int)Math.Round(pt.Height * 96.0 / 72.0 * scale);
            return new System.Drawing.Size(Math.Min(8000, Math.Max(80, w)), Math.Min(11000, Math.Max(80, h)));
        }

        void ApplyBitmap(Bitmap bmp)
        {
            ClearImage();
            _img = bmp;
            _pic.Image = bmp;

            if (IsFit)
            {
                _pic.Dock = DockStyle.Fill;                 // 交给 Zoom 自适应
                _pic.SizeMode = PictureBoxSizeMode.Zoom;
                _picHost.AutoScroll = false;
            }
            else
            {
                _pic.Dock = DockStyle.None;                 // 1:1 显示，超出部分用滚动条
                _pic.SizeMode = PictureBoxSizeMode.Zoom;    // 位图按控件尺寸铺满，兼顾 DPI 放大
                _pic.Size = ZoomSize(_pdfPage);
                _pic.Location = Point.Empty;
                _picHost.AutoScroll = true;
            }

            _pic.Visible = true;
            _picHost.Visible = true;
            _ph.Visible = false;
            UpdatePageLabel();
        }

        void GoPage(int delta)
        {
            if (_pdf == null) return;
            int next = _pdfPage + delta;
            if (next < 0 || next >= _pdf.PageCount) return;
            _pdfPage = next;
            RenderPageAsync();
        }

        void UpdatePageLabel()
        {
            if (_pdf == null) return;
            _lblPage.Text = (_pdfPage + 1) + " / " + _pdf.PageCount;
            _btnPrev.Enabled = _pdfPage > 0;
            _btnNext.Enabled = _pdfPage < _pdf.PageCount - 1;
        }

        void ShowPdfBar(bool on)
        {
            _pdfBar.Visible = on;
            _previewLayout.RowStyles[1].Height = on ? PdfBarHeight : 0;
        }

        void DisposePdf()
        {
            if (_pdf != null) { try { _pdf.Dispose(); } catch { } _pdf = null; }
            _pdfPage = 0;
            _lblPage.Text = "1 / 1";
        }

        // ======================== 公共 ========================

        void AddProp(string k, string v)
        {
            var it = new ListViewItem(k);
            it.SubItems.Add(v ?? "");
            if (k == "SHA-256" || k == "路径") it.ToolTipText = v;
            _props.Items.Add(it);
        }

        void SetButtons(bool enabled)
        {
            _btnOpen.Enabled = enabled;
            _btnFolder.Enabled = enabled;
            _btnEdit.Enabled = enabled;
        }

        void ShowPlaceholder(string text)
        {
            if (text != null) _ph.Text = text;
            _ph.Visible = true;
            _ph.BringToFront();
            _picHost.Visible = false;
        }

        void ClearImage()
        {
            _pic.Image = null;
            if (_img != null) { _img.Dispose(); _img = null; }
            if (_imgStream != null) { _imgStream.Dispose(); _imgStream = null; }
        }
    }
}
