using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 主页面：上工具条 / 左目录树 · 中文件列表 · 右预览 / 下状态栏。
    /// 全部用 TableLayoutPanel + SplitContainer 排布，不再靠固定像素的 Dock 叠加，
    /// 窗口变窄、系统缩放变化时不会互相挤压塌陷。
    /// </summary>
    public class BrowseTab : UserControl
    {
        class DirNode
        {
            public string Name;
            public string FullPath;
            public int Direct;   // 直接位于此目录的档案数
            public int Total;    // 子树档案数
            public Dictionary<string, DirNode> Children = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        }

        const int TreePaneWidth = 240;
        const int PreviewPaneWidth = 300;
        const int MinNameWidth = 110;

        readonly MainForm _main;
        readonly TextBox _tbSearch = UiUtil.Txt(240);
        readonly TreeView _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = UiUtil.Font9,
            ShowLines = false,
            FullRowSelect = true,
            ItemHeight = 22,
            ShowNodeToolTips = true,
            BackColor = Color.White,
        };
        readonly SmoothGrid _grid = new SmoothGrid();
        readonly Button _btnNew = UiUtil.Btn("", 190);
        List<ScanRow> _newRows = new List<ScanRow>();
        bool _autoChecking;
        readonly CheckBox _cbOnlyPending = new CheckBox
        {
            Text = "只看待补",
            AutoSize = true,
            Font = UiUtil.Font9,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(16, 0, 0, 0),
        };
        static readonly Color PendingOrange = Color.FromArgb(190, 100, 0);
        readonly PreviewPanel _preview;
        readonly Label _lblStatus = UiUtil.Lbl("");
        readonly Timer _searchDebounce = new Timer { Interval = 400 };
        readonly Timer _previewDebounce = new Timer { Interval = 200 };
        readonly ImportDialogLink _importDlg = new ImportDialogLink();
        readonly ScanDialogLink _scanDlg = new ScanDialogLink();
        readonly SplitContainer _splitOuter = new SplitContainer();
        readonly SplitContainer _splitInner = new SplitContainer();

        List<FileRecord> _all = new List<FileRecord>();
        List<FileRecord> _view = new List<FileRecord>();
        string _scope;            // null=全部，"!outside"=根目录之外，其他=目录路径
        string _scopeLabel = "全部档案";
        string _keyword = "";
        int _sortCol = -1;
        bool _sortAsc = true;
        bool _suppressPreview;
        bool _userSplit;      // 用户拖动过分隔条后不再自动重排
        bool _inSplit;
        bool _inColumnLayout;
        public BrowseTab(MainForm main)
        {
            _main = main;
            _preview = new PreviewPanel(r => EditRecord(r));

            Dock = DockStyle.Fill;
            BackColor = Color.White;
            AllowDrop = true;

            // ---- 根布局：工具条 / 内容 / 状态栏，三行显式比例 ----
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            root.Controls.Add(BuildToolbar(), 0, 0);
            root.Controls.Add(BuildContent(), 0, 1);
            root.Controls.Add(BuildStatusBar(), 0, 2);
            Controls.Add(root);

            // ---- 搜索 ----
            var tip = new ToolTip();
            tip.SetToolTip(_tbSearch, "空格分隔多关键词；覆盖文件名 / 供应商 / 文档类型 / 全部编号");
            _tbSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    _searchDebounce.Stop();
                    DoSearch();
                }
            };
            _tbSearch.TextChanged += (s, e) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); DoSearch(); };

            // ---- 预览延迟联动 ----
            _previewDebounce.Tick += (s, e) => { _previewDebounce.Stop(); PreviewCurrent(); };
            _grid.SelectionChanged += (s, e) =>
            {
                if (_suppressPreview) return;
                _previewDebounce.Stop();
                _previewDebounce.Start();
            };

            BuildGrid();
            BuildMenu();
            WireTree();

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => OpenImport((string[])e.Data.GetData(DataFormats.FileDrop));

            Load += delegate
            {
                ReloadAll();
                BeginInvoke(new Action(ApplyDefaultSplitters));
                // 打开软件就自动检查一次：有人直接把文件拷进归档目录时，不用再手动扫描也能发现。
                // 延后 3 秒（先让首屏和列表出来），并且走目录快照剪枝的增量检查——几万文件也只在秒级
                var kickoff = new Timer { Interval = 3000 };
                kickoff.Tick += delegate
                {
                    kickoff.Stop();
                    kickoff.Dispose();
                    AutoCheckNewFiles(true);
                };
                kickoff.Start();
            };
        }

        // ================= 布局 =================

        Control BuildToolbar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(12, 0, 12, 0),
                Margin = Padding.Empty,
                BackColor = Color.White,
            };            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var lbl = UiUtil.Lbl("搜索");
            lbl.Anchor = AnchorStyles.Left;
            lbl.Margin = new Padding(0, 0, 8, 0);
            bar.Controls.Add(lbl, 0, 0);

            _tbSearch.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _tbSearch.Margin = new Padding(0, 0, 0, 0);
            bar.Controls.Add(_tbSearch, 1, 0);

            var btnClear = UiUtil.Btn("清除", 56);
            btnClear.Anchor = AnchorStyles.Left;
            btnClear.Margin = new Padding(6, 0, 0, 0);
            btnClear.Click += delegate { _tbSearch.Text = ""; };
            bar.Controls.Add(btnClear, 2, 0);

            // 补入库时留空的行会带「待补」标记，勾上这里就只剩没填完信息的档案
            var tipPending = new ToolTip();
            tipPending.SetToolTip(_cbOnlyPending, "只显示供应商或文档类型还空着的档案（补入库时可以先入库、事后补填）");
            _cbOnlyPending.CheckedChanged += delegate { RefreshView(); };
            bar.Controls.Add(_cbOnlyPending, 3, 0);

            var actions = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = Padding.Empty,
            };
            var btnImport = UiUtil.Btn("添加文件…", 96);
            btnImport.Click += delegate { OpenImport(null); };
            var btnScan = UiUtil.Btn("扫描目录…", 96);
            btnScan.Click += delegate { OpenScan(); };
            var btnRefresh = UiUtil.Btn("刷新", 64);
            btnRefresh.Click += delegate { ReloadAll(); AutoCheckNewFiles(false); };

            // 启动时自动检查到的新文件：用这条橙色按钮提示，点一下直接进补入库（不弹模态框打断）
            _btnNew.Visible = false;
            _btnNew.ForeColor = PendingOrange;
            _btnNew.FlatStyle = FlatStyle.System;
            _btnNew.Click += delegate { OpenAdoptNew(); };
            var tipNew = new ToolTip();
            tipNew.SetToolTip(_btnNew, "归档目录里有文件是直接拷进去的，还没登记进档案库");

            btnImport.Margin = new Padding(0, 0, 8, 0);
            btnScan.Margin = new Padding(0, 0, 8, 0);
            btnRefresh.Margin = Padding.Empty;
            _btnNew.Margin = new Padding(0, 0, 8, 0);
            actions.Controls.AddRange(new Control[] { _btnNew, btnImport, btnScan, btnRefresh });
            bar.Controls.Add(actions, 4, 0);

            return bar;
        }

        Control BuildContent()
        {
            // 外层：左树 | 右侧（列表 + 预览）
            // 先给一个初始尺寸，再设最小宽度，最后才 Dock —— SplitContainer 在尺寸为 0 时会拒绝设置 MinSize
            _splitOuter.Size = new Size(1200, 700);
            _splitOuter.Dock = DockStyle.Fill;
            _splitOuter.Margin = Padding.Empty;
            _splitOuter.Orientation = Orientation.Vertical;
            _splitOuter.FixedPanel = FixedPanel.Panel1;
            _splitOuter.SplitterWidth = 4;
            _splitOuter.Panel1MinSize = 160;
            _splitOuter.Panel2MinSize = 560;
            _splitOuter.Panel1.Padding = new Padding(12, 10, 4, 10);
            _splitOuter.Panel2.Padding = Padding.Empty;
            _splitOuter.SplitterDistance = TreePaneWidth;

            var treeHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var treeTitle = new Label
            {
                Text = "归档目录",
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Color.FromArgb(90, 90, 90),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            treeHost.Controls.Add(_tree);
            treeHost.Controls.Add(treeTitle);
            _splitOuter.Panel1.Controls.Add(treeHost);

            // 内层：列表 | 预览
            _splitInner.Size = new Size(900, 700);
            _splitInner.Dock = DockStyle.Fill;
            _splitInner.Margin = Padding.Empty;
            _splitInner.Orientation = Orientation.Vertical;
            _splitInner.FixedPanel = FixedPanel.Panel2;
            _splitInner.SplitterWidth = 4;
            _splitInner.Panel1MinSize = 300;
            _splitInner.Panel2MinSize = 240;
            _splitInner.Panel1.Padding = new Padding(4, 10, 4, 10);
            _splitInner.Panel2.Padding = new Padding(4, 10, 12, 10);
            _splitInner.SplitterDistance = 900 - PreviewPaneWidth - _splitInner.SplitterWidth;

            _splitInner.Panel1.Controls.Add(_grid);
            _preview.Dock = DockStyle.Fill;
            _splitInner.Panel2.Controls.Add(_preview);

            // 只有用户按住鼠标拖动才视为"手动分栏"，容器尺寸变化引发的 SplitterMoved 不算
            _splitOuter.SplitterMoved += (s, e) => { if (!_inSplit && Control.MouseButtons != MouseButtons.None) _userSplit = true; };
            _splitInner.SplitterMoved += (s, e) => { if (!_inSplit && Control.MouseButtons != MouseButtons.None) _userSplit = true; };

            _splitOuter.Panel2.Controls.Add(_splitInner);
            return _splitOuter;
        }

        Control BuildStatusBar()
        {
            var status = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.FromArgb(250, 250, 250) };
            status.Paint += (s, e) => e.Graphics.DrawLine(Pens.Gainsboro, 0, 0, status.Width, 0);
            _lblStatus.AutoSize = false;
            _lblStatus.AutoEllipsis = true;
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(12, 0, 12, 0);
            _lblStatus.ForeColor = Color.FromArgb(80, 80, 80);
            status.Controls.Add(_lblStatus);
            return status;
        }

        /// <summary>
        /// 三栏初始宽度按当前可用宽度算一次；用户拖动过分隔条后就不再干预（_userSplit）。
        /// 放在 OnResize 里做，是因为窗体首次显示、DPI 缩放都会改变实际宽度。
        /// </summary>
        void ApplyDefaultSplitters()
        {
            if (_userSplit || _inSplit) return;
            _inSplit = true;
            try
            {
                int maxOuter = _splitOuter.Width - _splitOuter.Panel2MinSize - _splitOuter.SplitterWidth;
                if (maxOuter >= _splitOuter.Panel1MinSize)
                    _splitOuter.SplitterDistance = Math.Min(TreePaneWidth, maxOuter);

                int maxInner = _splitInner.Width - _splitInner.Panel2MinSize - _splitInner.SplitterWidth;
                if (maxInner >= _splitInner.Panel1MinSize)
                {
                    int want = _splitInner.Width - PreviewPaneWidth - _splitInner.SplitterWidth;
                    _splitInner.SplitterDistance = Math.Max(_splitInner.Panel1MinSize, Math.Min(want, maxInner));
                }
            }
            catch { }
            finally { _inSplit = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyDefaultSplitters();
        }

        // ================= 数据加载 =================

        /// <summary>整页刷新（树 + 列表 + 预览）。</summary>
        public void ReloadAll()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _all = FileRepo.LoadAllWithCodes();
                BuildTree();
                RefreshView();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "加载失败", ex); }
            finally { Cursor = Cursors.Default; }
        }

        // ================= 文件夹树 =================

        void WireTree()
        {
            _tree.BeforeExpand += (s, e) =>
            {
                var tn = e.Node;
                if (tn.Nodes.Count == 1 && tn.Nodes[0].Text == "…" && tn.Tag is DirNode)
                {
                    tn.Nodes.Clear();
                    var dn = (DirNode)tn.Tag;
                    foreach (var c in dn.Children.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        tn.Nodes.Add(NodeFor(c));
                }
            };
            // 右键目录 → 统一指定该目录（及子目录）下所有档案的文档类型
            var treeMenu = new ContextMenuStrip();
            var miType = new ToolStripMenuItem("统一指定类型…");
            treeMenu.Items.Add(miType);
            _tree.ContextMenuStrip = treeMenu;
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.Node != null) _tree.SelectedNode = e.Node;
            };
            treeMenu.Opening += (s, e) =>
            {
                var dn = (_tree.SelectedNode == null ? null : _tree.SelectedNode.Tag) as DirNode;
                miType.Enabled = dn != null && !string.IsNullOrEmpty(dn.FullPath);
            };
            miType.Click += delegate
            {
                var dn = (_tree.SelectedNode == null ? null : _tree.SelectedNode.Tag) as DirNode;
                if (dn == null || string.IsNullOrEmpty(dn.FullPath)) return;
                OpenTypeAssign(dn);
            };

            _tree.AfterSelect += (s, e) =>
            {
                var dn = e.Node.Tag as DirNode;
                if (dn != null)
                {
                    _scope = dn.FullPath;
                    _scopeLabel = dn.Name;
                }
                else if (Equals(e.Node.Tag, "!outside"))
                {
                    _scope = "!outside";
                    _scopeLabel = "其他位置";
                }
                else
                {
                    _scope = null;
                    _scopeLabel = "全部档案";
                }
                RefreshView();
            };
        }

        void BuildTree()
        {
            _suppressPreview = true;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            _tree.Nodes.Add(new TreeNode("全部档案") { ToolTipText = "显示库中全部档案" });

            var roots = ArchiveRoots.Paths();
            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
                nodes[r] = new DirNode { Name = ArchiveRoots.DisplayName(r), FullPath = r };

            int outside = 0;
            foreach (var rec in _all)
            {
                string dir = SafeDir(rec.CurrentPath);
                if (dir == null) { outside++; continue; }

                // 归属判定取最具体的根（万一出现嵌套目录也不会重复计数）
                string root = null;
                foreach (var r in roots)
                    if (Under(dir, r) && (root == null || r.Length > root.Length)) root = r;

                if (root == null) { outside++; continue; }

                var node = nodes[root];
                string rel = dir.Length > root.Length ? dir.Substring(root.Length).Trim('\\') : "";
                string cur = root;
                if (rel.Length > 0)
                    foreach (var seg in rel.Split('\\'))
                    {
                        cur = cur + "\\" + seg;
                        DirNode child;
                        if (!node.Children.TryGetValue(seg, out child))
                        {
                            child = new DirNode { Name = seg, FullPath = cur };
                            node.Children[seg] = child;
                        }
                        node = child;
                    }
                node.Direct++;
            }

            foreach (var r in roots)
            {
                var dn = nodes[r];
                SumCounts(dn);
                // 根节点直接铺开一级子目录，不再放"…"占位
                var tn = new TreeNode(dn.Name + "（" + dn.Total + "）") { Tag = dn, ToolTipText = r };
                foreach (var c in dn.Children.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    tn.Nodes.Add(NodeFor(c));
                tn.Expand();
                _tree.Nodes.Add(tn);
            }

            if (outside > 0)
                _tree.Nodes.Add(new TreeNode("（其他位置）" + outside) { Tag = "!outside", ForeColor = Color.Gray });

            if (roots.Count == 0)
                _tree.Nodes.Add(new TreeNode("（未设置归档目录，请在【设置】中添加）") { ForeColor = Color.Gray });

            _tree.EndUpdate();
            _tree.SelectedNode = _tree.Nodes[0];   // 初始范围 = 全部档案
            _suppressPreview = false;
        }

        static TreeNode NodeFor(DirNode n)
        {
            var tn = new TreeNode(n.Name + "（" + n.Total + "）")
            {
                Tag = n,
                ToolTipText = n.FullPath,
            };
            if (n.Children.Count > 0) tn.Nodes.Add(new TreeNode("…")); // 展开时替换
            return tn;
        }

        static int SumCounts(DirNode n)
        {
            int t = n.Direct;
            foreach (var c in n.Children.Values)
                t += SumCounts(c);
            n.Total = t;
            return t;
        }

        static string SafeDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string d = Path.GetDirectoryName(Path.GetFullPath(path));
                return string.IsNullOrEmpty(d) ? null : d;
            }
            catch { return null; }
        }

        static bool Under(string dir, string root)
        {
            return dir.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // ================= 列表 =================

        void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.VirtualMode = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.AllowUserToResizeRows = false;
            _grid.GridColor = Color.FromArgb(235, 235, 235);
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 28;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 247, 247);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(60, 60, 60);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(247, 247, 247);
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 4, 0);

            // 其余列固定宽度，文件名吃掉剩余宽度：宽度计算是确定的，不会出现"算完又溢出"的滚动条
            var pending = Col("Pending", "待补", 56);
            pending.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            pending.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            pending.HeaderCell.ToolTipText = "供应商或文档类型为空，需要事后补填";
            _grid.Columns.Add(pending);
            _grid.Columns.Add(Col("FileName", "文件名", 300));
            _grid.Columns.Add(Col("Supplier", "供应商", 74));
            _grid.Columns.Add(Col("DocType", "类型", 56));
            _grid.Columns.Add(Col("PartNo", "零件号", 86));
            _grid.Columns.Add(Col("VwNo", "VW号", 82));
            _grid.Columns.Add(Col("IdentNo", "ident号", 82));
            _grid.Columns.Add(Col("BmgNo", "BMG号", 78));
            var size = Col("Size", "大小", 56);
            size.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns.Add(size);

            // 列名短，完整含义放表头提示里
            _grid.Columns["PartNo"].HeaderCell.ToolTipText = "供应商零件号";
            _grid.Columns["VwNo"].HeaderCell.ToolTipText = "VW 客户号";
            _grid.Columns["IdentNo"].HeaderCell.ToolTipText = "ident 内部件号";
            _grid.Columns["BmgNo"].HeaderCell.ToolTipText = "BMG 号（单值，可空；空着会标「待补」）";
            _grid.Resize += (s, e) => LayoutColumns();

            _grid.CellValueNeeded += GridCellValueNeeded;
            _grid.CellFormatting += GridCellFormatting;
            _grid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
                var r = _view[e.RowIndex];
                string name = _grid.Columns[e.ColumnIndex].Name;
                if (name == "PartNo") e.ToolTipText = UiUtil.CodeValue(r, CodeTypes.PartNo);
                else if (name == "VwNo") e.ToolTipText = UiUtil.CodeValue(r, CodeTypes.VwNo);
                else if (name == "IdentNo") e.ToolTipText = UiUtil.CodeValue(r, CodeTypes.IdentNo);
                else if (name == "Pending")
                    e.ToolTipText = r.NeedsInfo ? "缺：" + r.MissingInfo + "（右键【编辑档案】补填）" : "";
                else if (name == "FileName")
                    e.ToolTipText = r.Lost ? r.CurrentPath + "\n（源文件丢失）" : r.CurrentPath;
            };
            _grid.ColumnHeaderMouseClick += GridColumnSort;
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) WithCurrent(r => UiUtil.OpenFile(this, r.CurrentPath)); };
            _grid.DataError += delegate { };
            _grid.AllowDrop = true;
            _grid.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            _grid.DragDrop += (s, e) => OpenImport((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        static DataGridViewTextBoxColumn Col(string name, string header, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.Programmatic,
            };
        }

        /// <summary>
        /// 文件名列吃掉剩余宽度，其余列固定。窗口太窄时按优先级依次收起"大小""类型""BMG号"
        /// （预览里都能看到），保证文件名 + 三类编号始终完整可见，不出现横向滚动条。
        /// </summary>
        void LayoutColumns()
        {
            if (_grid.Columns.Count == 0 || _inColumnLayout) return;
            _inColumnLayout = true;
            try
            {
                _grid.Columns["Size"].Visible = true;
                _grid.Columns["DocType"].Visible = true;
                _grid.Columns["BmgNo"].Visible = true;

                int avail = _grid.ClientSize.Width;
                if (avail - FixedExceptName() - 2 < MinNameWidth) _grid.Columns["Size"].Visible = false;
                if (avail - FixedExceptName() - 2 < MinNameWidth) _grid.Columns["DocType"].Visible = false;
                if (avail - FixedExceptName() - 2 < MinNameWidth) _grid.Columns["BmgNo"].Visible = false;

                _grid.Columns["FileName"].Width = Math.Max(MinNameWidth, avail - FixedExceptName() - 2);
            }
            finally { _inColumnLayout = false; }
        }

        int FixedExceptName()
        {
            int w = 0;
            foreach (DataGridViewColumn c in _grid.Columns)
                if (c.Name != "FileName" && c.Visible) w += c.Width;
            return w;
        }

        void GridCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            var r = _view[e.RowIndex];
            switch (_grid.Columns[e.ColumnIndex].Name)
            {
                case "Pending": e.Value = IsPending(r) ? "待补" : ""; break;
                case "FileName": e.Value = r.FileName; break;
                case "Supplier": e.Value = r.Supplier ?? ""; break;
                case "DocType": e.Value = r.DocType ?? ""; break;
                case "PartNo": e.Value = UiUtil.CodeValue(r, CodeTypes.PartNo); break;
                case "VwNo": e.Value = UiUtil.CodeValue(r, CodeTypes.VwNo); break;
                case "IdentNo": e.Value = UiUtil.CodeValue(r, CodeTypes.IdentNo); break;
                case "BmgNo": e.Value = r.BmgNo ?? ""; break;
                case "Size": e.Value = UiUtil.FormatSize(r.FileSize); break;
            }
        }

        void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            // 源文件丢失的整行标红（不再单占一列"状态"，列表更紧凑）
            if (_view[e.RowIndex].Lost) { e.CellStyle.ForeColor = UiUtil.LostRed; return; }
            // 待补信息：只在标记列上色，不干扰整行阅读
            if (_grid.Columns[e.ColumnIndex].Name == "Pending" && IsPending(_view[e.RowIndex]))
                e.CellStyle.ForeColor = PendingOrange;
        }

        /// <summary>供应商或文档类型还空着 = 这条档案的信息没填完（补入库允许先入库后补）。</summary>
        static bool IsPending(FileRecord r)
        {
            return r.NeedsInfo;
        }

        void GridColumnSort(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (_sortCol == e.ColumnIndex) _sortAsc = !_sortAsc;
            else { _sortCol = e.ColumnIndex; _sortAsc = true; }
            RefreshView();
            foreach (DataGridViewColumn c in _grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
            _grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection =
                _sortAsc ? SortOrder.Ascending : SortOrder.Descending;
        }

        void RefreshView()
        {
            IEnumerable<FileRecord> q = _all;
            if (_scope == "!outside")
            {
                q = _all.Where(r => !ArchiveRoots.IsUnderAny(r.CurrentPath));
            }
            else if (_scope != null)
            {
                string sc = _scope;
                q = _all.Where(r =>
                {
                    var d = SafeDir(r.CurrentPath);
                    return d != null && Under(d, sc);
                });
            }

            var terms = (_keyword ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in terms)
            {
                string t = term;
                q = q.Where(r => MatchTerm(r, t));
            }

            if (_cbOnlyPending.Checked) q = q.Where(IsPending);

            _view = q.ToList();
            ApplySort();

            _suppressPreview = true;
            _grid.RowCount = 0;
            _grid.RowCount = _view.Count;
            _suppressPreview = false;
            _previewDebounce.Stop();
            PreviewCurrent();

            int pending = _view.Count(IsPending);
            string pendDetail = "";
            if (pending > 0)
            {
                int noSup = _view.Count(r => string.IsNullOrWhiteSpace(r.Supplier));
                int noType = _view.Count(r => string.IsNullOrWhiteSpace(r.DocType));
                int noBmg = _view.Count(r => string.IsNullOrWhiteSpace(r.BmgNo));
                var parts = new List<string>();
                if (noSup > 0) parts.Add("缺供应商 " + noSup);
                if (noType > 0) parts.Add("缺类型 " + noType);
                if (noBmg > 0) parts.Add("缺BMG号 " + noBmg);
                pendDetail = "（" + string.Join("，", parts) + "）";
            }

            _lblStatus.Text = string.Format("共 {0} 个档案 ｜ 范围：{1}{2}{3} ｜ 待补 {4} 个{5}{6}",
                _view.Count, _scopeLabel,
                terms.Length > 0 ? " ｜ 搜索：" + _keyword : "",
                _cbOnlyPending.Checked ? " ｜ 只看待补" : "",
                pending, pendDetail,
                _newRows.Count > 0 ? " ｜ 新文件 " + _newRows.Count + " 个待入库" : "");
        }

        static bool MatchTerm(FileRecord r, string term)
        {
            if (r.FileName != null && r.FileName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.Supplier != null && r.Supplier.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.DocType != null && r.DocType.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.BmgNo != null && r.BmgNo.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.Codes != null)
                foreach (var c in r.Codes)
                    if (c.Value != null && c.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        void ApplySort()
        {
            string name = _sortCol >= 0 && _sortCol < _grid.Columns.Count ? _grid.Columns[_sortCol].Name : "FileName";
            bool asc = _sortAsc;
            _view.Sort(delegate (FileRecord a, FileRecord b)
            {
                int v;
                if (name == "Size") v = a.FileSize.CompareTo(b.FileSize);
                else if (name == "Status") v = a.Status.CompareTo(b.Status);
                else if (name == "Pending") v = (IsPending(a) ? 0 : 1).CompareTo(IsPending(b) ? 0 : 1);
                else v = string.Compare(Val(a, name) ?? "", Val(b, name) ?? "", StringComparison.OrdinalIgnoreCase);
                return asc ? v : -v;
            });
        }

        static string Val(FileRecord r, string col)
        {
            switch (col)
            {
                case "FileName": return r.FileName;
                case "Supplier": return r.Supplier;
                case "DocType": return r.DocType;
                case "PartNo": return UiUtil.CodeValue(r, CodeTypes.PartNo);
                case "VwNo": return UiUtil.CodeValue(r, CodeTypes.VwNo);
                case "IdentNo": return UiUtil.CodeValue(r, CodeTypes.IdentNo);
                case "BmgNo": return r.BmgNo;
                case "MTime": return r.FileMTime;
                default: return "";
            }
        }

        // ================= 预览 / 操作 =================

        void PreviewCurrent()
        {
            var r = Current();
            _preview.Show(r);
        }

        FileRecord Current()
        {
            if (_grid.CurrentRow == null) return null;
            int i = _grid.CurrentRow.Index;
            if (i < 0 || i >= _view.Count) return null;
            return _view[i];
        }

        void WithCurrent(Action<FileRecord> act)
        {
            var r = Current();
            if (r != null) act(r);
        }

        void EditRecord(FileRecord r)
        {
            using (var dlg = new EditArchiveForm(r))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) ReloadAll();
            }
        }

        void BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("打开文件");
            var miFolder = new ToolStripMenuItem("打开所在文件夹");
            var miEdit = new ToolStripMenuItem("编辑档案…");
            var miCopyPath = new ToolStripMenuItem("复制完整路径");
            var miCopyHash = new ToolStripMenuItem("复制 SHA-256");
            miOpen.Click += delegate { WithCurrent(r => UiUtil.OpenFile(this, r.CurrentPath)); };
            miFolder.Click += delegate { WithCurrent(r => UiUtil.OpenFolder(this, r.CurrentPath)); };
            miEdit.Click += delegate { WithCurrent(EditRecord); };
            miCopyPath.Click += delegate { WithCurrent(r => UiUtil.CopyClipboard(r.CurrentPath)); };
            miCopyHash.Click += delegate { WithCurrent(r => UiUtil.CopyClipboard(r.Hash)); };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miFolder, miEdit, new ToolStripSeparator(), miCopyPath, miCopyHash });
            _grid.ContextMenuStrip = menu;
            _grid.CellMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                    _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
            };
        }

        // ================= 工具条动作 =================

        void DoSearch()
        {
            _keyword = _tbSearch.Text.Trim();
            RefreshView();
        }

        void OpenImport(IEnumerable<string> initialFiles)
        {
            if (_importDlg.Alive) { _importDlg.Dialog.Activate(); return; }
            var dlg = new ImportDialog(() => _main.RefreshAll(), initialFiles);
            _importDlg.Dialog = dlg;
            dlg.Show(FindForm());
        }

        void OpenScan()
        {
            if (_scanDlg.Alive) { _scanDlg.Dialog.Activate(); return; }
            var dlg = new ScanDialog(() => _main.RefreshAll());
            _scanDlg.Dialog = dlg;
            dlg.Show(FindForm());
        }

        // ================= 启动/刷新时的"新文件"检查 =================

        /// <summary>
        /// 后台核对归档目录，只发现"未入库"（有人直接把文件拷进归档目录的情况），
        /// 纯只读：路径变更、丢失、重复一律不动，那些交给【扫描目录】由用户确认。
        /// 结果不弹模态框打断，只点亮工具条上的橙色按钮 + 状态栏提示。
        /// </summary>
        async void AutoCheckNewFiles(bool pruned)
        {
            if (_autoChecking) return;
            var roots = ArchiveRoots.Paths();
            if (roots.Count == 0) return;

            _autoChecking = true;
            _btnNew.Visible = false;
            _lblStatus.Text = pruned ? "正在检查归档目录里的新文件…" : "正在核对归档目录（全量）…";
            try
            {
                var prog = new Progress<ScanProgress>(p =>
                    _lblStatus.Text = string.Format(
                        (pruned ? "正在检查归档目录里的新文件… {0}/{1}　{2}" : "正在核对归档目录（全量）… {0}/{1}　{2}"),
                        p.Done, p.Total, p.Current));
                ScanOutput outp;
                if (pruned)
                {
                    // 增量剪枝：只进"目录快照有变化"的目录，稳态几乎不碰磁盘文件
                    outp = await Task.Run(delegate
                    {
                        return ScanCore.ScanForNewFiles(roots, prog, System.Threading.CancellationToken.None);
                    });
                }
                else
                {
                    // 全量核对：兜底剪枝盲区（robocopy 刷时间戳等），结束时刷新快照
                    outp = await ScanCore.ScanAsync(roots, false, prog, System.Threading.CancellationToken.None);
                }
                _newRows = outp.Rows.Where(r => r.Category == ScanCategory.Unregistered).ToList();
            }
            catch
            {
                // 共享盘没连上、目录不可达等：静默放过，别在启动时弹框
                _newRows = new List<ScanRow>();
            }
            finally { _autoChecking = false; }

            UpdateNewFileHint();
            RefreshView();
        }

        void UpdateNewFileHint()
        {
            int n = _newRows.Count;
            _btnNew.Visible = n > 0;
            if (n > 0) _btnNew.Text = "发现 " + n + " 个新文件 → 补入库";
        }

        void OpenTypeAssign(DirNode dn)
        {
            using (var dlg = new TypeAssignForm(dn.FullPath, dn.Name))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (dlg.Changed > 0) ReloadAll();
            }
        }

        void OpenAdoptNew()
        {
            var rows = _newRows;
            if (rows == null || rows.Count == 0) return;
            using (var dlg = new AdoptForm(rows))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            }
            _newRows = new List<ScanRow>();
            UpdateNewFileHint();
            ReloadAll();
        }

        // 对话框句柄容器（关闭后置空，避免持有已释放窗体）
        class ImportDialogLink { public ImportDialog Dialog; public bool Alive { get { return Dialog != null && !Dialog.IsDisposed; } } }
        class ScanDialogLink { public ScanDialog Dialog; public bool Alive { get { return Dialog != null && !Dialog.IsDisposed; } } }
    }
}
