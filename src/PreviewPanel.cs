using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 右侧预览：标题一行、预览图（弹性）、属性清单（固定高、可滚动）、操作按钮一行。
    /// 全部用 TableLayoutPanel 分行，行高固定值集中在一处，便于整体对齐。
    /// </summary>
    public class PreviewPanel : Panel
    {
        const int TitleHeight = 34;
        const int PropsHeight = 226;
        const int ButtonRowHeight = 42;

        readonly Label _title = new Label
        {
            Font = UiUtil.Font9B,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        readonly PictureBox _pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Visible = false,
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
        readonly Action<FileRecord> _onEdit;
        FileRecord _rec;
        Image _img;
        MemoryStream _imgStream;

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

            // ---- 图片区 ----
            var picBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
            };
            picBorder.Controls.Add(_pic);
            picBorder.Controls.Add(_ph);

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

            // ---- 三行显式布局：标题 / 预览(弹性) / 属性 / 按钮 ----
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
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_props.Columns.Count > 1)
                _props.Columns[1].Width = Math.Max(120, _props.ClientSize.Width - _props.Columns[0].Width - 8);
        }

        public void Show(FileRecord r)
        {
            _rec = r;
            ClearImage();
            _props.BeginUpdate();
            _props.Items.Clear();

            if (r == null)
            {
                _title.Text = "";
                _ph.Text = "选择列表中的文件进行预览";
                _ph.Visible = true;
                _pic.Visible = false;
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

            string ext = Path.GetExtension(r.CurrentPath ?? "").ToLowerInvariant();
            bool isImg = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";

            if (isImg && File.Exists(r.CurrentPath))
            {
                try
                {
                    // 复制到内存流再解码，避免 GDI+ 锁住原文件
                    byte[] bytes = File.ReadAllBytes(r.CurrentPath);
                    _imgStream = new MemoryStream(bytes);
                    _img = Image.FromStream(_imgStream);   // TIFF 默认取第一页
                    _pic.Image = _img;
                    _pic.Visible = true;
                    _ph.Visible = false;
                }
                catch
                {
                    ClearImage();
                    _ph.Text = "图片加载失败（文件可能损坏）";
                    _ph.Visible = true;
                    _pic.Visible = false;
                }
            }
            else if (!File.Exists(r.CurrentPath))
            {
                _ph.Text = r.Lost ? "源文件丢失，无法预览" : "文件不在磁盘上，可执行一次目录扫描";
                _ph.Visible = true;
                _pic.Visible = false;
            }
            else
            {
                _ph.Text = "PDF 文档（点【打开文件】查看）";
                _ph.Visible = true;
                _pic.Visible = false;
            }
        }

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

        void ClearImage()
        {
            _pic.Image = null;
            if (_img != null) { _img.Dispose(); _img = null; }
            if (_imgStream != null) { _imgStream.Dispose(); _imgStream = null; }
        }
    }
}
