using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace HashDoc
{
    /// <summary>
    /// 一种关联编号的录入控件：随动检索输入 + 添加 + 已录入列表 + 删除。入库页与档案编辑窗复用。
    /// 候选来自库内该类型已有的编号值，打字即过滤，回车或点【添加】录入。
    /// </summary>
    public class CodeListEditor : UserControl
    {
        readonly ComboBox _cb;
        readonly ListBox _list;
        readonly Button _add, _del;

        /// <param name="title">分组标题</param>
        /// <param name="codeType">CodeTypes.*，用于从库里取历史值当候选；为空则不联想</param>
        public CodeListEditor(string title, string codeType = null)
        {
            Font = UiUtil.Font9;

            var lbl = UiUtil.Lbl(title, bold: true);
            lbl.Dock = DockStyle.Top;

            _cb = UiUtil.ComboEdit(200);
            _cb.Dock = DockStyle.Fill;
            if (!string.IsNullOrEmpty(codeType))
                UiUtil.AutoComplete(_cb, FileRepo.DistinctCodes(codeType));
            _cb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddCurrent(); }
            };

            _add = UiUtil.Btn("添加", 56);
            _add.Dock = DockStyle.Fill;
            _add.Click += delegate { AddCurrent(); };

            _del = UiUtil.Btn("删除", 56);
            // 按钮固定高度、贴顶：编辑器被拉高时不会变成一根竖长条
            _del.Dock = DockStyle.Top;
            _del.Height = 26;
            _del.Margin = new Padding(0, 4, 0, 0);
            _del.Click += delegate
            {
                if (_list.SelectedIndex >= 0) _list.Items.RemoveAt(_list.SelectedIndex);
            };

            _list = new ListBox { Font = UiUtil.Font9, IntegralHeight = false, HorizontalScrollbar = true };
            _list.Dock = DockStyle.Fill;
            _list.DoubleClick += delegate
            {
                // 双击条目取回输入框修改
                if (_list.SelectedIndex >= 0)
                {
                    _cb.Text = _list.SelectedItem as string;
                    _list.Items.RemoveAt(_list.SelectedIndex);
                    _cb.Focus();
                }
            };

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.Controls.Add(_cb, 0, 0);
            table.Controls.Add(_add, 1, 0);
            table.Controls.Add(_list, 0, 1);
            table.Controls.Add(_del, 1, 1);

            Controls.Add(table);
            Controls.Add(lbl);
            Height = 100;
        }

        void AddCurrent()
        {
            string v = (_cb.Text ?? "").Trim();
            if (v.Length == 0) return;
            if (!_list.Items.Contains(v)) _list.Items.Add(v);
            _cb.Text = "";
            _cb.Focus();
        }

        /// <summary>当前已录入的编号值。</summary>
        public List<string> Values
        {
            get { return _list.Items.Cast<string>().ToList(); }
        }

        public void SetValues(IEnumerable<string> values)
        {
            _list.Items.Clear();
            if (values == null) return;
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v) && !_list.Items.Contains(v))
                    _list.Items.Add(v);
        }
    }
}
