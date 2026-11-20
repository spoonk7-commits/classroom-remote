// 교실 리모컨 — 단추 고치기 창(끌어다 놓기 편집).
// 단추.json 을 읽어 고치고 저장한다. 저장하면 Remote.cs 의 감시가 폰에 새 단추를 보낸다.
// 모르는 칸은 건드리지 않고 그대로 둔다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamRemote
{
    class DeckEditor : Form
    {
        static DeckEditor inst;

        // 트레이 메뉴 「단추 고치기」(UI 스레드)
        public static void Open(Store store)
        {
            if (inst != null && !inst.IsDisposed)
            {
                if (inst.WindowState == FormWindowState.Minimized) inst.WindowState = FormWindowState.Normal;
                inst.Show(); inst.BringToFront(); inst.Activate();
                return;
            }
            Dictionary<string, object> deck = null;
            try { deck = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(store.DeckPath, Encoding.UTF8)) as Dictionary<string, object>; }
            catch { }
            if (deck == null)
            {
                MessageBox.Show("단추.json 을 읽지 못했습니다.", "단추 고치기");
                return;
            }
            inst = new DeckEditor(store, deck);
            inst.Show();
            inst.Activate();
        }

        const string TileFmt = "StreamRemoteTile";
        const string Gray = "#7d8597", AppColor = "#ff7a45";

        class Target
        {
            public bool App; public int Index; public string Text;
            public override string ToString() { return Text; }
        }

        readonly Store store;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Dictionary<string, object> deck;
        readonly List<object> appsView;    // 단추.json 에 apps 가 없으면 기본 목록 사본(고칠 때 붙인다)
        bool dirty, loading;
        readonly float sc = 1f;
        Target cur;
        Dictionary<string, object> selKey;
        int selIndex = -1;

        ComboBox targetBox;
        Button bAddPage, bRename, bDelPage, bAddApp;
        FlowLayoutPanel grid;
        Panel right, stepPanel;
        TextBox nameBox, iconBox, stepText, keyBox;
        ListBox stepList;
        Button bAddStep, bUp, bDown, bDelStep, bTest, bPick, bSave, bClose;
        Label stepLabel, secLabel, note, status;
        NumericUpDown secBox, xBox, yBox;
        CheckBox winBox;
        ComboBox relBox;
        ContextMenuStrip addStepMenu;
        readonly Timer countTimer = new Timer();
        int countLeft; string countMsg; Action countDone;
        Point downPt; bool downOnTile;

        int S(int v) { return (int)Math.Round(v * sc); }

        DeckEditor(Store store, Dictionary<string, object> deck)
        {
            this.store = store;
            this.deck = deck;
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);

            Lst(deck, "pages");
            object a;
            if (deck.TryGetValue("apps", out a) && a is IEnumerable && !(a is string)) appsView = Lst(deck, "apps");
            else
            {
                appsView = new List<object>();
                var copy = json.DeserializeObject(json.Serialize(store.Apps())) as object[];
                if (copy != null) appsView.AddRange(copy);
            }

            Text = "단추 고치기";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("맑은 고딕", 9f);
            ClientSize = new Size(S(980), S(660));
            MinimumSize = new Size(S(760), S(560));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            KeyPreview = false;

            Build();
            countTimer.Interval = 1000;
            countTimer.Tick += delegate { CountTick(); };
            FillTargets(false, 0);
        }

        // ---------- 화면 짜기 ----------
        void Build()
        {
            // 가운데(먼저 넣어야 Fill 이 남은 자리를 차지한다)
            grid = new FlowLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.AutoScroll = true;
            grid.AllowDrop = true;
            grid.BackColor = Color.FromArgb(30, 34, 42);
            grid.Padding = new Padding(S(8));
            grid.DragEnter += GridDragOver;
            grid.DragOver += GridDragOver;
            grid.DragDrop += GridDrop;
            grid.MouseDown += delegate { SelectKey(-1); };
            Controls.Add(grid);

            // 오른쪽
            right = new Panel();
            right.Dock = DockStyle.Right;
            right.Width = S(340);
            right.AutoScroll = true;
            Controls.Add(right);
            BuildRight();

            // 위
            var top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = S(44);
            top.Padding = new Padding(S(6), S(8), S(6), 0);
            top.WrapContents = false;
            targetBox = new ComboBox();
            targetBox.DropDownStyle = ComboBoxStyle.DropDownList;
            targetBox.Width = S(240);
            targetBox.Margin = new Padding(S(2), S(1), S(10), 0);
            targetBox.SelectedIndexChanged += delegate { if (!loading) ShowTarget(); };
            top.Controls.Add(targetBox);
            bAddPage = Btn("쪽 더하기", delegate { AddPage(); }); top.Controls.Add(bAddPage);
            bRename = Btn("이름 바꾸기", delegate { RenameTarget(); }); top.Controls.Add(bRename);
            bDelPage = Btn("쪽 지우기", delegate { DeleteTarget(); }); top.Controls.Add(bDelPage);
            bAddApp = Btn("앱 단추 더하기", delegate { AddAppRule(); });
            bAddApp.Margin = new Padding(S(16), 0, S(4), 0);
            top.Controls.Add(bAddApp);
            Controls.Add(top);

            // 아래
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = S(48);
            status = new Label();
            status.AutoSize = false;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Location = new Point(S(12), S(8));
            status.Size = new Size(S(560), S(30));
            status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.Add(status);
            var btns = new FlowLayoutPanel();
            btns.FlowDirection = FlowDirection.RightToLeft;
            btns.Dock = DockStyle.Right;
            btns.Width = S(260);
            btns.Padding = new Padding(0, S(8), S(8), 0);
            bClose = Btn("닫기", delegate { Close(); }); btns.Controls.Add(bClose);
            bSave = Btn("저장", delegate { Save(); });
            bSave.Font = new Font(Font, FontStyle.Bold);
            btns.Controls.Add(bSave);
            bottom.Controls.Add(btns);
            Controls.Add(bottom);
            AcceptButton = null;
        }

        Button Btn(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(S(64), S(28));
            b.Padding = new Padding(S(4), 0, S(4), 0);
            b.Margin = new Padding(S(2), 0, S(4), 0);
            b.UseVisualStyleBackColor = true;
            b.Click += click;
            return b;
        }

        Label Lbl(Control parent, string text, int x, int y)
        {
            var l = new Label();
            l.Text = text; l.AutoSize = true; l.Location = new Point(S(x), S(y));
            parent.Controls.Add(l);
            return l;
        }

        void BuildRight()
        {
            int x0 = 12, w = 306;
            Lbl(right, "이름", x0, 14);
            nameBox = new TextBox();
            nameBox.Location = new Point(S(70), S(10)); nameBox.Width = S(w - 58);
            nameBox.TextChanged += delegate
            {
                if (loading || selKey == null) return;
                selKey["label"] = nameBox.Text; MarkDirty(); RefreshTile();
            };
            right.Controls.Add(nameBox);

            Lbl(right, "아이콘", x0, 48);
            iconBox = new TextBox();
            iconBox.Location = new Point(S(70), S(42)); iconBox.Width = S(90);
            iconBox.Font = new Font("Segoe UI Emoji", 12f);
            iconBox.TextChanged += delegate
            {
                if (loading || selKey == null) return;
                selKey["icon"] = iconBox.Text.Trim(); MarkDirty(); RefreshTile();
            };
            right.Controls.Add(iconBox);

            Lbl(right, "동작 순서", x0, 84);
            stepList = new ListBox();
            stepList.Location = new Point(S(x0), S(104));
            stepList.Size = new Size(S(w), S(150));
            stepList.IntegralHeight = false;
            stepList.HorizontalScrollbar = true;
            stepList.SelectedIndexChanged += delegate { if (!loading) ShowStep(); };
            right.Controls.Add(stepList);

            int by = 260;
            bAddStep = Btn("＋ 동작", delegate { addStepMenu.Show(bAddStep, new Point(0, bAddStep.Height)); });
            bAddStep.Location = new Point(S(x0), S(by)); right.Controls.Add(bAddStep);
            bUp = Btn("위로", delegate { MoveStep(-1); }); bUp.Location = new Point(S(x0 + 84), S(by)); right.Controls.Add(bUp);
            bDown = Btn("아래로", delegate { MoveStep(1); }); bDown.Location = new Point(S(x0 + 154), S(by)); right.Controls.Add(bDown);
            bDelStep = Btn("지우기", delegate { DeleteStep(); }); bDelStep.Location = new Point(S(x0 + 230), S(by)); right.Controls.Add(bDelStep);

            addStepMenu = new ContextMenuStrip();
            foreach (var kind in new[] { "open", "wait", "waitwin", "focus", "key", "type", "click" })
            {
                string k = kind;
                addStepMenu.Items.Add(KindName(k), null, delegate { AddStep(k); });
            }

            // 고르는 동작의 편집 칸
            stepPanel = new Panel();
            stepPanel.Location = new Point(S(x0), S(300));
            stepPanel.Size = new Size(S(w), S(176));
            right.Controls.Add(stepPanel);

            stepLabel = Lbl(stepPanel, "", 0, 2);
            stepText = new TextBox();
            stepText.Location = new Point(0, S(22)); stepText.Width = S(w);
            stepText.TextChanged += delegate { StepTextChanged(); };
            stepPanel.Controls.Add(stepText);

            secLabel = Lbl(stepPanel, "초", 0, 60);
            secBox = new NumericUpDown();
            secBox.DecimalPlaces = 1; secBox.Increment = 0.5m; secBox.Minimum = 0; secBox.Maximum = 30;
            secBox.Location = new Point(S(40), S(56)); secBox.Width = S(80);
            secBox.ValueChanged += delegate { SecChanged(); };
            stepPanel.Controls.Add(secBox);

            keyBox = new TextBox();
            keyBox.ReadOnly = true;
            keyBox.BackColor = SystemColors.Window;
            keyBox.Location = new Point(0, S(22)); keyBox.Width = S(200);
            keyBox.PreviewKeyDown += delegate (object s, PreviewKeyDownEventArgs e) { e.IsInputKey = true; };
            keyBox.KeyDown += KeyCapture;
            stepPanel.Controls.Add(keyBox);
            winBox = new CheckBox();
            winBox.Text = "win 함께"; winBox.AutoSize = true;
            winBox.Location = new Point(S(210), S(24));
            winBox.CheckedChanged += delegate { WinChanged(); };
            stepPanel.Controls.Add(winBox);

            xBox = Num(0, 22); yBox = Num(90, 22);
            relBox = new ComboBox();
            relBox.DropDownStyle = ComboBoxStyle.DropDownList;
            relBox.Items.Add("창 기준"); relBox.Items.Add("화면 기준");
            relBox.Location = new Point(S(180), S(22)); relBox.Width = S(110);
            relBox.SelectedIndexChanged += delegate { ClickChanged(); };
            stepPanel.Controls.Add(relBox);
            bPick = Btn("위치 잡기", delegate { PickPoint(); });
            bPick.Location = new Point(0, S(58));
            stepPanel.Controls.Add(bPick);

            note = Lbl(stepPanel, "비밀번호는 넣지 마세요", 0, 120);
            note.ForeColor = Color.FromArgb(180, 70, 40);

            bTest = Btn("시험 실행", delegate { TestRun(); });
            bTest.Location = new Point(S(x0), S(486));
            right.Controls.Add(bTest);

            ShowStep();
        }

        NumericUpDown Num(int x, int y)
        {
            var n = new NumericUpDown();
            n.Minimum = -10000; n.Maximum = 10000;
            n.Location = new Point(S(x), S(y)); n.Width = S(80);
            n.ValueChanged += delegate { ClickChanged(); };
            stepPanel.Controls.Add(n);
            return n;
        }

        // ---------- 편집 대상(쪽·앱) ----------
        List<object> Pages() { return Lst(deck, "pages"); }

        Dictionary<string, object> CurDict()
        {
            if (cur == null) return null;
            var list = cur.App ? appsView : Pages();
            if (cur.Index < 0 || cur.Index >= list.Count) return null;
            return list[cur.Index] as Dictionary<string, object>;
        }

        List<object> CurKeys()
        {
            var d = CurDict(); return d == null ? null : Lst(d, "keys");
        }

        void FillTargets(bool app, int idx)
        {
            loading = true;
            targetBox.Items.Clear();
            int sel = -1;
            var pages = Pages();
            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i] as Dictionary<string, object>;
                targetBox.Items.Add(new Target { App = false, Index = i, Text = "쪽: " + (p == null ? "" : Str(p, "name") ?? "") });
                if (!app && i == idx) sel = targetBox.Items.Count - 1;
            }
            for (int i = 0; i < appsView.Count; i++)
            {
                var r = appsView[i] as Dictionary<string, object>;
                targetBox.Items.Add(new Target { App = true, Index = i, Text = "앱: " + (r == null ? "" : Str(r, "name") ?? "") });
                if (app && i == idx) sel = targetBox.Items.Count - 1;
            }
            if (sel < 0 && targetBox.Items.Count > 0) sel = 0;
            targetBox.SelectedIndex = sel;
            loading = false;
            ShowTarget();
        }

        void ShowTarget()
        {
            cur = targetBox.SelectedItem as Target;
            bDelPage.Text = cur != null && cur.App ? "앱 지우기" : "쪽 지우기";
            selKey = null; selIndex = -1;
            RebuildTiles();
            ShowKey();
        }

        void AddPage()
        {
            string n = Ask("쪽 더하기", "");
            if (string.IsNullOrEmpty(n)) return;
            var p = new Dictionary<string, object>();
            p["name"] = n; p["keys"] = new List<object>();
            Pages().Add(p);
            MarkDirty();
            FillTargets(false, Pages().Count - 1);
        }

        void RenameTarget()
        {
            var d = CurDict(); if (d == null) return;
            string n = Ask("이름 바꾸기", Str(d, "name") ?? "");
            if (string.IsNullOrEmpty(n)) return;
            d["name"] = n;
            MarkDirty();
            FillTargets(cur.App, cur.Index);
        }

        void DeleteTarget()
        {
            var d = CurDict(); if (d == null) return;
            string nm = Str(d, "name") ?? "";
            if (!cur.App && Pages().Count <= 1) { MessageBox.Show(this, "쪽이 하나는 있어야 합니다.", Text); return; }
            string q = cur.App ? "「" + nm + "」 앱 단추를 지울까요?" : "「" + nm + "」 쪽을 지울까요?";
            if (MessageBox.Show(this, q, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool app = cur.App;
            MarkDirty();   // 앱이면 기본 목록 사본을 먼저 붙인다
            if (app) appsView.RemoveAt(cur.Index); else Pages().RemoveAt(cur.Index);
            FillTargets(app, Math.Max(0, cur.Index - 1));
        }

        void AddAppRule()
        {
            Countdown("3초 안에 단추를 만들 앱을 앞에 띄우세요", delegate
            {
                string exe, title;
                if (!Fore.Get(out exe, out title) || string.IsNullOrEmpty(exe))
                {
                    Activate();
                    MessageBox.Show(this, "앞에 띄운 앱을 찾지 못했습니다.", Text);
                    return;
                }
                string nm = null;
                try
                {
                    var ps = Process.GetProcessesByName(exe);
                    if (ps.Length > 0) nm = ps[0].MainModule.FileVersionInfo.FileDescription;
                }
                catch { }
                if (string.IsNullOrEmpty(nm)) nm = string.IsNullOrEmpty(title) ? exe : title;
                nm = nm.Trim();
                if (nm.Length > 30) nm = nm.Substring(0, 30);
                var r = new Dictionary<string, object>();
                r["name"] = nm;
                r["exe"] = new List<object> { exe };
                r["keys"] = new List<object>();
                appsView.Add(r);
                AttachApps();
                MarkDirty();
                FillTargets(true, appsView.Count - 1);
                Activate();
            });
        }

        void AttachApps()
        {
            if (!deck.ContainsKey("apps")) deck["apps"] = appsView;
        }

        // ---------- 단추 칸 ----------
        void RebuildTiles()
        {
            grid.SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in grid.Controls) old.Add(c);
            grid.Controls.Clear();
            foreach (var c in old) c.Dispose();
            var keys = CurKeys();
            if (keys != null)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    var k = keys[i] as Dictionary<string, object>;
                    if (k == null) continue;
                    var t = new Tile();
                    t.Key = k; t.Index = i;
                    t.Size = new Size(S(96), S(96));
                    t.Margin = new Padding(S(5));
                    t.Back = TileColor(k);
                    t.Sel = i == selIndex;
                    t.AllowDrop = true;
                    t.MouseDown += TileDown;
                    t.MouseMove += TileMove;
                    t.MouseUp += TileUp;
                    t.DragEnter += GridDragOver;
                    t.DragOver += GridDragOver;
                    t.DragDrop += GridDrop;
                    grid.Controls.Add(t);
                }
            }
            grid.ResumeLayout();
        }

        void RefreshTile()
        {
            foreach (Control c in grid.Controls)
            {
                var t = c as Tile; if (t == null) continue;
                t.Sel = t.Index == selIndex;
                if (t.Key == selKey) t.Back = TileColor(t.Key);
                t.Invalidate();
            }
        }

        Color TileColor(Dictionary<string, object> k)
        {
            string c = Str(k, "color");
            if (c == null)
            {
                if (cur != null && cur.App) c = AppColor;
                else
                {
                    string cat = Str(k, "cat");
                    object co; deck.TryGetValue("colors", out co);
                    var colors = co as Dictionary<string, object>;
                    object v;
                    if (cat != null && colors != null && colors.TryGetValue(cat, out v) && v != null) c = v.ToString();
                }
            }
            if (c == null) c = Gray;
            try { return ColorTranslator.FromHtml(c); } catch { return ColorTranslator.FromHtml(Gray); }
        }

        void SelectKey(int i)
        {
            var keys = CurKeys();
            if (keys != null && i >= 0 && i < keys.Count) { selKey = keys[i] as Dictionary<string, object>; selIndex = selKey == null ? -1 : i; }
            else { selKey = null; selIndex = -1; }
            RefreshTile();
            ShowKey();
        }

        void TileDown(object sender, MouseEventArgs e)
        {
            var t = (Tile)sender;
            if (t.Index != selIndex) SelectKey(t.Index);
            downPt = e.Location; downOnTile = e.Button == MouseButtons.Left;
        }

        void TileMove(object sender, MouseEventArgs e)
        {
            if (!downOnTile || e.Button != MouseButtons.Left) return;
            var ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - downPt.X) < ds.Width && Math.Abs(e.Y - downPt.Y) < ds.Height) return;
            downOnTile = false;
            var t = (Tile)sender;
            var data = new DataObject();
            data.SetData(TileFmt, (object)t.Index);
            t.DoDragDrop(data, DragDropEffects.Move);
        }

        void TileUp(object sender, MouseEventArgs e)
        {
            downOnTile = false;
            if (e.Button != MouseButtons.Right) return;
            var t = (Tile)sender;
            var m = new ContextMenuStrip();
            m.Items.Add("이름 바꾸기", null, delegate
            {
                string n = Ask("이름 바꾸기", Str(t.Key, "label") ?? "");
                if (n == null) return;
                t.Key["label"] = n; MarkDirty(); ShowKey(); RefreshTile();
            });
            m.Items.Add("아이콘 바꾸기", null, delegate
            {
                string n = Ask("아이콘 바꾸기", Str(t.Key, "icon") ?? "");
                if (n == null) return;
                t.Key["icon"] = n; MarkDirty(); ShowKey(); RefreshTile();
            });
            if (cur != null && !cur.App && Pages().Count > 1)
            {
                var mv = new ToolStripMenuItem("다른 쪽으로 옮기기");
                var pages = Pages();
                for (int i = 0; i < pages.Count; i++)
                {
                    if (i == cur.Index) continue;
                    var p = pages[i] as Dictionary<string, object>; if (p == null) continue;
                    int to = i;
                    mv.DropDownItems.Add(Str(p, "name") ?? "", null, delegate { MoveToPage(t.Index, to); });
                }
                m.Items.Add(mv);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("지우기", null, delegate
            {
                var keys = CurKeys(); if (keys == null || t.Index >= keys.Count) return;
                keys.RemoveAt(t.Index); MarkDirty();
                selKey = null; selIndex = -1; RebuildTiles(); ShowKey();
            });
            m.Show(t, e.Location);
        }

        void MoveToPage(int i, int page)
        {
            var keys = CurKeys(); if (keys == null || i < 0 || i >= keys.Count) return;
            var p = Pages()[page] as Dictionary<string, object>; if (p == null) return;
            var k = keys[i];
            keys.RemoveAt(i);
            Lst(p, "keys").Add(k);
            MarkDirty();
            selKey = null; selIndex = -1; RebuildTiles(); ShowKey();
        }

        int DropIndex(DragEventArgs e)
        {
            Point p = grid.PointToClient(new Point(e.X, e.Y));
            foreach (Control c in grid.Controls)
            {
                var t = c as Tile;
                if (t != null && t.Bounds.Contains(p)) return t.Index;
            }
            return -1;
        }

        void GridDragOver(object sender, DragEventArgs e)
        {
            if (cur == null) { e.Effect = DragDropEffects.None; return; }
            var d = e.Data;
            if (d.GetDataPresent(TileFmt)) { e.Effect = DragDropEffects.Move; return; }
            bool ok = d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent("UniformResourceLocatorW")
                || d.GetDataPresent("UniformResourceLocator") || d.GetDataPresent("text/x-moz-url")
                || d.GetDataPresent(DataFormats.UnicodeText) || d.GetDataPresent(DataFormats.Text);
            if (!ok) { e.Effect = DragDropEffects.None; return; }
            if ((e.AllowedEffect & DragDropEffects.Copy) != 0) e.Effect = DragDropEffects.Copy;
            else if ((e.AllowedEffect & DragDropEffects.Link) != 0) e.Effect = DragDropEffects.Link;
            else e.Effect = e.AllowedEffect & DragDropEffects.Move;
        }

        void GridDrop(object sender, DragEventArgs e)
        {
            var keys = CurKeys(); if (keys == null) return;
            int at = DropIndex(e);
            var d = e.Data;
            try
            {
                if (d.GetDataPresent(TileFmt))
                {
                    int from = (int)d.GetData(TileFmt);
                    if (from < 0 || from >= keys.Count) return;
                    int to = at < 0 ? keys.Count - 1 : at;
                    if (to == from) return;
                    var k = keys[from];
                    keys.RemoveAt(from);
                    if (to > keys.Count) to = keys.Count;
                    keys.Insert(to, k);
                    MarkDirty();
                    selIndex = to; selKey = k as Dictionary<string, object>;
                    RebuildTiles(); ShowKey();
                    return;
                }
                var made = new List<Dictionary<string, object>>();
                var files = d.GetDataPresent(DataFormats.FileDrop) ? d.GetData(DataFormats.FileDrop) as string[] : null;
                if (files != null && files.Length > 0)
                {
                    foreach (var f in files) { var k = FromFile(f); if (k != null) made.Add(k); }
                }
                else
                {
                    string title;
                    string url = ReadUrl(d, out title);
                    if (url != null) made.Add(NewKey("🌐", title ?? HostOf(url), url));
                }
                if (made.Count == 0) return;
                int pos = at < 0 ? keys.Count : at;
                foreach (var k in made) keys.Insert(pos++, k);
                MarkDirty();
                selIndex = pos - 1; selKey = made[made.Count - 1];
                RebuildTiles(); ShowKey();
                Activate();
            }
            catch { }
        }

        Dictionary<string, object> NewKey(string icon, string label, string target)
        {
            var k = new Dictionary<string, object>();
            if (cur != null && !cur.App)
            {
                var p = CurDict(); string pn = p == null ? null : Str(p, "name");
                object co; deck.TryGetValue("colors", out co);
                var colors = co as Dictionary<string, object>;
                if (pn != null && colors != null && colors.ContainsKey(pn)) k["cat"] = pn;
            }
            k["icon"] = icon;
            k["label"] = label;
            var st = new Dictionary<string, object>(); st["do"] = "open"; st["target"] = target;
            k["steps"] = new List<object> { st };
            return k;
        }

        Dictionary<string, object> FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Directory.Exists(path))
            {
                string nm = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(nm)) nm = path;
                return NewKey("📁", nm, path);
            }
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            string label = Path.GetFileNameWithoutExtension(path);
            if (ext == ".url")
            {
                try
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        string l = line.Trim();
                        if (l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return NewKey("🌐", label, l.Substring(4).Trim());
                    }
                }
                catch { }
            }
            string icon;
            switch (ext)
            {
                case ".lnk": case ".exe": icon = "🚀"; break;
                case ".pdf": icon = "📕"; break;
                case ".ppt": case ".pptx": icon = "📊"; break;
                case ".hwp": case ".hwpx": icon = "📝"; break;
                case ".xls": case ".xlsx": case ".csv": icon = "📗"; break;
                case ".doc": case ".docx": icon = "📄"; break;
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": case ".heic": icon = "🖼️"; break;
                case ".mp4": case ".mov": case ".avi": case ".mkv": case ".wmv": case ".webm": icon = "🎬"; break;
                default: icon = "📄"; break;
            }
            return NewKey(icon, label, path);
        }

        // 브라우저에서 끌어온 주소(크롬 주소창 자물쇠, 링크 등)
        static string ReadUrl(IDataObject d, out string title)
        {
            title = null;
            string url = ReadFmt(d, "UniformResourceLocatorW", true);
            if (!IsUrl(url)) url = ReadFmt(d, "UniformResourceLocator", false);
            if (!IsUrl(url))
            {
                string moz = ReadFmt(d, "text/x-moz-url", true);   // 파이어폭스: 주소\n제목
                if (moz != null)
                {
                    var parts = moz.Split('\n');
                    url = parts[0].Trim();
                    if (parts.Length > 1 && parts[1].Trim() != "") title = parts[1].Trim();
                }
            }
            if (!IsUrl(url))
            {
                string t = null;
                try { if (d.GetDataPresent(DataFormats.UnicodeText)) t = d.GetData(DataFormats.UnicodeText) as string; } catch { }
                if (t == null) { try { if (d.GetDataPresent(DataFormats.Text)) t = d.GetData(DataFormats.Text) as string; } catch { } }
                if (t != null) url = t.Trim().Split('\n')[0].Trim();
            }
            if (!IsUrl(url)) return null;
            if (title == null)
            {
                try
                {
                    if (d.GetDataPresent("FileGroupDescriptorW"))
                    {
                        var ms = d.GetData("FileGroupDescriptorW") as MemoryStream;
                        if (ms != null)
                        {
                            byte[] b = ms.ToArray();
                            // FILEGROUPDESCRIPTORW: cItems(4) + FILEDESCRIPTORW(앞 72바이트 뒤 cFileName)
                            if (b.Length > 76)
                            {
                                string fn = Encoding.Unicode.GetString(b, 76, Math.Min(520, b.Length - 76));
                                int z = fn.IndexOf('\0'); if (z >= 0) fn = fn.Substring(0, z);
                                if (fn.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) fn = fn.Substring(0, fn.Length - 4);
                                fn = fn.Trim();
                                if (fn != "") title = fn;
                            }
                        }
                    }
                }
                catch { }
            }
            return url;
        }

        static string ReadFmt(IDataObject d, string fmt, bool unicode)
        {
            try
            {
                if (!d.GetDataPresent(fmt)) return null;
                object o = d.GetData(fmt);
                string s = o as string;
                if (s == null)
                {
                    var ms = o as MemoryStream; if (ms == null) return null;
                    byte[] b = ms.ToArray();
                    s = unicode ? Encoding.Unicode.GetString(b) : Encoding.Default.GetString(b);
                }
                int z = s.IndexOf('\0'); if (z >= 0) s = s.Substring(0, z);
                return s.Trim();
            }
            catch { return null; }
        }

        static bool IsUrl(string s)
        {
            return s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        static string HostOf(string url)
        {
            try { string h = new Uri(url).Host; return h.StartsWith("www.") ? h.Substring(4) : h; } catch { return url; }
        }

        // ---------- 고른 단추 ----------
        void ShowKey()
        {
            loading = true;
            bool on = selKey != null;
            foreach (Control c in right.Controls) c.Enabled = on;
            nameBox.Text = on ? Str(selKey, "label") ?? "" : "";
            iconBox.Text = on ? Str(selKey, "icon") ?? "" : "";
            FillSteps(-1);
            loading = false;
            ShowStep();
        }

        // 보이기용 순서(옛 단추는 바꾸지 않고 보이기만)
        List<object> ViewSteps()
        {
            if (selKey == null) return new List<object>();
            if (selKey.ContainsKey("steps")) return Lst(selKey, "steps");
            return Steps.FromLegacy(selKey);
        }

        // 고칠 때: 옛 칸(web·app·key)을 동작 순서로 바꾼다
        List<object> EditSteps()
        {
            if (!selKey.ContainsKey("steps"))
            {
                var l = Steps.FromLegacy(selKey);
                selKey.Remove("web"); selKey.Remove("app"); selKey.Remove("key");
                selKey["steps"] = l;
            }
            MarkDirty();
            return Lst(selKey, "steps");
        }

        void FillSteps(int sel)
        {
            bool was = loading; loading = true;
            stepList.Items.Clear();
            foreach (var o in ViewSteps())
            {
                var s = o as Dictionary<string, object>;
                stepList.Items.Add(s == null ? "?" : StepLine(s));
            }
            if (sel >= 0 && sel < stepList.Items.Count) stepList.SelectedIndex = sel;
            loading = was;
        }

        static string KindName(string d)
        {
            switch (d)
            {
                case "open": return "열기";
                case "wait": return "기다리기";
                case "waitwin": return "창 기다리기";
                case "focus": return "창 앞으로";
                case "key": return "키";
                case "type": return "글자";
                case "click": return "클릭";
            }
            return d;
        }

        static string StepLine(Dictionary<string, object> s)
        {
            string d = Str(s, "do") ?? "";
            string head = KindName(d) + " · ";
            switch (d)
            {
                case "open": return head + (Str(s, "target") ?? "");
                case "wait": return head + Sec(s, 1000) + "초";
                case "waitwin": return head + (Str(s, "title") ?? "");
                case "focus": return head + (Str(s, "title") ?? "");
                case "key": return head + (Str(s, "key") ?? "");
                case "type":
                    {
                        string t = (Str(s, "text") ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
                        if (t.Length > 30) t = t.Substring(0, 30) + "…";
                        return head + t;
                    }
                case "click":
                    return head + (Str(s, "rel") == "screen" ? "화면 기준 " : "창 기준 ") + Int(s, "x", 0) + "," + Int(s, "y", 0);
            }
            return d;
        }

        static string Sec(Dictionary<string, object> s, int def)
        {
            return (Int(s, "ms", def) / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        Dictionary<string, object> ViewStep()
        {
            int i = stepList.SelectedIndex; var l = ViewSteps();
            return i >= 0 && i < l.Count ? l[i] as Dictionary<string, object> : null;
        }

        Dictionary<string, object> EditStep()
        {
            int i = stepList.SelectedIndex; if (i < 0 || selKey == null) return null;
            var l = EditSteps();
            return i < l.Count ? l[i] as Dictionary<string, object> : null;
        }

        void UpdateLine()
        {
            int i = stepList.SelectedIndex; if (i < 0) return;
            var s = ViewStep(); if (s == null) return;
            bool was = loading; loading = true;
            stepList.Items[i] = StepLine(s);
            stepList.SelectedIndex = i;
            loading = was;
        }

        void ShowStep()
        {
            loading = true;
            foreach (Control c in stepPanel.Controls) c.Visible = false;
            var s = ViewStep();
            bool has = s != null;
            bUp.Enabled = has && stepList.SelectedIndex > 0 && selKey != null;
            bDown.Enabled = has && stepList.SelectedIndex < stepList.Items.Count - 1 && selKey != null;
            bDelStep.Enabled = has && selKey != null;
            if (has)
            {
                string d = Str(s, "do") ?? "";
                stepText.Multiline = false;
                stepText.Height = S(24);
                switch (d)
                {
                    case "open":
                        Reveal(stepLabel, "주소·파일·폴더"); Reveal(stepText, null);
                        stepText.Text = Str(s, "target") ?? "";
                        break;
                    case "wait":
                        Reveal(secLabel, "초"); Reveal(secBox, null);
                        secLabel.Top = S(4); secBox.Top = S(0);
                        secBox.Value = ClampSec(Int(s, "ms", 1000));
                        break;
                    case "waitwin":
                        Reveal(stepLabel, "창 제목에 든 글"); Reveal(stepText, null);
                        Reveal(secLabel, "최대 초"); Reveal(secBox, null);
                        secLabel.Top = S(60); secBox.Top = S(56);
                        stepText.Text = Str(s, "title") ?? "";
                        secBox.Value = ClampSec(Int(s, "ms", 10000));
                        break;
                    case "focus":
                        Reveal(stepLabel, "창 제목에 든 글"); Reveal(stepText, null);
                        stepText.Text = Str(s, "title") ?? "";
                        break;
                    case "key":
                        Reveal(stepLabel, "누를 키"); Reveal(keyBox, null); Reveal(winBox, null);
                        string k = Str(s, "key") ?? "";
                        keyBox.Text = k;
                        winBox.Checked = HasWin(k);
                        break;
                    case "type":
                        Reveal(stepLabel, "넣을 글자"); Reveal(stepText, null); Reveal(note, null);
                        stepText.Multiline = true;
                        stepText.ScrollBars = ScrollBars.Vertical;
                        stepText.Height = S(90);
                        stepText.Text = (Str(s, "text") ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                        break;
                    case "click":
                        Reveal(stepLabel, "위치"); Reveal(xBox, null); Reveal(yBox, null); Reveal(relBox, null); Reveal(bPick, null);
                        xBox.Value = Clamp(Int(s, "x", 0), xBox); yBox.Value = Clamp(Int(s, "y", 0), yBox);
                        relBox.SelectedIndex = Str(s, "rel") == "screen" ? 1 : 0;
                        break;
                }
            }
            loading = false;
        }

        void Reveal(Control c, string text) { if (text != null) c.Text = text; c.Visible = true; }

        static decimal ClampSec(int ms)
        {
            decimal v = ms / 1000m; if (v < 0) v = 0; if (v > 30) v = 30; return Math.Round(v, 1);
        }

        static decimal Clamp(int v, NumericUpDown n)
        {
            decimal d = v; if (d < n.Minimum) d = n.Minimum; if (d > n.Maximum) d = n.Maximum; return d;
        }

        void AddStep(string kind)
        {
            if (selKey == null) return;
            var s = new Dictionary<string, object>();
            s["do"] = kind;
            switch (kind)
            {
                case "open": s["target"] = ""; break;
                case "wait": s["ms"] = 1000; break;
                case "waitwin": s["title"] = ""; s["ms"] = 10000; break;
                case "focus": s["title"] = ""; break;
                case "key": s["key"] = ""; break;
                case "type": s["text"] = ""; break;
                case "click": s["x"] = 0; s["y"] = 0; s["rel"] = "win"; break;
            }
            var l = EditSteps();
            int at = stepList.SelectedIndex < 0 ? l.Count : stepList.SelectedIndex + 1;
            if (at > l.Count) at = l.Count;
            l.Insert(at, s);
            FillSteps(at);
            ShowStep();
            if (stepText.Visible) stepText.Focus(); else if (keyBox.Visible) keyBox.Focus();
        }

        void MoveStep(int dir)
        {
            int i = stepList.SelectedIndex; if (i < 0 || selKey == null) return;
            var l = EditSteps();
            int j = i + dir; if (j < 0 || j >= l.Count) return;
            var s = l[i]; l.RemoveAt(i); l.Insert(j, s);
            FillSteps(j); ShowStep();
        }

        void DeleteStep()
        {
            int i = stepList.SelectedIndex; if (i < 0 || selKey == null) return;
            var l = EditSteps();
            if (i >= l.Count) return;
            l.RemoveAt(i);
            FillSteps(Math.Min(i, l.Count - 1)); ShowStep();
        }

        void StepTextChanged()
        {
            if (loading) return;
            var s = EditStep(); if (s == null) return;
            string d = Str(s, "do");
            if (d == "open") s["target"] = stepText.Text.Trim();
            else if (d == "waitwin" || d == "focus") s["title"] = stepText.Text.Trim();
            else if (d == "type") s["text"] = stepText.Text.Replace("\r\n", "\n");
            UpdateLine();
        }

        void SecChanged()
        {
            if (loading) return;
            var s = EditStep(); if (s == null) return;
            s["ms"] = (int)(secBox.Value * 1000);
            UpdateLine();
        }

        void ClickChanged()
        {
            if (loading) return;
            var s = EditStep(); if (s == null || Str(s, "do") != "click") return;
            s["x"] = (int)xBox.Value; s["y"] = (int)yBox.Value;
            s["rel"] = relBox.SelectedIndex == 1 ? "screen" : "win";
            UpdateLine();
        }

        static bool HasWin(string combo)
        {
            foreach (var p in combo.Split('+')) if (string.Equals(p.Trim(), "win", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void WinChanged()
        {
            if (loading) return;
            var s = EditStep(); if (s == null || Str(s, "do") != "key") return;
            string k = Str(s, "key") ?? "";
            var parts = new List<string>();
            foreach (var p in k.Split('+')) if (p != "" && !string.Equals(p.Trim(), "win", StringComparison.OrdinalIgnoreCase)) parts.Add(p);
            if (k.EndsWith("+") && k.Length > 1) parts.Add("+");   // "ctrl++" 같은 끝의 '+'
            if (winBox.Checked) parts.Insert(0, "win");
            k = string.Join("+", parts.ToArray());
            s["key"] = k; keyBox.Text = k;
            UpdateLine();
        }

        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

        void KeyCapture(object sender, KeyEventArgs e)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            string name = KeyName(e.KeyCode);
            if (name == null) return;   // 수정 키만 누른 때는 기다린다
            var parts = new List<string>();
            bool win = winBox.Checked || GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0;
            if (win) parts.Add("win");
            if (e.Control) parts.Add("ctrl");
            if (e.Shift) parts.Add("shift");
            if (e.Alt) parts.Add("alt");
            parts.Add(name);
            string combo = string.Join("+", parts.ToArray());
            var s = EditStep(); if (s == null) return;
            s["key"] = combo;
            loading = true; keyBox.Text = combo; winBox.Checked = win; loading = false;
            UpdateLine();
        }

        // Input.Combo 가 아는 이름으로
        static string KeyName(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return ((char)('0' + (k - Keys.NumPad0))).ToString();
            if (k >= Keys.F1 && k <= Keys.F24) return "F" + (1 + (k - Keys.F1));
            switch (k)
            {
                case Keys.Escape: return "Esc";
                case Keys.Return: return "Enter";
                case Keys.Space: return "Space";
                case Keys.Tab: return "Tab";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Left: return "Left";
                case Keys.Up: return "Up";
                case Keys.Right: return "Right";
                case Keys.Down: return "Down";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Oemplus: return "=";
                case Keys.OemMinus: return "-";
                case Keys.OemPeriod: return ".";
                case Keys.Oemcomma: return ",";
                case Keys.OemPipe: return "\\";
                case Keys.OemBackslash: return "\\";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.VolumeUp: return "VolumeUp";
                case Keys.VolumeDown: return "VolumeDown";
                case Keys.VolumeMute: return "VolumeMute";
                case Keys.MediaPlayPause: return "PlayPause";
                case Keys.MediaNextTrack: return "NextTrack";
                case Keys.MediaPreviousTrack: return "PrevTrack";
            }
            return null;
        }

        void PickPoint()
        {
            Countdown("3초 안에 누를 곳에 마우스를 올리세요", delegate
            {
                var s = EditStep(); if (s == null || Str(s, "do") != "click") return;
                Point p = Cursor.Position;
                IntPtr h = Win.GetForegroundWindow();
                Win.RECT r;
                if (h != IntPtr.Zero && Win.GetWindowRect(h, out r)) { s["x"] = p.X - r.Left; s["y"] = p.Y - r.Top; s["rel"] = "win"; }
                else { s["x"] = p.X; s["y"] = p.Y; s["rel"] = "screen"; }
                ShowStep();
                UpdateLine();
                Activate();
            });
        }

        void TestRun()
        {
            if (selKey == null) return;
            Dictionary<string, object> k = selKey;
            if (!selKey.ContainsKey("steps")) { k = new Dictionary<string, object>(); k["steps"] = Steps.FromLegacy(selKey); }
            Steps.Run(k, delegate (string err)
            {
                try { BeginInvoke(new Action(delegate { MessageBox.Show(this, err, "시험 실행"); })); } catch { }
            });
        }

        // ---------- 3초 세기 ----------
        void Countdown(string msg, Action done)
        {
            if (countTimer.Enabled) return;
            countLeft = 3; countMsg = msg; countDone = done;
            status.Text = msg + " · 3";
            countTimer.Start();
        }

        void CountTick()
        {
            countLeft--;
            if (countLeft > 0) { status.Text = countMsg + " · " + countLeft; return; }
            countTimer.Stop();
            status.Text = "";
            var a = countDone; countDone = null;
            if (a != null) a();
        }

        // ---------- 저장·닫기 ----------
        void MarkDirty()
        {
            if (cur != null && cur.App) AttachApps();
            if (!dirty) { dirty = true; Text = "단추 고치기 *"; }
        }

        bool Save()
        {
            try
            {
                var sb = new StringBuilder();
                Write(sb, deck, 0);
                sb.Append('\n');
                File.WriteAllText(store.DeckPath, sb.ToString(), new UTF8Encoding(false));
                dirty = false; Text = "단추 고치기";
                status.Text = "저장했습니다";
                return true;
            }
            catch
            {
                MessageBox.Show(this, "저장하지 못했습니다.", Text);
                return false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (dirty && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "고친 것을 저장할까요?", "단추 고치기", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !Save())) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            countTimer.Stop();
            if (inst == this) inst = null;
            base.OnFormClosed(e);
        }

        // 읽기 쉬운 JSON: 안에 묶음이 없는 것은 한 줄로
        void Write(StringBuilder sb, object v, int ind)
        {
            var d = v as Dictionary<string, object>;
            if (d != null)
            {
                if (Flat(d.Values)) { sb.Append(json.Serialize(v)); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in d)
                {
                    sb.Append(' ', ind + 2).Append(json.Serialize(kv.Key)).Append(": ");
                    Write(sb, kv.Value, ind + 2);
                    if (++i < d.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', ind).Append('}');
                return;
            }
            var e = v as IEnumerable;
            if (e != null && !(v is string))
            {
                var items = new List<object>(); foreach (var o in e) items.Add(o);
                if (Flat(items)) { sb.Append(json.Serialize(items)); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(' ', ind + 2);
                    Write(sb, items[i], ind + 2);
                    if (i < items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', ind).Append(']');
                return;
            }
            sb.Append(json.Serialize(v));
        }

        static bool Flat(IEnumerable values)
        {
            foreach (var o in values)
            {
                if (o is Dictionary<string, object>) return false;
                if (o is IEnumerable && !(o is string))
                {
                    // 글자만 든 짧은 목록(exe 등)은 한 줄로 둔다
                    foreach (var x in (IEnumerable)o) if (x is Dictionary<string, object> || (x is IEnumerable && !(x is string))) return false;
                }
            }
            return true;
        }

        // ---------- 도움 ----------
        string Ask(string title, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.Font = Font;
                f.AutoScaleMode = AutoScaleMode.None;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(S(320), S(92));
                var tb = new TextBox();
                tb.Location = new Point(S(12), S(14)); tb.Width = S(296); tb.Text = value;
                f.Controls.Add(tb);
                var ok = new Button(); ok.Text = "확인"; ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(S(80), S(28)); ok.Location = new Point(S(140), S(52));
                var no = new Button(); no.Text = "취소"; no.DialogResult = DialogResult.Cancel;
                no.Size = new Size(S(80), S(28)); no.Location = new Point(S(228), S(52));
                f.Controls.Add(ok); f.Controls.Add(no);
                f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                return tb.Text.Trim();
            }
        }

        static List<object> Lst(Dictionary<string, object> d, string f)
        {
            object v; d.TryGetValue(f, out v);
            var l = v as List<object>;
            if (l != null) return l;
            l = new List<object>();
            var e = v as IEnumerable;
            if (e != null && !(v is string)) foreach (var o in e) l.Add(o);
            d[f] = l;
            return l;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }
        static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v; double r;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) return (int)Math.Round(r);
            return def;
        }
    }

    // 단추 칸 하나(폰과 같은 색)
    class Tile : Control
    {
        public Dictionary<string, object> Key;
        public int Index;
        public Color Back;
        public bool Sel;
        static readonly Font emoji = new Font("Segoe UI Emoji", 22f);
        static readonly Font small = new Font("맑은 고딕", 9f);

        public Tile()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            int rad = Math.Max(6, Width / 8);
            using (var path = Round(r, rad))
            {
                using (var b = new SolidBrush(Back)) g.FillPath(b, path);
                if (Sel) using (var p = new Pen(Color.White, 3f)) g.DrawPath(p, path);
            }
            object iv, lv;
            string icon = Key != null && Key.TryGetValue("icon", out iv) && iv != null ? iv.ToString() : "";
            string label = Key != null && Key.TryGetValue("label", out lv) && lv != null ? lv.ToString() : "";
            // 폰에서 선 아이콘(ti-…)으로 그리는 단추는 여기서는 이름 첫 글자로 보인다
            if (icon.StartsWith("ti-")) icon = label.Length > 0 ? label.Substring(0, 1) : "•";
            var top = new Rectangle(0, Height / 12, Width, Height * 6 / 12);
            TextRenderer.DrawText(g, icon, emoji, top, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var bot = new Rectangle(4, Height * 7 / 12, Width - 8, Height * 5 / 12 - 4);
            TextRenderer.DrawText(g, label, small, bot, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
