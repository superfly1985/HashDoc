using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 目录扫描窗口（从主页面工具条打开，非模态）：遍历归档根目录核对档案。
    /// 扫描模式（自动静默更新 / 确认后更新）在【设置】页配置。
    /// </summary>
    public class ScanDialog : Form
    {
        readonly Action _onChanged;
        readonly SmoothGrid _grid = new SmoothGrid();
        readonly Button _btnStart = UiUtil.Btn("开始扫描", 100);
        readonly Button _btnCancel = UiUtil.Btn("取消", 80);
        readonly Button _btnApply = UiUtil.Btn("应用勾选项", 110);
        readonly Button _btnAdopt = UiUtil.Btn("补入库…", 100);
        readonly ProgressBar _bar = new ProgressBar { Height = 16 };
        readonly Label _status = UiUtil.Lbl("就绪");
        readonly Label _lblMode = UiUtil.Lbl("");
        readonly Label _lblRoot = UiUtil.Lbl("");
        readonly ComboBox _cbScope = UiUtil.Combo(380);
        readonly Label _summary = UiUtil.Lbl("");
        List<ScanRow> _rows = new List<ScanRow>();
        CancellationTokenSource _cts;
        bool _busy;

        public ScanDialog(Action onChanged)
        {
            _onChanged = onChanged;
            Text = "扫描目录";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(860, 520);
            ShowInTaskbar = false;

            // ===== 顶部：扫描模式 / 按钮 / 范围 / 目录清单 / 进度 =====
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(12, 8, 12, 6),
                Margin = Padding.Empty,
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 扫描模式
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // 按钮行
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));  // 扫描范围
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 目录清单
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));  // 进度 + 状态

            _lblMode.ForeColor = Color.DimGray;
            var modeRow = UiUtil.Row(UiUtil.Lbl("扫描模式：", bold: true), _lblMode);
            modeRow.Dock = DockStyle.Fill;

            _btnStart.Click += async delegate { await StartScan(); };
            _btnCancel.Enabled = false;
            _btnCancel.Click += delegate { if (_cts != null) _cts.Cancel(); };
            _btnApply.Visible = false;
            _btnApply.Click += delegate { ApplyChecked(); };
            _btnAdopt.Visible = false;
            _btnAdopt.Click += delegate { AdoptUnregistered(); };
            var btnRow = UiUtil.Row(_btnStart, _btnCancel, _btnApply, _btnAdopt);
            btnRow.Dock = DockStyle.Fill;

            var scopeRow = UiUtil.Row(UiUtil.Lbl("扫描范围"), _cbScope);
            scopeRow.Dock = DockStyle.Fill;

            _lblRoot.AutoSize = false;
            _lblRoot.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _lblRoot.Height = 18;
            _lblRoot.AutoEllipsis = true;
            _lblRoot.TextAlign = ContentAlignment.MiddleLeft;
            _lblRoot.Margin = new Padding(0, 2, 0, 4);

            var prog = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            prog.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            prog.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            _bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _bar.Height = 14;
            _bar.Margin = new Padding(0, 6, 12, 0);
            _status.AutoSize = false;
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _status.Height = 20;
            _status.AutoEllipsis = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Margin = new Padding(0, 3, 0, 0);
            prog.Controls.Add(_bar, 0, 0);
            prog.Controls.Add(_status, 1, 0);

            top.Controls.Add(modeRow, 0, 0);
            top.Controls.Add(btnRow, 0, 1);
            top.Controls.Add(scopeRow, 0, 2);
            top.Controls.Add(_lblRoot, 0, 3);
            top.Controls.Add(prog, 0, 4);

            _summary.AutoSize = false;
            _summary.Dock = DockStyle.Fill;
            _summary.AutoEllipsis = true;
            _summary.TextAlign = ContentAlignment.MiddleLeft;
            _summary.Margin = new Padding(12, 2, 12, 6);

            BuildGrid();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 顶部工具区
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 表格
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // 底部统计

            _grid.Dock = DockStyle.Fill;
            _grid.Margin = Padding.Empty;
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(_summary, 0, 2);
            Controls.Add(root);

            _grid.CellValueChanged += GridCellValueChanged;
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.ContextMenuStrip = BuildMenu();

            VisibleChanged += delegate
            {
                bool auto = AppSettings.ScanMode() == "auto";
                _lblMode.Text = auto
                    ? "自动静默更新路径（在【设置】中可更改）"
                    : "列出差异，确认后更新（在【设置】中可更改）";
                FillScopes();
            };
        }

        /// <summary>扫描范围下拉：全部目录 / 某一个目录。</summary>
        void FillScopes()
        {
            var paths = ArchiveRoots.Paths();
            string keep = _cbScope.SelectedItem as string;
            _cbScope.Items.Clear();
            _cbScope.Items.Add("全部目录（" + paths.Count + " 个）");
            foreach (var p in paths) _cbScope.Items.Add(p);

            int idx = 0;
            if (!string.IsNullOrEmpty(keep))
            {
                int i = _cbScope.Items.IndexOf(keep);
                if (i > 0) idx = i;
            }
            _cbScope.SelectedIndex = idx;

            _lblRoot.Text = paths.Count == 0
                ? "归档目录：（未设置，请先在【设置】中添加）"
                : "归档目录（" + paths.Count + " 个）：" + string.Join(" ｜ ", paths.ToArray());
        }

        /// <summary>当前要扫描的目录集合；返回空表示没有可扫描的有效目录。</summary>
        List<string> ScopedRoots()
        {
            var roots = new List<string>();
            if (_cbScope.SelectedIndex <= 0) roots.AddRange(ArchiveRoots.Paths());
            else if (_cbScope.SelectedItem is string s) roots.Add(s);
            roots.RemoveAll(r => !Directory.Exists(r));
            return roots;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                MessageBox.Show(this, "扫描进行中，请等待完成，或先点【取消】终止扫描。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        void BuildGrid()
        {
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Apply", HeaderText = "应用", Width = 46 });
            AddText("Category", "分类", 85);
            AddText("File", "文件路径", 330);
            AddText("Detail", "说明", 250);
            AddText("State", "状态", 70);
            _grid.ReadOnly = false;
            foreach (DataGridViewColumn c in _grid.Columns)
                if (c.Name != "Apply") c.ReadOnly = true;
        }

        void AddText(string name, string header, int weight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        async Task StartScan()
        {
            if (_busy) return;
            var roots = ScopedRoots();
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "请先在【设置】的归档目录清单中添加有效目录，再选择扫描范围。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _busy = true;
            _cts = new CancellationTokenSource();
            _btnStart.Enabled = false;
            _btnCancel.Enabled = true;
            _btnApply.Visible = false;
            _btnAdopt.Visible = false;
            _grid.Rows.Clear();
            _rows.Clear();
            try
            {
                bool auto = AppSettings.ScanMode() == "auto";
                int lastMax = 0;
                var prog = new Progress<ScanProgress>(p =>
                {
                    if (p.Total != lastMax) { lastMax = p.Total; _bar.Maximum = Math.Max(1, p.Total); }
                    _bar.Value = Math.Min(_bar.Maximum, p.Done);
                    _status.Text = "扫描中 (" + p.Done + "/" + p.Total + ")：" + p.Current;
                });

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var output = await ScanCore.ScanAsync(roots, auto, prog, _cts.Token);
                sw.Stop();

                _rows = output.Rows;
                FillGrid();
                _summary.Text = string.Format(
                    "扫描完成（{0:F1} 秒）：共 {1} 个文件，计算哈希 {2} 个 ｜ 路径变更 {3}，丢失 {4}，重复 {5}，未入库 {6}，内容变化 {7}" +
                    (auto && output.AutoApplied > 0 ? "，已自动处理 " + output.AutoApplied + " 项" : ""),
                    sw.Elapsed.TotalSeconds, output.TotalFiles, output.HashedFiles,
                    _rows.Count(x => x.Category == ScanCategory.PathChanged),
                    _rows.Count(x => x.Category == ScanCategory.Lost),
                    _rows.Count(x => x.Category == ScanCategory.Duplicate),
                    _rows.Count(x => x.Category == ScanCategory.Unregistered),
                    _rows.Count(x => x.Category == ScanCategory.ContentChanged));
                _status.Text = "扫描完成";
                _btnAdopt.Visible = _rows.Any(x => x.Category == ScanCategory.Unregistered);
                _btnApply.Visible = !auto && _rows.Any(x => x.CanApply && !x.Applied);
                if (auto && output.AutoApplied > 0 && _onChanged != null) _onChanged();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "已取消";
                _summary.Text = "扫描已取消。";
            }
            catch (Exception ex)
            {
                UiUtil.ShowError(this, "扫描失败", ex);
            }
            finally
            {
                _busy = false;
                _btnStart.Enabled = true;
                _btnCancel.Enabled = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        void FillGrid()
        {
            foreach (var r in _rows)
            {
                int i = _grid.Rows.Add(r.Apply, r.CategoryText, r.FilePath, r.Detail, StateText(r));
                var row = _grid.Rows[i];
                row.Tag = r;
                row.Cells["File"].ToolTipText = r.FilePath;
                if (r.Category == ScanCategory.Lost) row.Cells["Category"].Style.ForeColor = UiUtil.LostRed;
                else if (r.Category == ScanCategory.Duplicate) row.Cells["Category"].Style.ForeColor = UiUtil.DupBrown;
                else if (r.Category == ScanCategory.Unregistered) row.Cells["Category"].Style.ForeColor = UiUtil.UnregBlue;
            }
        }

        string StateText(ScanRow r)
        {
            if (r.Applied) return "已处理";
            if (!r.CanApply) return "—";
            return r.Apply ? "待确认" : "不应用";
        }

        void GridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Apply") return;
            var row = _grid.Rows[e.RowIndex];
            var r = row.Tag as ScanRow;
            if (r == null) return;
            bool val = Convert.ToBoolean(row.Cells["Apply"].Value ?? false);
            if (!r.CanApply)
            {
                row.Cells["Apply"].Value = false;
                return;
            }
            r.Apply = val;
            row.Cells["State"].Value = StateText(r);
        }

        void ApplyChecked()
        {
            try
            {
                int n = ScanCore.Apply(_rows);
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var r = row.Tag as ScanRow;
                    if (r != null && r.Applied) row.Cells["State"].Value = "已处理";
                }
                _summary.Text = "已应用 " + n + " 项更新。";
                _btnApply.Visible = _rows.Any(x => x.CanApply && !x.Applied);
                if (n > 0 && _onChanged != null) _onChanged();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "更新失败", ex); }
        }

        void AdoptUnregistered()
        {
            var pending = _rows.Where(x => x.Category == ScanCategory.Unregistered && !x.Applied).ToList();
            if (pending.Count == 0) return;
            using (var dlg = new AdoptForm(pending))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var imported = new HashSet<string>(dlg.ImportedHashes, StringComparer.Ordinal);
                    foreach (DataGridViewRow row in _grid.Rows)
                    {
                        var r = row.Tag as ScanRow;
                        if (r != null && r.Category == ScanCategory.Unregistered && r.Hash != null && imported.Contains(r.Hash))
                        {
                            r.Applied = true;
                            row.Cells["State"].Value = "已入库";
                        }
                    }
                    _summary.Text = "已补入库 " + dlg.ImportedHashes.Count + " 个文件。";
                    if (dlg.ImportedHashes.Count > 0 && _onChanged != null) _onChanged();
                }
            }
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("打开文件");
            var miFolder = new ToolStripMenuItem("打开所在文件夹");
            var miDelDup = new ToolStripMenuItem("删除此重复文件（仅磁盘副本）");
            miOpen.Click += delegate { WithCurrent(r => UiUtil.OpenFile(this, r.FilePath)); };
            miFolder.Click += delegate { WithCurrent(r => UiUtil.OpenFolder(this, r.FilePath)); };
            miDelDup.Click += delegate { DeleteDuplicate(); };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miFolder, miDelDup });
            return menu;
        }

        void WithCurrent(Action<ScanRow> act)
        {
            if (_grid.CurrentRow == null) return;
            var r = _grid.CurrentRow.Tag as ScanRow;
            if (r != null) act(r);
        }

        void DeleteDuplicate()
        {
            WithCurrent(r =>
            {
                if (r.Category != ScanCategory.Duplicate)
                {
                    MessageBox.Show(this, "请对【重复文件】行使用此操作。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var ans = MessageBox.Show(this,
                    "确定删除磁盘上的重复文件吗？\n" + r.FilePath +
                    "\n\n库内档案及其归档文件不受影响，此操作不可恢复。",
                    "删除重复文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans != DialogResult.Yes) return;
                try
                {
                    File.Delete(r.FilePath);
                    r.Applied = true;
                    foreach (DataGridViewRow row in _grid.Rows)
                        if (row.Tag == r)
                        {
                            row.Cells["State"].Value = "已删除";
                            row.Cells["Apply"].Value = false;
                        }
                    _summary.Text = "已删除重复文件（库内档案保留）。";
                }
                catch (Exception ex) { UiUtil.ShowError(this, "删除失败", ex); }
            });
        }
    }
}
