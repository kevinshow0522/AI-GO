using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace AISwitchDesk
{
	internal sealed class UsagePopup : ToolStripDropDown
	{
		private readonly Panel card;

		private readonly ToolStripControlHost host;

		private readonly Button refreshButton;

		private readonly Font titleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);

		private readonly Font bodyFont = new Font("Microsoft YaHei UI", 9.5f);

		private readonly Font smallFont = new Font("Microsoft YaHei UI", 8.5f);

		private static Color Paper { get { return DeskTheme.Current.Surface; } }
		private static Color Ink { get { return DeskTheme.Current.Ink; } }
		private static Color Muted { get { return DeskTheme.Current.Muted; } }

		private readonly List<Label> labels = new List<Label>();

		private PlanReport lastReport;

		private string lastTitle;

		private string lastError;

		private string lastAge;

		private bool lastReading;

		private int lastDpi;

		private double? lastAmount;

		private int width;

		private int side;

		private int y;

		private float scale;

		internal UsagePopup(Action refresh)
		{
			AutoSize = false;
			base.Padding = (base.Margin = Padding.Empty);
			base.BackColor = Paper;
			base.DropShadowEnabled = true;
			base.Name = "UsagePopup";
			base.AccessibleName = "套餐用量";
			card = new Panel
			{
				BackColor = Paper,
				Padding = Padding.Empty,
				Margin = Padding.Empty,
				TabStop = false
			};
			host = new ToolStripControlHost(card)
			{
				AutoSize = false,
				Padding = Padding.Empty,
				Margin = Padding.Empty
			};
			Items.Add(host);
			refreshButton = new Button
			{
				Text = "刷新额度",
				AccessibleName = "刷新额度",
				FlatStyle = FlatStyle.Flat,
				BackColor = DeskTheme.Current.Control,
				ForeColor = Ink,
				Font = smallFont,
				Cursor = Cursors.Hand,
				TabIndex = 0
			};
			refreshButton.FlatAppearance.BorderSize = 0;
			Button button = refreshButton;
			EventHandler value = delegate
			{
				refresh();
			};
			button.Click += value;
			card.Controls.Add(refreshButton);
			card.Paint += delegate(object sender, PaintEventArgs e)
			{
				if (card.Width < 2 || card.Height < 2) return;
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (var path = RailButton.Rounded(new RectangleF(.5f, .5f, card.Width - 1f, card.Height - 1f), Math.Max(1, Px(15))))
				using (var edge = new Pen(DeskTheme.Current.Rule)) e.Graphics.DrawPath(edge, path);
			};
			ApplyTheme();
		}

		internal void ApplyTheme()
		{
			var theme = DeskTheme.Current;
			BackColor = card.BackColor = theme.Surface;
			refreshButton.BackColor = theme.Control;
			refreshButton.ForeColor = theme.Ink;
			refreshButton.FlatAppearance.MouseOverBackColor = theme.Hover;
			refreshButton.FlatAppearance.MouseDownBackColor = theme.Selected;
			lastDpi = 0;
		}

		private int Px(int value)
		{
			return (int)Math.Round((float)value * scale);
		}

		private void Line(string text, Font font, Color ink, int top, int bottom)
		{
			int num = width - side * 2;
			y += Px(top);
			int num2 = TextRenderer.MeasureText(text, font, new Size(num, 1000), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height + Px(2);
			Label label = new Label();
			label.Text = text;
			label.AutoSize = false;
			label.UseMnemonic = false;
			label.Font = font;
			label.ForeColor = ink;
			label.BackColor = Paper;
			label.TextAlign = ContentAlignment.MiddleLeft;
			label.Bounds = new Rectangle(side, y, num, num2);
			Label label2 = label;
			card.Controls.Add(label2);
			labels.Add(label2);
			y += num2 + Px(bottom);
		}

		internal void Display(string title, UsageSlot slot, int dpi)
		{
			PlanReport report = slot.Report;
			Allowance allowance = slot.RingRow();
			double? num = ((allowance == null) ? null : new double?(allowance.Remaining));
			string age = slot.Stale ? slot.AgeText() : "";
			if (lastReport == report && lastError == slot.Error && lastReading == slot.Reading && lastTitle == title && lastDpi == dpi && lastAmount == num && lastAge == age)
			{
				return;
			}
			lastAge = age;
			lastReport = report;
			lastTitle = title;
			lastDpi = dpi;
			lastAmount = num;
			lastError = slot.Error;
			lastReading = slot.Reading;
			scale = (float)dpi / 96f;
			width = Px(266);
			side = Px(15);
			y = Px(11);
			SuspendLayout();
			card.SuspendLayout();
			try
			{
				foreach (Label label in labels)
				{
					label.Dispose();
				}
				labels.Clear();
				Line(title + " · 套餐用量", titleFont, Ink, 0, 6);
				if (report.Rows != null && report.Rows.Count > 0)
				{
					foreach (Allowance item in report.Rows.Where((Allowance x) => x != null))
					{
						string text = slot.Amount(item, CultureInfo.CurrentCulture);
						Line(item.Window + "    " + text, bodyFont, Ink, 3, 0);
						Line(slot.ResetLine(item), smallFont, Muted, 0, 6);
					}
				}
				else
				{
					Line(report.Note ?? "暂无官方额度数据", bodyFont, Muted, 2, 7);
				}
				if (!string.IsNullOrWhiteSpace(slot.Error))
				{
					Line(slot.Error, smallFont, Muted, 2, 5);
				}
				if (report.ReadAt > 0 && report.ReadAt <= DateTime.MaxValue.Ticks)
				{
					Line("上次读取  " + new DateTime(report.ReadAt, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm"), smallFont, Muted, 1, 7);
				}
				refreshButton.Text = (slot.Reading ? "正在读取…" : "刷新额度");
				refreshButton.Enabled = !slot.Reading;
				refreshButton.Bounds = new Rectangle(side, y, Px(85), Px(27));
				using (GraphicsPath path = RailButton.Rounded(new RectangleF(0f, 0f, refreshButton.Width, refreshButton.Height), Px(13)))
				{
					Region region = refreshButton.Region;
					refreshButton.Region = new Region(path);
					if (region != null)
					{
						region.Dispose();
					}
				}
				Size size = new Size(width, y + refreshButton.Height + Px(12));
				ToolStripControlHost toolStripControlHost = host;
				Panel panel = card;
				Size size3 = (base.Size = size);
				Size size5 = (panel.Size = size3);
				toolStripControlHost.Size = size5;
				using (GraphicsPath path2 = RailButton.Rounded(new RectangleF(0f, 0f, size.Width, size.Height), Px(15)))
				{
					Region region2 = base.Region;
					base.Region = new Region(path2);
					if (region2 != null)
					{
						region2.Dispose();
					}
				}
			}
			finally
			{
				card.ResumeLayout(false);
				ResumeLayout(false);
			}
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (disposing)
			{
				titleFont.Dispose();
				bodyFont.Dispose();
				smallFont.Dispose();
			}
		}
	}
}
