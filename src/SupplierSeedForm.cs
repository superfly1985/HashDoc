using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 从文件清单（Excel 另存的 CSV / 每行一个路径的 TXT）一次性播种供应商字典。
    /// 只认一条固定规则：取"目录标记"（默认 02 BMG）紧邻的下一层文件夹名——实测各类型目录
    /// 层级不一致（ELV 下一层混着分类、PPAP 下一层是 PPAP(完整）/Supplier PPAP Report），
    /// 所以不再做"路径任意层级匹配"，名字是否真的是供应商由人工勾选确认。
    /// </summary>
    public class SupplierSeedForm : Form
    {
        readonly SmoothGrid _grid = new SmoothGrid();
        readonly Label _lblFile = UiUtil.LblFill("尚未选择文件清单", Color.DimGray);
        readonly TextBox _tbAnchor = UiUtil.Txt(110);
        readonly TextBox _tbFilter = UiUtil.Txt(160);
        readonly Label _lblCount = UiUtil.Lbl("");
        readonly HashSet<string> _existing;
        List<HarvestItem> _items = new List<HarvestItem>();
        string _listFile = "";

        public SupplierSeedForm()
        {
            _existing = new HashSet<string>(
                DictRepo.Suppliers().Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

            Text = "从文件清单导入供应商";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(820, 500);

            var lblTop = new Label
            {
                Text = "选一份文件清单（Excel 另存的 CSV，或每行一个完整路径的 TXT），程序取「目录标记」紧邻的下一层文件夹名作为供应商候选。\n" +
                       "默认标记是 02 BMG——实测只有它下面紧跟着的就是供应商；其它类型目录层级不一致，不自动猜。\n" +
                       "直接散落在该目录下的文件（Thumbs.db、xx模板.xlsx、new folder 1.zip）会自动忽略，不进候选。\n" +
                       "候选默认全部勾选，明显不是供应商的（分类目录、说明性文件夹）请取消勾选；已在字典中的会自动跳过。",
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = UiUtil.Font9,
                Padding = new Padding(12, 8, 12, 0),
                Margin = Padding.Empty,
            };

            // 第 1 行：选文件
            var btnPick = UiUtil.Btn("选择清单文件…", 130);
            btnPick.Click += delegate { PickFile(); };
            _lblFile.Margin = new Padding(0, 6, 0, 0);
            _lblFile.AutoEllipsis = true;
            var fileRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12, 4, 12, 2),
                Margin = Padding.Empty,
            };
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnPick.Anchor = AnchorStyles.Left;
            btnPick.Margin = new Padding(0, 0, 10, 0);
            fileRow.Controls.Add(btnPick, 0, 0);
            fileRow.Controls.Add(_lblFile, 1, 0);

            // 第 2 行：目录标记 + 名称筛选 + 批量勾选
            _tbAnchor.Text = "02 BMG";
            var btnRead = UiUtil.Btn("读取候选", 90);
            btnRead.Click += delegate { LoadItems(); };
            _tbFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFilter(); } };
            var btnFilter = UiUtil.Btn("筛选", 70);
            btnFilter.Click += delegate { ApplyFilter(); };
            var btnShowAll = UiUtil.Btn("显示全部", 80);
            btnShowAll.Click += delegate { _tbFilter.Text = ""; ApplyFilter(); };
            var btnAll = UiUtil.Btn("全选", 70);
            btnAll.Click += delegate { SetAll(true); };
            var btnNone = UiUtil.Btn("全不选", 80);
            btnNone.Click += delegate { SetAll(false); };

            var toolRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(12, 2, 12, 2),
                Margin = Padding.Empty,
            };
            foreach (var c in new Control[]
            {
                UiUtil.Lbl("目录标记"), _tbAnchor, btnRead,
                UiUtil.Lbl("　名称筛选"), _tbFilter, btnFilter, btnShowAll,
                UiUtil.Lbl("　"), btnAll, btnNone
            })
            {
                c.Margin = c is Label ? new Padding(0, 6, 6, 0) : new Padding(0, 4, 8, 0);
                toolRow.Controls.Add(c);
            }

            // 底部
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(12, 2, 12, 8),
                Margin = Padding.Empty,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _lblCount.AutoSize = false;
            _lblCount.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _lblCount.Height = 20;
            _lblCount.AutoEllipsis = true;
            _lblCount.Margin = new Padding(0, 8, 12, 0);

            var ok = UiUtil.Btn("加入字典", 110);
            ok.Anchor = AnchorStyles.Left;
            ok.Margin = new Padding(0, 8, 8, 0);
            ok.Click += delegate { Confirm(); };
            var cancel = UiUtil.Btn("取消", 90);
            cancel.Anchor = AnchorStyles.Left;
            cancel.Margin = new Padding(0, 8, 0, 0);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;

            bottom.Controls.Add(_lblCount, 0, 0);
            bottom.Controls.Add(cancel, 1, 0);
            bottom.Controls.Add(ok, 2, 0);

            BuildGrid();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 说明
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // 选文件
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // 工具行
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 表格
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // 统计 + 按钮

            _grid.Dock = DockStyle.Fill;
            _grid.Margin = Padding.Empty;
            root.Controls.Add(lblTop, 0, 0);
            root.Controls.Add(fileRow, 0, 1);
            root.Controls.Add(toolRow, 0, 2);
            root.Controls.Add(_grid, 0, 3);
            root.Controls.Add(bottom, 0, 4);
            Controls.Add(root);

            _lblCount.Text = "选好清单文件后点【读取候选】。";
        }

        void BuildGrid()
        {
            _grid.ReadOnly = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = "加入", Width = 46 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Name", HeaderText = "供应商名（目录名）", FillWeight = 46,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Count", HeaderText = "文件数", FillWeight = 12,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Sample", HeaderText = "样例路径", FillWeight = 70,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.DataError += delegate { };
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
        }

        void PickFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "选择文件清单（Excel 另存为 CSV，或每行一个路径的 TXT）",
                Filter = "清单文件 (*.csv;*.txt)|*.csv;*.txt|CSV (*.csv)|*.csv|文本 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _listFile = dlg.FileName;
                _lblFile.Text = _listFile;
                _lblFile.ForeColor = SystemColors.ControlText;
                LoadItems();
            }
        }

        void LoadItems()
        {
            if (string.IsNullOrEmpty(_listFile))
            {
                MessageBox.Show(this, "请先选择文件清单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                _items = PathRules.ReadSupplierSeeds(_listFile, _tbAnchor.Text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                UiUtil.ShowError(this, "读取清单失败", ex);
                _items = new List<HarvestItem>();
            }
            finally { Cursor = Cursors.Default; }

            int inDict = _items.Count(x => _existing.Contains(x.Name));
            _lblCount.Text = string.Format("候选 {0} 个（标记：{1}），其中 {2} 个已在字典中（灰色、自动跳过）。",
                _items.Count, _tbAnchor.Text.Trim(), inDict);
            ApplyFilter();
        }

        void ApplyFilter()
        {
            string kw = _tbFilter.Text.Trim();
            IEnumerable<HarvestItem> visible = _items;
            if (kw.Length > 0)
                visible = _items.Where(x => x.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var it in visible)
            {
                bool inDict = _existing.Contains(it.Name);
                int i = _grid.Rows.Add(!inDict, it.Name, it.FileCount.ToString(), it.SamplePath);
                var row = _grid.Rows[i];
                row.Tag = it;
                row.Cells["Sample"].ToolTipText = it.SamplePath;
                if (inDict)
                {
                    row.Cells["Sel"].Value = false;
                    row.Cells["Name"].Style.ForeColor = Color.Gray;
                    row.Cells["Name"].Style.SelectionForeColor = Color.Gray;
                    row.Cells["Name"].ToolTipText = "已在供应商字典中，加入时自动跳过";
                }
            }
            _grid.ResumeLayout();
        }

        void SetAll(bool v)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var it = row.Tag as HarvestItem;
                if (it != null && _existing.Contains(it.Name)) continue; // 已在字典的不勾
                row.Cells["Sel"].Value = v;
            }
        }

        void Confirm()
        {
            var selected = new List<string>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!Convert.ToBoolean(row.Cells["Sel"].Value ?? false)) continue;
                var it = row.Tag as HarvestItem;
                if (it == null || _existing.Contains(it.Name)) continue;
                if (!selected.Contains(it.Name)) selected.Add(it.Name);
            }
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "没有勾选任何可加入的候选。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "确定把 " + selected.Count + " 个名称加入供应商字典吗？\n加入后可在字典区继续改名、删除。",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            try
            {
                string remark = "来自文件清单 " + AppSettings.NowStr();
                foreach (var name in selected)
                    DictRepo.SupplierAdd(name, remark);
                MessageBox.Show(this, "已加入 " + selected.Count + " 个供应商。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "加入失败", ex); }
        }
    }
}
