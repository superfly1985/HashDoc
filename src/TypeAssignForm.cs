using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 按文件夹统一指定文档类型：选中目录树里的某个文件夹，
    /// 把它（及可选子文件夹）下所有档案的类型批量改成指定值。
    /// 先预览命中数量，确认后才写库。
    /// </summary>
    public class TypeAssignForm : Form
    {
        readonly string _dir;
        readonly ComboBox _cbType;
        readonly CheckBox _cbRecursive;
        readonly Label _lblDir, _lblStat;
        readonly Button _btnOk;
        bool _counting;

        /// <summary>确认后实际更新的档案数（0 = 用户取消或没有需要更新的）。</summary>
        public int Changed { get; private set; }

        public TypeAssignForm(string dir, string displayName)
        {
            _dir = dir;
            Font = UiUtil.Font9;
            Text = "统一指定文档类型";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 216);
            MinimumSize = new Size(500, 200);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(14, 12, 14, 10),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 目录
            _lblDir = new Label
            {
                Text = "文件夹：" + displayName + "　（" + dir + "）",
                AutoEllipsis = true,
                Font = UiUtil.Font9B,
                AutoSize = false,
                Height = 24,
                Dock = DockStyle.Fill,
            };
            UiUtil.SetTip(_lblDir, dir);
            root.Controls.Add(_lblDir, 0, 0);

            root.Controls.Add(new Label
            {
                Text = "把该文件夹下所有档案的文档类型统一改成同一个值（适合一整个供应商目录都是同一种文件的场景）。",
                ForeColor = Color.DimGray,
                AutoSize = false,
                Height = 22,
                Dock = DockStyle.Fill,
            }, 0, 1);

            // 类型 + 范围
            _cbType = UiUtil.Combo(180);
            foreach (var t in DictRepo.DocTypes()) _cbType.Items.Add(t.Name);
            if (_cbType.Items.Count > 0) _cbType.SelectedIndex = 0;
            _cbRecursive = new CheckBox
            {
                Text = "包含子文件夹",
                Checked = true,
                AutoSize = true,
                Font = UiUtil.Font9,
            };
            _cbRecursive.CheckedChanged += delegate { RefreshCount(); };

            var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true };
            row1.Controls.Add(UiUtil.Lbl("文档类型"));
            _cbType.Margin = new Padding(6, 3, 14, 0);
            row1.Controls.Add(_cbType);
            row1.Controls.Add(_cbRecursive);
            root.Controls.Add(row1, 0, 2);

            // 预览
            _lblStat = new Label
            {
                Text = " ",
                AutoSize = false,
                Height = 40,
                Dock = DockStyle.Fill,
            };
            root.Controls.Add(_lblStat, 0, 3);

            // 按钮
            _btnOk = UiUtil.Btn("确定", 84);
            _btnOk.Click += delegate { Confirm(); };
            var btnCancel = UiUtil.Btn("取消", 84);
            btnCancel.DialogResult = DialogResult.Cancel;

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
            };
            btns.Controls.Add(_btnOk);
            btnCancel.Margin = new Padding(8, 3, 0, 0);
            btns.Controls.Add(btnCancel);
            root.Controls.Add(btns, 0, 4);

            Controls.Add(root);
            AcceptButton = _btnOk;
            CancelButton = btnCancel;

            _cbType.SelectedIndexChanged += delegate { RefreshCount(); };
            Load += delegate { RefreshCount(); };
        }

        bool Recursive { get { return _cbRecursive.Checked; } }
        string Type { get { return _cbType.Text == null ? "" : _cbType.Text.Trim(); } }

        void RefreshCount()
        {
            if (_counting) return;
            _counting = true;
            try
            {
                int total, changeable, already;
                FileRepo.CountUnderForType(_dir, Type, Recursive, out total, out changeable, out already);
                if (total == 0)
                {
                    _lblStat.Text = "该文件夹下没有档案。";
                    _btnOk.Enabled = false;
                }
                else if (changeable == 0)
                {
                    _lblStat.Text = string.Format(
                        "该文件夹{0}共 {1} 个档案，类型全部已是「{2}」，无需更新。",
                        Recursive ? "及子文件夹" : "", total, Type);
                    _btnOk.Enabled = false;
                }
                else
                {
                    _lblStat.Text = string.Format(
                        "该文件夹{0}共 {1} 个档案：将更新 {2} 个为「{3}」（{4} 个已是该类型，保持不动）。",
                        Recursive ? "及子文件夹" : "", total, changeable, Type, already);
                    _btnOk.Enabled = true;
                }
            }
            finally { _counting = false; }
        }

        void Confirm()
        {
            string type = Type;
            if (type.Length == 0) { UiUtil.ShowError(this, "请选择文档类型", new ArgumentException("类型为空")); return; }

            int n;
            try { n = FileRepo.UpdateDocTypeUnder(_dir, type, Recursive); }
            catch (Exception ex) { UiUtil.ShowError(this, "更新失败", ex); return; }

            Changed = Math.Max(0, n);
            MessageBox.Show(this,
                "已把 " + Changed + " 个档案的文档类型更新为「" + type + "」。",
                "统一指定类型", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
    }
}
