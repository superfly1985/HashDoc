using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>档案编辑：修改供应商、文档类型、增删各类关联编号。只改数据库元数据，不动磁盘文件。</summary>
    public class EditArchiveForm : Form
    {
        readonly FileRecord _rec;
        readonly ComboBox _cbSupplier = UiUtil.Combo(200);
        readonly ComboBox _cbDocType = UiUtil.Combo(140);
        readonly ComboBox _cbBmg = UiUtil.ComboEdit(200);
        readonly CodeListEditor _edPart = new CodeListEditor("供应商零件号（可多条）", CodeTypes.PartNo);
        readonly CodeListEditor _edVw = new CodeListEditor("VW客户号（可多条）", CodeTypes.VwNo);
        readonly CodeListEditor _edIdent = new CodeListEditor("ident内部件号（可多条）", CodeTypes.IdentNo);

        public EditArchiveForm(FileRecord rec)
        {
            _rec = rec;
            Text = "编辑档案 — " + rec.FileName;
            Font = UiUtil.Font9;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 616);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                Padding = new Padding(16, 14, 16, 10),
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 路径
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 哈希
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 供应商 / 文档类型
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // BMG 号
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));  // 零件号
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));  // VW号
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));  // ident号
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // 按钮

            var lblPath = UiUtil.LblFill("路径：" + rec.CurrentPath);
            lblPath.Margin = new Padding(0, 0, 0, 2);
            var lblHash = UiUtil.LblFill(
                "SHA-256：" + HashService.Short(rec.Hash) + "（文件唯一身份；双击列表行可打开文件）",
                Color.DimGray);

            var attrs = UiUtil.Row(
                UiUtil.Lbl("供应商"),
                _cbSupplier,
                UiUtil.Lbl("文档类型"),
                _cbDocType);
            attrs.Margin = new Padding(0, 4, 0, 6);

            var bmgRow = UiUtil.Row(UiUtil.Lbl("BMG 号"), _cbBmg,
                UiUtil.LblFill("（可空）", Color.DimGray));
            bmgRow.Margin = new Padding(0, 0, 0, 6);

            _edPart.Dock = DockStyle.Fill;
            _edPart.Margin = new Padding(0, 0, 0, 6);
            _edVw.Dock = DockStyle.Fill;
            _edVw.Margin = new Padding(0, 0, 0, 6);
            _edIdent.Dock = DockStyle.Fill;
            _edIdent.Margin = new Padding(0, 0, 0, 0);

            var ok = UiUtil.Btn("保存", 100);
            ok.Click += Save_Click;
            var cancel = UiUtil.Btn("取消", 100);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            var btnRow = UiUtil.RowRight(cancel, ok);
            btnRow.Margin = new Padding(0, 6, 0, 0);

            root.Controls.Add(lblPath, 0, 0);
            root.Controls.Add(lblHash, 0, 1);
            root.Controls.Add(attrs, 0, 2);
            root.Controls.Add(bmgRow, 0, 3);
            root.Controls.Add(_edPart, 0, 4);
            root.Controls.Add(_edVw, 0, 5);
            root.Controls.Add(_edIdent, 0, 6);
            root.Controls.Add(btnRow, 0, 7);

            Controls.Add(root);

            foreach (var b in FileRepo.DistinctBmgNos()) _cbBmg.Items.Add(b);
            UiUtil.AutoComplete(_cbBmg, _cbBmg.Items.Cast<string>());
            _cbBmg.Text = rec.BmgNo ?? "";

            foreach (var s in DictRepo.Suppliers()) _cbSupplier.Items.Add(s.Name);
            UiUtil.AutoComplete(_cbSupplier, _cbSupplier.Items.Cast<string>());
            foreach (var t in DictRepo.DocTypes()) _cbDocType.Items.Add(t.Name);
            UiUtil.AutoComplete(_cbDocType, _cbDocType.Items.Cast<string>());
            if (rec.Supplier != "" && _cbSupplier.Items.Contains(rec.Supplier)) _cbSupplier.SelectedItem = rec.Supplier;
            if (rec.DocType != "" && _cbDocType.Items.Contains(rec.DocType)) _cbDocType.SelectedItem = rec.DocType;

            _edPart.SetValues(rec.Codes.Where(c => c.Type == CodeTypes.PartNo).Select(c => c.Value));
            _edVw.SetValues(rec.Codes.Where(c => c.Type == CodeTypes.VwNo).Select(c => c.Value));
            _edIdent.SetValues(rec.Codes.Where(c => c.Type == CodeTypes.IdentNo).Select(c => c.Value));
        }

        void Save_Click(object sender, EventArgs e)
        {
            string supplier = _cbSupplier.SelectedItem as string ?? "";
            string doctype = _cbDocType.SelectedItem as string ?? "";
            string bmgNo = (_cbBmg.Text ?? "").Trim();
            if (supplier.Length == 0 &&
                MessageBox.Show(this, "未选择供应商，确定以空供应商保存吗？",
                    "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                var codes = new List<CodeEntry>();
                codes.AddRange(_edPart.Values.Select(v => new CodeEntry(CodeTypes.PartNo, v)));
                codes.AddRange(_edVw.Values.Select(v => new CodeEntry(CodeTypes.VwNo, v)));
                codes.AddRange(_edIdent.Values.Select(v => new CodeEntry(CodeTypes.IdentNo, v)));
                FileRepo.UpdateAttrs(_rec.Hash, supplier, doctype, bmgNo);
                FileRepo.ReplaceCodes(_rec.Hash, codes);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { UiUtil.ShowError(this, "保存失败", ex); }
        }
    }
}
