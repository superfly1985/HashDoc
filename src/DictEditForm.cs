using System;
using System.Drawing;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>字典项（供应商/文档类型）新增或修改的小对话框。返回 null 表示取消。</summary>
    public class DictEditForm : Form
    {
        readonly TextBox _tbName = UiUtil.Txt(220);
        readonly TextBox _tbRemark = UiUtil.Txt(220);

        DictEditForm(string title, string label, string name, string remark)
        {
            Text = title;
            Font = UiUtil.Font9;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 152);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(16, 14, 16, 10),
                Margin = Padding.Empty,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            var lbl = UiUtil.Lbl(label);
            lbl.Anchor = AnchorStyles.Left;
            lbl.Margin = new Padding(0, 5, 10, 0);
            _tbName.Dock = DockStyle.Fill;
            _tbName.Margin = new Padding(0, 2, 0, 4);
            root.Controls.Add(lbl, 0, 0);
            root.Controls.Add(_tbName, 1, 0);

            var lbl2 = UiUtil.Lbl("备注");
            lbl2.Anchor = AnchorStyles.Left;
            lbl2.Margin = new Padding(0, 5, 10, 0);
            _tbRemark.Dock = DockStyle.Fill;
            _tbRemark.Margin = new Padding(0, 2, 0, 4);
            root.Controls.Add(lbl2, 0, 1);
            root.Controls.Add(_tbRemark, 1, 1);

            var ok = UiUtil.Btn("确定", 90);
            ok.Click += delegate
            {
                if (_tbName.Text.Trim().Length == 0)
                {
                    MessageBox.Show(this, "名称不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            var cancel = UiUtil.Btn("取消", 90);
            cancel.DialogResult = DialogResult.Cancel;

            var btnRow = UiUtil.RowRight(cancel, ok);
            btnRow.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(btnRow, 0, 3);
            root.SetColumnSpan(btnRow, 2);

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(root);

            _tbName.Text = name ?? "";
            _tbRemark.Text = remark ?? "";
        }

        public static string[] Show(IWin32Window owner, string title, string label, string name = "", string remark = "")
        {
            using (var f = new DictEditForm(title, label, name, remark))
            {
                return f.ShowDialog(owner) == DialogResult.OK ? new[] { f._tbName.Text.Trim(), f._tbRemark.Text.Trim() } : null;
            }
        }
    }
}
