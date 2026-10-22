using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 文件入库窗口（从主页面工具条打开，非模态）：多选/拖入文件 → 录入属性
    /// （供应商单选 + DocType + 多条编号）→ 后台哈希并复制/移动归档。
    /// </summary>
    public class ImportDialog : Form
    {
        readonly Action _onChanged;
        readonly ListView _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
        };
        readonly ComboBox _cbSupplier = UiUtil.Combo(200);
        readonly ComboBox _cbDocType = UiUtil.Combo(200);
        readonly ComboBox _cbBmg = UiUtil.ComboEdit(220);
        readonly ComboBox _cbRoot = UiUtil.Combo(250);
        readonly Label _lblRoot = UiUtil.Lbl("归档到");
        readonly GroupBox _gbMode = new GroupBox { Text = "入库方式" };
        readonly RadioButton _rbCopy = new RadioButton { Text = "复制到归档目录（保留源文件）", AutoSize = true, Checked = true };
        readonly RadioButton _rbMove = new RadioButton { Text = "移动到归档目录（导入后删除源文件）", AutoSize = true };
        readonly CodeListEditor _edPart = new CodeListEditor("供应商零件号（可多条）", CodeTypes.PartNo);
        readonly CodeListEditor _edVw = new CodeListEditor("VW客户号（可多条）", CodeTypes.VwNo);
        readonly CodeListEditor _edIdent = new CodeListEditor("ident内部件号（可多条）", CodeTypes.IdentNo);
        readonly TextBox _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
        };
        readonly Button _btnStart = UiUtil.Btn("开始入库", 110);
        readonly Button _btnCancel = UiUtil.Btn("取消", 80);
        readonly Label _status = UiUtil.Lbl("就绪");
        CancellationTokenSource _cts;
        bool _busy;

        public ImportDialog(Action onChanged, IEnumerable<string> initialFiles = null)
        {
            _onChanged = onChanged;
            Text = "添加文件 — 入库";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 736);
            MinimumSize = new Size(880, 620);
            ShowInTaskbar = false;

            // ===== 左栏：待入库文件列表 =====
            _list.Columns.Add("待入库文件（支持直接拖入）", 470);
            _list.Columns.Add("大小", 90);
            _list.AllowDrop = true;
            _list.Resize += delegate
            {
                // 文件名列吃剩余宽度，避免“大小”列被挤出可视区
                int w = _list.ClientSize.Width - _list.Columns[1].Width - 4;
                if (w > 160) _list.Columns[0].Width = w;
            };
            _list.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            _list.DragDrop += (s, e) => AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));

            var btnPick = UiUtil.Btn("选择文件…", 100);
            btnPick.Click += delegate { PickFiles(); };
            var btnRemove = UiUtil.Btn("移除所选", 90);
            btnRemove.Click += delegate { foreach (ListViewItem it in _list.SelectedItems) _list.Items.Remove(it); };
            var btnClear = UiUtil.Btn("清空", 70);
            btnClear.Click += delegate { _list.Items.Clear(); };
            var btnRow = UiUtil.Row(btnPick, btnRemove, btnClear, _status);
            btnRow.Dock = DockStyle.Fill;

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 8, 0),
            };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _list.Dock = DockStyle.Fill;
            _list.Margin = Padding.Empty;
            left.Controls.Add(btnRow, 0, 0);
            left.Controls.Add(_list, 0, 1);

            // ===== 右栏：入库方式 + 档案属性 + 执行 =====
            _rbCopy.CheckedChanged += delegate { if (_rbCopy.Checked) AppSettings.MoveOnImport(false); };
            _rbMove.CheckedChanged += delegate { if (_rbMove.Checked) AppSettings.MoveOnImport(true); };

            var modeInner = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(0, 2, 0, 0),
            };
            modeInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modeInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            modeInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            modeInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            modeInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _rbCopy.Margin = new Padding(2, 3, 0, 2);
            _rbMove.Margin = new Padding(2, 2, 0, 2);
            _lblRoot.Margin = new Padding(2, 7, 8, 2);
            _lblRoot.Anchor = AnchorStyles.Left;
            _cbRoot.Dock = DockStyle.Fill;
            _cbRoot.Margin = new Padding(0, 4, 2, 2);
            modeInner.Controls.Add(_rbCopy, 0, 0);
            modeInner.SetColumnSpan(_rbCopy, 2);
            modeInner.Controls.Add(_rbMove, 0, 1);
            modeInner.SetColumnSpan(_rbMove, 2);
            modeInner.Controls.Add(_lblRoot, 0, 2);
            modeInner.Controls.Add(_cbRoot, 1, 2);

            _gbMode.Dock = DockStyle.Top;
            _gbMode.AutoSize = true;
            _gbMode.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _gbMode.Padding = new Padding(8, 4, 8, 6);
            _gbMode.Margin = new Padding(0, 0, 0, 6);
            _gbMode.Controls.Add(modeInner);

            var gbAttr = new GroupBox
            {
                Text = "档案属性",
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 6),
                Margin = new Padding(0, 0, 0, 6),
            };
            var attrInner = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 356,
                ColumnCount = 1,
                RowCount = 7,
            };
            attrInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            attrInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var btnAddSup = UiUtil.Btn("＋新增", 62);
            btnAddSup.Click += delegate { AddSupplier(); };
            var supRow = UiUtil.Row(UiUtil.Lbl("供应商 *"), _cbSupplier, btnAddSup);
            supRow.Dock = DockStyle.Fill;
            var typeRow = UiUtil.Row(UiUtil.Lbl("文档类型"), _cbDocType);
            typeRow.Dock = DockStyle.Fill;
            // BMG 号是单值字段（可事后补），打字有历史候选
            var bmgRow = UiUtil.Row(UiUtil.Lbl("BMG 号"), _cbBmg);
            bmgRow.Dock = DockStyle.Fill;

            _edPart.Dock = DockStyle.Fill;
            _edPart.Margin = new Padding(0, 2, 0, 4);
            _edVw.Dock = DockStyle.Fill;
            _edVw.Margin = new Padding(0, 2, 0, 4);
            _edIdent.Dock = DockStyle.Fill;
            _edIdent.Margin = new Padding(0, 2, 0, 2);

            attrInner.Controls.Add(supRow, 0, 0);
            attrInner.Controls.Add(typeRow, 0, 1);
            attrInner.Controls.Add(bmgRow, 0, 2);
            attrInner.Controls.Add(_edPart, 0, 3);
            attrInner.Controls.Add(_edVw, 0, 4);
            attrInner.Controls.Add(_edIdent, 0, 5);

            // 窗口压到最小时编号区不压扁，而是给滚动条；此时只出纵向滚动条
            var attrScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            attrScroll.Controls.Add(attrInner);
            gbAttr.Controls.Add(attrScroll);

            _btnStart.Click += delegate { StartImport(); };
            _btnCancel.Enabled = false;
            _btnCancel.Click += delegate { if (_cts != null) _cts.Cancel(); };
            var gbGo = new GroupBox
            {
                Text = "执行",
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 6),
                Margin = Padding.Empty,
            };
            var goRow = UiUtil.Row(_btnStart, _btnCancel);
            goRow.Dock = DockStyle.Fill;
            gbGo.Controls.Add(goRow);

            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
            };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 入库方式
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 档案属性
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));  // 执行
            right.Controls.Add(_gbMode, 0, 0);
            right.Controls.Add(gbAttr, 0, 1);
            right.Controls.Add(gbGo, 0, 2);

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(left, 0, 0);
            main.Controls.Add(right, 1, 0);

            // ===== 底部：入库日志 =====
            var lblLog = UiUtil.Lbl("入库日志", bold: true);
            lblLog.Margin = new Padding(0, 0, 0, 2);
            var logCell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12, 4, 12, 8),
                Margin = Padding.Empty,
            };
            logCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logCell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _log.Dock = DockStyle.Fill;
            _log.Margin = Padding.Empty;
            logCell.Controls.Add(lblLog, 0, 0);
            logCell.Controls.Add(_log, 0, 1);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(10, 8, 10, 0),
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            root.Controls.Add(main, 0, 0);
            root.Controls.Add(logCell, 0, 1);

            Controls.Add(root);

            bool init = initialFiles != null;
            Load += delegate
            {
                ReloadDicts();
                ReloadRoots();
                _rbMove.Checked = AppSettings.MoveOnImport();
                if (init) AddFiles(initialFiles);
            };
        }

        /// <summary>只有一个归档目录时不显示"归档到"，界面和原先一致。</summary>
        void ReloadRoots()
        {
            _cbRoot.Items.Clear();
            foreach (var p in ArchiveRoots.Paths()) _cbRoot.Items.Add(p);
            bool multi = _cbRoot.Items.Count > 1;
            _lblRoot.Visible = multi;
            _cbRoot.Visible = multi;
            if (_cbRoot.Items.Count > 0) _cbRoot.SelectedIndex = 0;
            // 分组随内容自适应高度（隐藏"归档到"那一行后自动收窄）
            _gbMode.PerformLayout();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                MessageBox.Show(this, "入库进行中，请等待完成，或先点【取消】终止本次入库。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        public void ReloadDicts()
        {
            string keepS = _cbSupplier.Text;
            string keepT = _cbDocType.Text;
            _cbSupplier.Items.Clear();
            foreach (var s in DictRepo.Suppliers()) _cbSupplier.Items.Add(s.Name);
            UiUtil.AutoComplete(_cbSupplier, _cbSupplier.Items.Cast<string>());
            _cbDocType.Items.Clear();
            foreach (var t in DictRepo.DocTypes()) _cbDocType.Items.Add(t.Name);
            UiUtil.AutoComplete(_cbDocType, _cbDocType.Items.Cast<string>());
            _cbBmg.Items.Clear();
            foreach (var b in FileRepo.DistinctBmgNos()) _cbBmg.Items.Add(b);
            UiUtil.AutoComplete(_cbBmg, _cbBmg.Items.Cast<string>());
            if (keepS != null && _cbSupplier.Items.Contains(keepS)) _cbSupplier.SelectedItem = keepS;
            if (keepT != null && _cbDocType.Items.Contains(keepT)) _cbDocType.SelectedItem = keepT;
        }

        void AddFiles(IEnumerable<string> paths)
        {
            int skipped = 0;
            var existing = new HashSet<string>(
                _list.Items.Cast<ListViewItem>().Select(i => i.Text),
                StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                if (!FileTypes.IsSupported(p)) { skipped++; continue; }
                if (existing.Contains(p)) continue;
                existing.Add(p);
                var it = new ListViewItem(p);
                it.SubItems.Add(UiUtil.FormatSize(new FileInfo(p).Length));
                _list.Items.Add(it);
            }
            if (skipped > 0)
                Log("已跳过 " + skipped + " 个不支持的文件（仅支持 PDF/JPG/PNG/BMP/TIFF）");
            AutoFillSupplierFromBmg();
        }

        /// <summary>
        /// 02 BMG 下的文件按目录默认供应商：仅当清单里所有文件推得的供应商一致、
        /// 该名字在字典里、且用户还没选过供应商时才填，绝不覆盖人工选择，填了也注明可改。
        /// </summary>
        void AutoFillSupplierFromBmg()
        {
            if (_cbSupplier.SelectedItem != null || _list.Items.Count == 0) return;
            var known = DictRepo.Suppliers().Select(x => x.Name).ToList();
            string hit = null;
            foreach (var p in _list.Items.Cast<ListViewItem>().Select(i => i.Text))
            {
                string s = PathRules.InferSupplierFromBmg(p, known);
                if (s == null) return;   // 有一个推不出就不预填，宁缺勿错
                if (hit == null) hit = s;
                else if (!string.Equals(hit, s, StringComparison.OrdinalIgnoreCase)) return;
            }
            if (hit != null && _cbSupplier.Items.Contains(hit))
            {
                _cbSupplier.SelectedItem = hit;
                Log("已按 02 BMG 目录默认供应商：" + hit + "（可改）");
            }
        }

        void PickFiles()
        {
            using (var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = FileTypes.DialogFilter,
                Title = "选择要入库的文件",
            })
            {
                string root = AppSettings.RootDir();
                if (Directory.Exists(root)) dlg.InitialDirectory = root;
                if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
            }
        }

        void AddSupplier()
        {
            var r = DictEditForm.Show(this, "新增供应商", "供应商名称");
            if (r == null) return;
            try
            {
                if (DictRepo.SupplierExists(r[0]))
                {
                    MessageBox.Show(this, "该供应商已存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DictRepo.SupplierAdd(r[0], r[1]);
                _cbSupplier.Items.Add(r[0]);
                _cbSupplier.SelectedItem = r[0];
                if (_onChanged != null) _onChanged();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "新增失败", ex); }
        }

        List<CodeEntry> CollectCodes()
        {
            var list = new List<CodeEntry>();
            foreach (var v in _edPart.Values) list.Add(new CodeEntry(CodeTypes.PartNo, v));
            foreach (var v in _edVw.Values) list.Add(new CodeEntry(CodeTypes.VwNo, v));
            foreach (var v in _edIdent.Values) list.Add(new CodeEntry(CodeTypes.IdentNo, v));
            return list;
        }

        async void StartImport()
        {
            if (_busy) return;
            if (_list.Items.Count == 0)
            {
                MessageBox.Show(this, "请先选择要入库的文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string supplier = _cbSupplier.SelectedItem as string;
            if (string.IsNullOrEmpty(supplier))
            {
                MessageBox.Show(this, "请选择供应商。供应商清单可在【设置】中维护，或点右侧【＋新增】。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string doctype = _cbDocType.SelectedItem as string ?? "";
            string bmgNo = (_cbBmg.Text ?? "").Trim();
            var opt = new ImportOptions
            {
                MoveSource = _rbMove.Checked,
                RootDir = _cbRoot.Visible ? (_cbRoot.SelectedItem as string ?? "") : ArchiveRoots.Default(),
                Supplier = supplier,
                DocType = doctype,
                BmgNo = bmgNo,
                Codes = CollectCodes(),
            };
            var files = _list.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();

            _busy = true;
            _cts = new CancellationTokenSource();
            _btnStart.Enabled = false;
            _btnCancel.Enabled = true;
            try
            {
                var prog = new Progress<ImportProgress>(p =>
                    _status.Text = "(" + p.Done + "/" + p.Total + ") " + p.Phase + "：" + p.Current);
                Log("―― 开始入库 " + files.Count + " 个文件，供应商=" + supplier +
                    (doctype != "" ? "，类型=" + doctype : "") +
                    "，归档到=" + opt.RootDir +
                    (opt.MoveSource ? "，方式=移动" : "，方式=复制"));
                var results = await ImportCore.ImportAsync(files, opt, prog, _cts.Token);
                int ok = 0, dup = 0, fail = 0;
                foreach (var r in results)
                {
                    if (r.Success) { ok++; Log("√ " + r.Message); }
                    else if (r.Duplicate) { dup++; Log("○ " + r.Message); }
                    else { fail++; Log("× " + Path.GetFileName(r.Source) + "：" + r.Message); }
                }
                Log("―― 完成：成功 " + ok + "，重复跳过 " + dup + "，失败 " + fail);
                _list.Items.Clear();
                _status.Text = "完成：成功 " + ok + "，重复 " + dup + "，失败 " + fail;
                if (_onChanged != null && ok > 0) _onChanged();
            }
            catch (OperationCanceledException)
            {
                Log("―― 入库已取消（已处理文件不受影响）。");
                _status.Text = "已取消";
            }
            catch (Exception ex)
            {
                UiUtil.ShowError(this, "入库失败", ex);
            }
            finally
            {
                _busy = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                _btnStart.Enabled = true;
                _btnCancel.Enabled = false;
            }
        }

        void Log(string line)
        {
            _log.AppendText("[" + AppSettings.NowStr() + "] " + line + Environment.NewLine);
        }
    }
}
