using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    public class AdoptItem
    {
        public ScanRow Row;
        public string Supplier = "";
        public bool Checked = true;
    }

    /// <summary>
    /// 扫描发现“未入库文件”后的批量补入库。
    /// 供应商不再按路径猜（各类型目录层级不一致，会命中分类目录），逐行由人工填，
    /// 填的时候带随动检索；文档类型统一选择。直接登记建档，文件已在归档目录内，不复制不移动。
    /// </summary>
    public class AdoptForm : Form
    {
        readonly List<ScanRow> _files;
        readonly SmoothGrid _grid = new SmoothGrid();
        readonly ComboBox _cbDocType = UiUtil.Combo(150);
        readonly ComboBox _cbQuickSup = UiUtil.ComboEdit(150);
        readonly TextBox _tbFilter = UiUtil.Txt(220);
        readonly Label _lblCount = UiUtil.Lbl("");
        List<AdoptItem> _items = new List<AdoptItem>();
        List<string> _suppliers = new List<string>();
        readonly List<string> _importedHashes = new List<string>();

        public AdoptForm(List<ScanRow> files)
        {
            _files = files;
            Text = "未入库文件补入库（" + files.Count + " 个）";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(860, 520);

            var lblTop = new Label
            {
                Text = "以下文件位于归档目录内但不在档案库中。路径在 02 BMG 下一层的会自动按目录默认供应商（仅限字典里已有的名字），" +
                       "其余逐行指定或用【批量设置】统一填，也可以全部留空直接入库——回主列表勾选「只看待补」再逐个补填。" +
                       "确认后直接登记入库，不复制不移动文件。",
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = UiUtil.Font9,
                Padding = new Padding(12, 8, 12, 0),
                Margin = Padding.Empty,
            };

            // ===== 底部操作区：三行 =====
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12, 2, 12, 8),
                Margin = Padding.Empty,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // 筛选 + 批量设置
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // 文档类型 + 全选 + 入库
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 计数
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 提示

            // 第1行：左边筛选，右边批量设置供应商
            _tbFilter.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFilter(); } };
            var btnFilter = UiUtil.Btn("筛选", 70);
            btnFilter.Click += delegate { ApplyFilter(); };
            var btnShowAll = UiUtil.Btn("显示全部", 80);
            btnShowAll.Click += delegate { _tbFilter.Text = ""; ApplyFilter(); };
            var filterRow = UiUtil.Row(UiUtil.Lbl("路径筛选"), _tbFilter, btnFilter, btnShowAll);

            var btnQuickApply = UiUtil.Btn("应用到显示行", 110);
            btnQuickApply.Click += delegate { QuickApply(); };
            var quickRow = UiUtil.RowRight(UiUtil.Lbl("批量设置供应商"), _cbQuickSup, btnQuickApply);

            var row1 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row1.Controls.Add(filterRow, 0, 0);
            row1.Controls.Add(quickRow, 2, 0);

            // 第2行：左边文档类型 + 全选，右边入库按钮
            var btnAll = UiUtil.Btn("全选", 70);
            btnAll.Click += delegate { SetAll(true); };
            var btnNone = UiUtil.Btn("全不选", 80);
            btnNone.Click += delegate { SetAll(false); };
            var leftRow = UiUtil.Row(UiUtil.Lbl("文档类型"), _cbDocType, btnAll, btnNone);

            var ok = UiUtil.Btn("入库所选", 110);
            ok.Click += delegate { Confirm(); };
            var cancel = UiUtil.Btn("取消", 90);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            var rightRow = UiUtil.RowRight(cancel, ok);

            var row2 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row2.Controls.Add(leftRow, 0, 0);
            row2.Controls.Add(rightRow, 2, 0);

            _lblCount.AutoSize = false;
            _lblCount.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _lblCount.Height = 20;
            _lblCount.AutoEllipsis = true;
            _lblCount.Margin = new Padding(0, 4, 0, 0);

            var lblHint = UiUtil.LblFill("提示：供应商格子里直接打字有候选；字典里没有的，先到【数据维护】→【从文件清单导入供应商…】批量补。不填也能入库，主列表会用「待补」标出来。", Color.DimGray);
            lblHint.Margin = new Padding(0, 0, 0, 0);

            bottom.Controls.Add(row1, 0, 0);
            bottom.Controls.Add(row2, 0, 1);
            bottom.Controls.Add(_lblCount, 0, 2);
            bottom.Controls.Add(lblHint, 0, 3);

            BuildGrid();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 说明文字
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 表格
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 底部操作区

            _grid.Dock = DockStyle.Fill;
            _grid.Margin = Padding.Empty;
            root.Controls.Add(lblTop, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            Load += delegate
            {
                _suppliers = DictRepo.Suppliers().Select(x => x.Name).ToList();
                var doctypes = DictRepo.DocTypes().Select(x => x.Name).ToList();
                foreach (var t in doctypes) _cbDocType.Items.Add(t);
                foreach (var s in _suppliers) _cbQuickSup.Items.Add(s);
                UiUtil.AutoComplete(_cbQuickSup, _suppliers);
                UiUtil.AutoComplete(_cbDocType, doctypes);
                UiUtil.GridAutoComplete(_grid, "Supplier", _suppliers);

                // 只对 02 BMG 做默认供应商（下一层就是供应商，且必须在字典里才填）；其它目录留空由人工指定
                foreach (var f in _files)
                    _items.Add(new AdoptItem
                    {
                        Row = f,
                        Supplier = PathRules.InferSupplierFromBmg(f.FilePath, _suppliers) ?? "",
                    });

                ApplyFilter();
            };
        }

        public List<string> ImportedHashes { get { return _importedHashes; } }

        void BuildGrid()
        {
            _grid.ReadOnly = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = "入库", Width = 46 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "File", HeaderText = "文件路径", FillWeight = 100,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            var supCol = new DataGridViewTextBoxColumn
            {
                Name = "Supplier",
                HeaderText = "供应商",
                FillWeight = 34,
            };
            supCol.HeaderCell.ToolTipText = "打字有候选；字典里没有的也能直接填，入库时可以一并加进字典";
            _grid.Columns.Add(supCol);
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Size", HeaderText = "大小", Width = 80,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.DataError += delegate { }; // 编辑中途的非法值不要弹框打断

            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var row = _grid.Rows[e.RowIndex];
                var it = row.Tag as AdoptItem;
                if (it == null) return;
                if (_grid.Columns[e.ColumnIndex].Name == "Sel")
                    it.Checked = Convert.ToBoolean(row.Cells["Sel"].Value ?? false);
                else if (_grid.Columns[e.ColumnIndex].Name == "Supplier")
                    it.Supplier = Convert.ToString(row.Cells["Supplier"].Value) ?? "";
            };
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
        }

        void ApplyFilter()
        {
            string kw = _tbFilter.Text.Trim();
            IEnumerable<AdoptItem> visible = _items;
            if (kw.Length > 0)
                visible = _items.Where(x => x.Row.FilePath.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
            var list = visible.ToList();

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var it in list)
            {
                int i = _grid.Rows.Add(it.Checked, it.Row.FilePath, it.Supplier, UiUtil.FormatSize(it.Row.NewSize));
                var row = _grid.Rows[i];
                row.Tag = it;
                row.Cells["File"].ToolTipText = it.Row.FilePath;
            }
            _grid.ResumeLayout();
            _lblCount.Text = "显示 " + list.Count + " / 共 " + _items.Count + " 个";
        }

        void QuickApply()
        {
            string sup = (_cbQuickSup.Text ?? "").Trim();
            if (sup.Length == 0)
            {
                MessageBox.Show(this, "请先选择或输入供应商。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var it = row.Tag as AdoptItem;
                if (it == null) continue;
                it.Supplier = sup;
                row.Cells["Supplier"].Value = sup;
            }
        }

        void SetAll(bool v)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells["Sel"].Value = v;
                var it = row.Tag as AdoptItem;
                if (it != null) it.Checked = v;
            }
        }

        void Confirm()
        {
            string doctype = _cbDocType.SelectedItem as string ?? "";
            var chosen = _items.Where(x => x.Checked).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "没有勾选任何文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int blank = chosen.Count(x => x.Supplier.Length == 0);
            if (blank > 0 &&
                MessageBox.Show(this, string.Format(
                    "有 {0} 个勾选文件未指定供应商。\n\n" +
                    "可以先把它们登记进库（供应商留空），回主列表勾选「只看待补」再逐个补填；" +
                    "选“否”则返回现在逐行填。\n\n继续入库吗？", blank),
                    "先入库、后补信息", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            // 手打的新供应商不在字典里时问一句，避免档案里的名字和字典对不上
            var missing = chosen.Select(x => x.Supplier)
                .Where(s => s.Length > 0 && !DictRepo.SupplierExists(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                if (MessageBox.Show(this,
                    "以下供应商还不在字典里：\n" + string.Join("、", missing) +
                    "\n\n一并加入供应商字典吗？（选“否”则只入档案，字典不变）",
                    "供应商字典", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    foreach (var s in missing) DictRepo.SupplierAdd(s, "补入库时新增");
                }
            }

            try
            {
                int ok = 0, dup = 0;
                foreach (var it in chosen)
                {
                    if (AdoptCore.Import(it.Row, it.Supplier, doctype))
                    {
                        ok++;
                        _importedHashes.Add(it.Row.Hash);
                    }
                    else dup++;   // 内容与已有档案相同，跳过
                }
                if (dup > 0)
                    MessageBox.Show(this, string.Format(
                        "入库 {0} 个，另有 {1} 个因内容与已有档案相同被跳过（不改原有档案）。",
                        ok, dup), "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "入库失败", ex); }
        }
    }
}
