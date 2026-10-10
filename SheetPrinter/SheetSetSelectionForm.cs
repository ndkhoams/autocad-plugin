using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CADtools
{
    internal sealed class SheetSetChoice
    {
        public string Name;
        public string Path;
        public int SheetCount;

        public override string ToString()
        {
            return Name + " (" + SheetCount + " sheet) - " + System.IO.Path.GetFileName(Path);
        }
    }

    internal sealed class SheetSetSelectionForm : Form
    {
        private readonly ListBox _list;
        public string SelectedPath { get; private set; }

        public SheetSetSelectionForm(IEnumerable<SheetSetChoice> choices)
        {
            Text = "Chọn Sheet Set";
            ClientSize = new Size(680, 330);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 260);

            Controls.Add(new Label
            {
                Text = "Có nhiều Sheet Set đang mở. Chọn Sheet Set cần xử lý:",
                Left = 16, Top = 14, Width = 640, Height = 24
            });

            _list = new ListBox
            {
                Left = 16, Top = 45, Width = 648, Height = 220,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                DisplayMember = "ToString"
            };
            foreach (SheetSetChoice choice in choices) _list.Items.Add(choice);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.DoubleClick += (s, e) => AcceptSelection();
            Controls.Add(_list);

            var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Left = 574, Top = 278, Width = 90, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var ok = new Button { Text = "Chọn", Left = 478, Top = 278, Width = 90, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            ok.Click += (s, e) => AcceptSelection();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void AcceptSelection()
        {
            var choice = _list.SelectedItem as SheetSetChoice;
            if (choice == null) return;
            SelectedPath = choice.Path;
            DialogResult = DialogResult.OK;
        }
    }
}