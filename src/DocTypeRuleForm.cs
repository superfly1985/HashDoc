using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    class DocTypeRuleItem
    {
        public FileRecord Rec;
        public string Target = "";
        public bool Checked;
    }

    /// <summary>
    /// 按路径规则手动补齐文档类型（02 BMG→BMG / 03 ELV→ELV / 04 PPAP→PPAP）。
    /// 先预览命中清单、人工确认后才写库；空文档类型的默认勾选，已有类型需人工勾选才覆盖。
    /// </summary>
    public class DocTypeRuleForm : Form
    {
        readonly SmoothGrid _grid = new SmoothGrid();
        readonly Label _lblSummary = UiUtil.Lbl("");
        List<DocTypeRuleItem> _items = new List<DocTypeRuleItem>();

        public DocTypeRuleForm()
        {
            Text = "按路径规则补齐文档类型";
            Font = UiUtil.Font9;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 620);
            MinimumSize = new Size(820, 480);

            var lblTop = new Label
            {
                Text = "规则（路径中任一层文件夹名完全匹配，大小写不限，按顺序取先命中）：\n" +
                       "    02 BMG → BMG        03 ELV → ELV        04 PPAP → PPAP\n" +
                       "默认只勾选【文档类型为空】的档案；已有类型的档案需手动勾选才会覆盖。",
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = UiUtil.Font9,
                Padding = new Padding(12, 8, 12, 0),
                Margin = Padding.Empty,
            };

            // 底部：统计行 + 按钮行
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12, 2, 12, 8),
                Margin = Padding.Empty,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _lblSummary.AutoSize = false;
            _lblSummary.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _lblSummary.Height = 20;
            _lblSummary.AutoEllipsis = true;
            _lblSummary.Margin = new Padding(0, 2, 0, 4);

            var btnAll = UiUtil.Btn("全选", 80);
            btnAll.Click += delegate { SetAll(true); };
            var btnNone = UiUtil.Btn("全不选", 90);
            btnNone.Click += delegate { SetAll(false); };
            var btnOnlyEmpty = UiUtil.Btn("只选空类型", 110);
            btnOnlyEmpty.Click += delegate
            {
                SetAll(false);
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var it = row.Tag as DocTypeRuleItem;
                    if (it != null && string.IsNullOrEmpty(it.Rec.DocType)) row.Cells["Sel"].Value = true;
                }
            };
            var left = UiUtil.Row(btnAll, btnNone, btnOnlyEmpty);

            var ok = UiUtil.Btn("应用所选", 110);
            ok.Click += delegate { Apply(); };
            var cancel = UiUtil.Btn("关闭", 90);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;

            var btnRow = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.Controls.Add(left, 0, 0);
            btnRow.Controls.Add(UiUtil.RowRight(cancel, ok), 2, 0);

            bottom.Controls.Add(_lblSummary, 0, 0);
            bottom.Controls.Add(btnRow, 0, 1);

            BuildGrid();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _grid.Dock = DockStyle.Fill;
            _grid.Margin = Padding.Empty;
            root.Controls.Add(lblTop, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            Load += delegate { LoadItems(); };
        }

        void BuildGrid()
        {
            _grid.ReadOnly = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Sel", HeaderText = "应用", Width = 46 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Path", HeaderText = "文件路径", FillWeight = 100,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Cur", HeaderText = "当前类型", Width = 90,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Target", HeaderText = "目标类型", Width = 90,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Sel") return;
                var it = _grid.Rows[e.RowIndex].Tag as DocTypeRuleItem;
                if (it != null) it.Checked = Convert.ToBoolean(_grid.Rows[e.RowIndex].Cells["Sel"].Value ?? false);
            };
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
        }

        void LoadItems()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _items = FileRepo.LoadAll()
                    .Select(r => new DocTypeRuleItem { Rec = r, Target = PathRules.InferDocType(r.CurrentPath) ?? "" })
                    .Where(x => x.Target.Length > 0 && x.Target != x.Rec.DocType)
                    .ToList();
            }
            finally { Cursor = Cursors.Default; }

            foreach (var it in _items)
                it.Checked = string.IsNullOrEmpty(it.Rec.DocType);

            Refill();
            _lblSummary.Text = string.Format(
                "命中 {0} 条待更新（其中 {1} 条当前类型为空，已默认勾选）。", _items.Count, _items.Count(x => string.IsNullOrEmpty(x.Rec.DocType)));
        }

        void Refill()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var it in _items)
            {
                int i = _grid.Rows.Add(it.Checked, it.Rec.CurrentPath,
                    string.IsNullOrEmpty(it.Rec.DocType) ? "（空）" : it.Rec.DocType, it.Target);
                var row = _grid.Rows[i];
                row.Tag = it;
                row.Cells["Path"].ToolTipText = it.Rec.CurrentPath;
                if (string.IsNullOrEmpty(it.Rec.DocType))
                    row.Cells["Cur"].Style.ForeColor = Color.DimGray;
            }
            _grid.ResumeLayout();
        }

        void SetAll(bool v)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells["Sel"].Value = v;
                var it = row.Tag as DocTypeRuleItem;
                if (it != null) it.Checked = v;
            }
        }

        void Apply()
        {
            var chosen = _items.Where(x => x.Checked).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "没有勾选任何条目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "确定把所选 " + chosen.Count + " 条档案的文档类型按规则更新吗？\n（供应商等其他信息不变）",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var it in chosen)
                    FileRepo.UpdateAttrs(it.Rec.Hash, it.Rec.Supplier, it.Target, it.Rec.BmgNo);
                MessageBox.Show(this, "已更新 " + chosen.Count + " 条。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadItems();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "更新失败", ex); }
            finally { Cursor = Cursors.Default; }
        }
    }
}
