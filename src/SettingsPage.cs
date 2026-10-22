using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 设置页：归档根目录、备份、扫描模式、供应商/文档类型字典、
    /// 从文件清单导入供应商、按路径规则补齐文档类型——辅助功能全部集中在此页。
    /// 行布局用 TableLayoutPanel / FlowLayoutPanel，控件不再用绝对坐标摆位。
    /// </summary>
    public class SettingsPage : UserControl
    {
        readonly Action _onChanged;
        readonly SmoothGrid _gRoots = new SmoothGrid();
        readonly Label _lblDb = UiUtil.Lbl("");
        readonly SmoothGrid _gSup = new SmoothGrid();
        readonly SmoothGrid _gType = new SmoothGrid();
        readonly RadioButton _rbAuto = new RadioButton { Text = "自动静默更新路径", AutoSize = true };
        readonly RadioButton _rbConfirm = new RadioButton { Text = "列出差异，确认后更新", AutoSize = true, Checked = true };

        public SettingsPage(Action onChanged)
        {
            _onChanged = onChanged;
            Dock = DockStyle.Fill;
            BackColor = Color.White;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(12, 12, 12, 10),
                Margin = Padding.Empty,
                BackColor = Color.White,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196)); // 归档目录清单
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 数据库 + 备份
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 两个补齐工具
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 提示
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));  // 扫描模式
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 两列字典

            root.Controls.Add(BuildRootsGroup(), 0, 0);
            root.Controls.Add(BuildDbRow(), 0, 1);
            root.Controls.Add(BuildToolsRow(), 0, 2);

            var lblHint = UiUtil.Lbl("提示：数据库固定在 data 目录内，程序文件可整个覆盖升级、不影响档案数据；请勿放在 Program Files 下运行。");
            lblHint.AutoSize = false;
            lblHint.Dock = DockStyle.Fill;
            lblHint.Height = 20;
            lblHint.AutoEllipsis = true;
            lblHint.ForeColor = Color.DimGray;
            lblHint.Margin = new Padding(0, 2, 0, 0);
            root.Controls.Add(lblHint, 0, 3);

            root.Controls.Add(BuildScanModeGroup(), 0, 4);
            root.Controls.Add(BuildDictTable(), 0, 5);

            Controls.Add(root);
            Load += delegate { Reload(); };
        }

        // ================= 布局 =================

        /// <summary>归档目录清单：可以有多个不同位置，第一行为默认入库位置。</summary>
        Control BuildRootsGroup()
        {
            var gb = new GroupBox
            {
                Text = "归档目录（可添加多个位置；入库时用“归档到”选择）",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 6),
            };

            _gRoots.Dock = DockStyle.Fill;
            _gRoots.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Path", HeaderText = "目录", FillWeight = 100,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            var colCount = new DataGridViewTextBoxColumn
            {
                Name = "Count", HeaderText = "档案数", Width = 84,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            };
            colCount.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colCount.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _gRoots.Columns.Add(colCount);

            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, WrapContents = false };
            var btnAdd = UiUtil.Btn("添加目录…", 104);
            btnAdd.Margin = new Padding(0, 4, 8, 0);
            btnAdd.Click += delegate { RootAdd(); };
            var btnDel = UiUtil.Btn("移除所选", 90);
            btnDel.Margin = new Padding(0, 4, 8, 0);
            btnDel.Click += delegate { RootRemove(); };
            var btnDef = UiUtil.Btn("设为默认", 90);
            btnDef.Margin = new Padding(0, 4, 12, 0);
            btnDef.Click += delegate { RootSetDefault(); };
            var hint = UiUtil.Lbl("第一行为默认入库位置；移除只影响清单，不动磁盘文件。");
            hint.ForeColor = Color.DimGray;
            hint.Margin = new Padding(0, 8, 0, 0);
            row.Controls.AddRange(new Control[] { btnAdd, btnDel, btnDef, hint });

            gb.Controls.Add(_gRoots);
            gb.Controls.Add(row);
            return gb;
        }

        Control BuildDbRow()
        {
            var row = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 2, 0, 4),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var btnBackup = UiUtil.Btn("备份数据库…", 108);
            btnBackup.Anchor = AnchorStyles.Left;
            btnBackup.Margin = new Padding(0, 0, 12, 0);
            btnBackup.Click += delegate { Backup(); };
            row.Controls.Add(btnBackup, 0, 0);

            _lblDb.AutoSize = false;
            _lblDb.Dock = DockStyle.Fill;
            _lblDb.Height = 20;
            _lblDb.AutoEllipsis = true;
            _lblDb.TextAlign = ContentAlignment.MiddleLeft;
            _lblDb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            row.Controls.Add(_lblDb, 1, 0);

            return row;
        }

        Control BuildToolsRow()
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 0, 4),
            };
            var btnHarvest = UiUtil.Btn("从文件清单导入供应商…", 178);
            btnHarvest.Margin = new Padding(0, 0, 8, 0);
            btnHarvest.Click += delegate { ImportSuppliers(); };
            var btnRule = UiUtil.Btn("按路径规则补齐文档类型…", 188);
            btnRule.Margin = new Padding(0, 0, 10, 0);
            btnRule.Click += delegate { InferDocTypes(); };
            var hint = UiUtil.Lbl("两个工具都只做候选/预填，确认后才写入数据库；清单文件只在导入时用一次，程序不带它。");
            hint.ForeColor = Color.DimGray;
            hint.Anchor = AnchorStyles.Left;
            hint.Margin = new Padding(0, 6, 0, 0);
            row.Controls.AddRange(new Control[] { btnHarvest, btnRule, hint });
            return row;
        }

        Control BuildScanModeGroup()
        {
            var gbMode = new GroupBox
            {
                Text = "扫描模式（目录扫描时生效）",
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 6, 12, 6),
                Margin = new Padding(0, 4, 0, 4),
            };
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            _rbAuto.Margin = new Padding(0, 4, 24, 0);
            _rbConfirm.Margin = new Padding(0, 4, 0, 0);
            _rbAuto.CheckedChanged += delegate { if (_rbAuto.Checked) AppSettings.ScanMode("auto"); };
            _rbConfirm.CheckedChanged += delegate { if (_rbConfirm.Checked) AppSettings.ScanMode("confirm"); };
            row.Controls.Add(_rbAuto);
            row.Controls.Add(_rbConfirm);
            gbMode.Controls.Add(row);
            return gbMode;
        }

        Control BuildDictTable()
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            BuildGrid(_gSup);
            BuildGrid(_gType);

            var g1 = MakeDictGroup("供应商清单", _gSup, SupAdd, SupRename, SupDel);
            g1.Margin = new Padding(0, 0, 6, 0);
            var g2 = MakeDictGroup("文档类型清单（内置 BMG / PPAP / ELV，细分型号可自行添加）", _gType, TypeAdd, TypeRename, TypeDel);
            g2.Margin = new Padding(6, 0, 0, 0);

            table.Controls.Add(g1, 0, 0);
            table.Controls.Add(g2, 1, 0);
            return table;
        }

        public void Reload()
        {
            FillRoots();
            _lblDb.Text = "程序版本 v" + UiUtil.AppVersion() + "　｜　数据库文件：" + Db.DbPath;
            FillGrid(_gSup, DictRepo.Suppliers());
            FillGrid(_gType, DictRepo.DocTypes());
            if (AppSettings.ScanMode() == "auto") _rbAuto.Checked = true;
            else _rbConfirm.Checked = true;
        }

        void FillRoots()
        {
            _gRoots.Rows.Clear();
            foreach (var r in ArchiveRoots.All())
            {
                int i = _gRoots.Rows.Add(r.Path, FileRepo.CountUnder(r.Path));
                _gRoots.Rows[i].Cells["Path"].ToolTipText = r.Path;
            }
            if (_gRoots.Rows.Count > 0)
                _gRoots.Rows[0].DefaultCellStyle.Font = UiUtil.Font9B;
        }

        GroupBox MakeDictGroup(string title, DataGridView grid, Action add, Action rename, Action del)
        {
            var gb = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, WrapContents = false };
            var bAdd = UiUtil.Btn("新增", 76);
            bAdd.Margin = new Padding(0, 4, 8, 0);
            bAdd.Click += delegate { add(); };
            var bRen = UiUtil.Btn("修改", 76);
            bRen.Margin = new Padding(0, 4, 8, 0);
            bRen.Click += delegate { rename(); };
            var bDel = UiUtil.Btn("删除", 76);
            bDel.Margin = new Padding(0, 4, 0, 0);
            bDel.Click += delegate { del(); };
            row.Controls.AddRange(new Control[] { bAdd, bRen, bDel });
            grid.Dock = DockStyle.Fill;
            gb.Controls.Add(grid);
            gb.Controls.Add(row);
            return gb;
        }

        static void BuildGrid(DataGridView g)
        {
            g.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Name", HeaderText = "名称", FillWeight = 42,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            g.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Remark", HeaderText = "备注", FillWeight = 58,
                ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        static void FillGrid(DataGridView g, List<DictItem> items)
        {
            g.Rows.Clear();
            foreach (var it in items)
                g.Rows.Add(it.Name, it.Remark);
        }

        // ---- 归档目录 ----

        void RootAdd()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择归档目录（可以重复添加多个位置，分别管理不同盘/不同共享盘上的档案）";
                dlg.ShowNewFolderButton = true;
                string def = ArchiveRoots.Default();
                if (Directory.Exists(def)) dlg.SelectedPath = def;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string p = ArchiveRoots.Normalize(dlg.SelectedPath);
                if (p.Length == 0)
                {
                    MessageBox.Show(this, "目录路径无效。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (ArchiveRoots.Contains(p))
                {
                    MessageBox.Show(this, "该目录已在清单中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ArchiveRoots.Add(p);
                Reload();
                Changed();
            }
        }

        void RootRemove()
        {
            string p = SelectedRoot();
            if (p == null) { MsgSelect(); return; }
            int used = FileRepo.CountUnder(p);
            string msg = "从清单移除该归档目录？\n" + p +
                         "\n\n只移除配置，磁盘文件不动。" +
                         (used > 0 ? "\n其中 " + used + " 条档案会归到【其他位置】，仍可在列表中查到。" : "");
            if (MessageBox.Show(this, msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ArchiveRoots.Remove(p);
            Reload();
            Changed();
        }

        void RootSetDefault()
        {
            string p = SelectedRoot();
            if (p == null) { MsgSelect(); return; }
            ArchiveRoots.SetDefault(p);
            Reload();
            Changed();
        }

        string SelectedRoot()
        {
            if (_gRoots.CurrentRow == null) return null;
            string p = Convert.ToString(_gRoots.CurrentRow.Cells["Path"].Value);
            return string.IsNullOrEmpty(p) ? null : p;
        }

        void Backup()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "SQLite 数据库|*.db";
                dlg.FileName = "HashDoc_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var dest = new SQLiteConnection("Data Source=" + dlg.FileName + ";Version=3;"))
                    {
                        dest.Open();
                        Db.Conn.BackupDatabase(dest, "main", "main", -1, null, 0);
                    }
                    MessageBox.Show(this, "备份完成：" + dlg.FileName, "备份",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { UiUtil.ShowError(this, "备份失败", ex); }
            }
        }

        // ---- 目录/规则工具（只做候选与预填，确认后才写库） ----

        /// <summary>从文件清单（Excel 另存的 CSV）一次性播种供应商字典：只取「目录标记」紧邻的下一层。</summary>
        void ImportSuppliers()
        {
            using (var dlg = new SupplierSeedForm())
            {
                dlg.ShowDialog(this);
            }
            Reload();
            Changed();
        }

        void InferDocTypes()
        {
            using (var dlg = new DocTypeRuleForm())
            {
                dlg.ShowDialog(this);
            }
            Changed();
        }

        // ---- 供应商 ----

        void SupAdd()
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
                Reload(); Changed();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "新增失败", ex); }
        }

        void SupRename()
        {
            var item = Selected(_gSup);
            if (item == null) { MsgSelect(); return; }
            var r = DictEditForm.Show(this, "修改供应商", "供应商名称", item.Name, item.Remark);
            if (r == null || (r[0] == item.Name && r[1] == item.Remark)) return;
            try
            {
                if (r[0] != item.Name && DictRepo.SupplierExists(r[0]))
                {
                    MessageBox.Show(this, "新名称已存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int used = DictRepo.SupplierUsage(item.Name);
                string msg = "确定将供应商【" + item.Name + "】改名为【" + r[0] + "】吗？";
                if (used > 0)
                    msg += "\n\n将有 " + used + " 条档案记录同步更新为新供应商名。" +
                           "\n磁盘上旧的供应商文件夹不会自动改名；执行一次目录扫描即可让库内路径与磁盘保持一致。";
                if (MessageBox.Show(this, msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                DictRepo.SupplierRename(item.Name, r[0], r[1]);
                Reload(); Changed();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "修改失败", ex); }
        }

        void SupDel()
        {
            var item = Selected(_gSup);
            if (item == null) { MsgSelect(); return; }
            int used = DictRepo.SupplierUsage(item.Name);
            if (used > 0)
            {
                MessageBox.Show(this, "该供应商已被 " + used + " 条档案使用，无法删除。\n可先修改这些档案的供应商，或用【修改】改名词典项。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "确定删除供应商【" + item.Name + "】吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { DictRepo.SupplierDelete(item.Name); Reload(); Changed(); }
            catch (Exception ex) { UiUtil.ShowError(this, "删除失败", ex); }
        }

        // ---- 文档类型 ----

        void TypeAdd()
        {
            var r = DictEditForm.Show(this, "新增文档类型", "类型名称（如 PPAP-首页）");
            if (r == null) return;
            try
            {
                if (DictRepo.DocTypeExists(r[0]))
                {
                    MessageBox.Show(this, "该文档类型已存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DictRepo.DocTypeAdd(r[0], r[1]);
                Reload(); Changed();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "新增失败", ex); }
        }

        void TypeRename()
        {
            var item = Selected(_gType);
            if (item == null) { MsgSelect(); return; }
            var r = DictEditForm.Show(this, "修改文档类型", "类型名称", item.Name, item.Remark);
            if (r == null || (r[0] == item.Name && r[1] == item.Remark)) return;
            try
            {
                if (r[0] != item.Name && DictRepo.DocTypeExists(r[0]))
                {
                    MessageBox.Show(this, "新名称已存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int used = DictRepo.DocTypeUsage(item.Name);
                string msg = "确定将文档类型【" + item.Name + "】改名为【" + r[0] + "】吗？";
                if (used > 0) msg += "\n\n将有 " + used + " 条档案记录同步更新。";
                if (MessageBox.Show(this, msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                DictRepo.DocTypeRename(item.Name, r[0], r[1]);
                Reload(); Changed();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "修改失败", ex); }
        }

        void TypeDel()
        {
            var item = Selected(_gType);
            if (item == null) { MsgSelect(); return; }
            int used = DictRepo.DocTypeUsage(item.Name);
            if (used > 0)
            {
                MessageBox.Show(this, "该文档类型已被 " + used + " 条档案使用，无法删除。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "确定删除文档类型【" + item.Name + "】吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { DictRepo.DocTypeDelete(item.Name); Reload(); Changed(); }
            catch (Exception ex) { UiUtil.ShowError(this, "删除失败", ex); }
        }

        // ---- 公共 ----

        DictItem Selected(DataGridView g)
        {
            if (g.CurrentRow == null) return null;
            string name = Convert.ToString(g.CurrentRow.Cells["Name"].Value);
            if (string.IsNullOrEmpty(name)) return null;
            return new DictItem
            {
                Name = name,
                Remark = Convert.ToString(g.CurrentRow.Cells["Remark"].Value) ?? ""
            };
        }

        void MsgSelect()
        {
            MessageBox.Show(this, "请先在清单中选择一条记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void Changed()
        {
            if (_onChanged != null) _onChanged();
        }
    }
}
