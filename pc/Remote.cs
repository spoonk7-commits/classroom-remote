// 교실 리모컨 — PC 프로그램(1단계 시제품).
// .NET Framework 4.8(윈도우 10·11 기본), C# 5. 빌드: build.sh
// 알림 영역에 머물며 초대 코드로 받은 중계 서버의 방에 붙어, 폰이 보낸 단추·제스처를 실행한다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

// 파일 속성에 보이는 이름·판(이름 없는 실행 파일은 더 의심받는다)
[assembly: AssemblyTitle("교실 리모컨")]
[assembly: AssemblyDescription("폰·아이패드로 교실 PC를 조작하는 리모컨")]
[assembly: AssemblyProduct("교실 리모컨")]
[assembly: AssemblyCompany("교실 리모컨")]
[assembly: AssemblyCopyright("2026")]
[assembly: AssemblyVersion("0.6.0.0")]
[assembly: AssemblyFileVersion("0.6.0.0")]

namespace StreamRemote
{
    static class Program
    {
        // 중계 서버 주소는 코드에 넣지 않는다. 초대 코드(주소/코드)로 받아 설정\연결.json 에 둔다.
        public static string Host;
        public const string AppName = "교실 리모컨";

        [STAThread]
        static void Main(string[] args)
        {
            bool fresh;
            using (var mx = new Mutex(true, "StreamRemote_one", out fresh))
            {
                if (!fresh) { MessageBox.Show("이미 켜져 있습니다. 알림 영역의 리모컨 아이콘을 보세요.", AppName); return; }
                Application.EnableVisualStyles();
                // 리모컨 폴더 밖에서 켰으면 D:\리모컨(없으면 받은 파일\리모컨)으로 옮긴다
                if (!Home.IsHome(Home.ExeDir) && Array.IndexOf(args, "--here") < 0)
                {
                    string moved = Home.OfferInstall();
                    if (moved != null)
                    {
                        mx.ReleaseMutex(); mx.Close();
                        Process.Start(new ProcessStartInfo(moved) { UseShellExecute = true });
                        return;
                    }
                }
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(Array.IndexOf(args, "--quiet") >= 0));
            }
        }
    }

    // ---- 리모컨 폴더 ----
    static class Home
    {
        public const string Folder = "리모컨";
        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static bool IsHome(string dir) { return string.Equals(Path.GetFileName(dir.TrimEnd('\\')), Folder, StringComparison.OrdinalIgnoreCase); }

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
        static string Downloads()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
            { string r = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); return r; }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static string Pick(out bool onD)
        {
            onD = false;
            try
            {
                var d = new DriveInfo("D");
                if (d.IsReady && d.DriveType == DriveType.Fixed) { onD = true; return @"D:\" + Folder; }
            }
            catch { }
            return Path.Combine(Downloads(), Folder);
        }

        // 옮겼으면 새 실행 파일 경로, 아니면 null(지금 자리에서 그대로 씀)
        public static string OfferInstall()
        {
            bool onD; string target = Pick(out onD);
            string msg = "프로그램과 기록을 둘 폴더를 만듭니다.\n\n" + target + "\n\n바탕화면에는 바로가기를 둡니다.";
            if (!onD) msg += "\n\n이 PC에는 D 드라이브가 없어 받은 파일 폴더에 둡니다. 학교 PC에 복원 프로그램이 있으면 껐다 켤 때 지워질 수 있습니다.";
            if (MessageBox.Show(msg, Program.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return null;
            try
            {
                Directory.CreateDirectory(Path.Combine(target, "설정"));
                string exe = Path.Combine(target, Path.GetFileName(Application.ExecutablePath));
                File.Copy(Application.ExecutablePath, exe, true);
                // 예전 자리(%APPDATA%\StreamRemote)의 연결 정보가 있으면 이어서 쓴다
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamRemote");
                foreach (var f in new[] { "연결.json", "단추.json" })
                {
                    string src = Path.Combine(old, f), dst = Path.Combine(target, "설정", f);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
                Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "리모컨.lnk"), exe);
                return exe;
            }
            catch (Exception e)
            {
                MessageBox.Show("폴더를 만들지 못했습니다. 지금 자리에서 씁니다.\n\n" + e.Message, Program.AppName);
                return null;
            }
        }

        static void Shortcut(string lnk, string target)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                dynamic sc = sh.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.Save();
            }
            catch { }
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly Control ui;                 // UI 스레드로 넘기기용
        readonly Store store;
        readonly Link link;
        // 믿는 기기: 중계가 붙인 기기 이름 → 이번 연결 번호·순번
        readonly Dictionary<string, Sess> trusted = new Dictionary<string, Sess>();
        class Sess { public string N; public long Seq; }
        Seal seal;
        string lastSaved;   // 마지막으로 받은 파일(알림을 누르면 탐색기에서 보임)
        FileSystemWatcher watch;
        // 지금 앞에 있는 앱과 맞는 앱 단추 묶음
        Dictionary<string, object> appRule; string appName;
        readonly System.Windows.Forms.Timer appTimer = new System.Windows.Forms.Timer();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public TrayApp(bool quiet)
        {
            ui = new Control(); ui.CreateControl();
            store = new Store();
            seal = new Seal(store.Key);
            if (store.Host == null || store.Inv == null)
            {
                if (!Invite.Ask(store, store.FirstRun)) { ExitThreadLater(); return; }
            }
            Program.Host = store.Host;
            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
            tray.Text = Program.AppName;
            tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("연결 QR 보기", null, delegate { ShowQr(); });
            menu.Items.Add("단추 고치기", null, delegate { EditDeck(); });
            menu.Items.Add("폰으로 보낼 파일", null, delegate { Files.EnsureDirs(); try { Process.Start("explorer.exe", "\"" + Files.OutDir + "\""); } catch { } });
            menu.Items.Add("리모컨 폴더 열기", null, delegate { try { Process.Start("explorer.exe", "\"" + Home.ExeDir + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("윈도우 켤 때 같이 켜기") { Checked = Autostart.IsOn() };
            auto.Click += delegate { Autostart.Set(!auto.Checked); auto.Checked = Autostart.IsOn(); };
            menu.Items.Add(auto);
            menu.Items.Add("연결한 기기 모두 끊기", null, delegate { ResetKey(); });
            menu.Items.Add("초대 코드 바꾸기", null, delegate
            {
                if (Invite.Ask(store, false)) { Program.Host = store.Host; link.Reconnect(store.Inv); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("끝내기", null, delegate { Quit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowQr(); };
            tray.BalloonTipClicked += delegate { if (lastSaved != null) try { Process.Start("explorer.exe", "/select,\"" + lastSaved + "\""); } catch { } };

            if (Autostart.IsOn()) Autostart.Set(true);   // 폴더를 옮겼으면 경로를 새로
            Rec.EnsureTemplates();
            Files.EnsureDirs();
            WatchDeck();
            link = new Link(store.Room, store.Inv);
            link.OnManyFails = delegate { Ui(CheckInvite); };
            link.OnMessage = OnRelay;
            link.OnState = delegate (bool on) { Ui(delegate { UpdateTip(on); }); };
            link.Start();

            appTimer.Interval = 400;
            appTimer.Tick += delegate { WatchApp(); };
            appTimer.Start();

            if (store.FirstRun && !quiet) ShowQr();
        }

        // 연결이 잇달아 안 되면 초대 코드가 끊겼는지 본다
        void CheckInvite()
        {
            string host = store.Host, inv = store.Inv;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, object> p;
                if (Invite.Check(host, inv, out p) == 0)
                    Ui(delegate { tray.ShowBalloonTip(5000, Program.AppName, "초대 코드가 끊겼습니다. 알림 영역 메뉴의 「초대 코드 바꾸기」로 새 코드를 넣어 주세요.", ToolTipIcon.Warning); });
            });
        }

        // 생성자 안에서 바로 끝낼 때
        void ExitThreadLater()
        {
            var t = new System.Windows.Forms.Timer { Interval = 10 };
            t.Tick += delegate { t.Stop(); ExitThread(); };
            t.Start();
        }

        void Ui(Action a) { try { if (ui.IsHandleCreated) ui.BeginInvoke(a); } catch { } }

        void UpdateTip(bool on)
        {
            string t = Program.AppName + (on ? " · 연결됨" : " · 연결 안 됨");
            if (trusted.Count > 0) t += " · 기기 " + trusted.Count;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void ShowQr()
        {
            OpenUrl("https://" + Program.Host + "/pair#r=" + store.Room + "&k=" + store.Key);
        }

        void EditDeck()
        {
            // 끌어다 놓기 편집 창(Editor.cs). 저장하면 단추.json 이 바뀌어 폰에 저절로 반영된다.
            DeckEditor.Open(store);
        }

        void ResetKey()
        {
            if (MessageBox.Show("연결한 폰·아이패드를 모두 끊고 새 QR을 만듭니다. 기기마다 QR을 다시 찍어야 합니다.", Program.AppName,
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            store.NewKey();
            seal = new Seal(store.Key);
            lock (trusted) trusted.Clear();
            link.Send("{\"t\":\"bad\"}");
            ShowQr();
        }

        void Quit()
        {
            tray.Visible = false;
            link.Stop();
            ExitThread();
        }

        void WatchDeck()
        {
            try
            {
                watch = new FileSystemWatcher(Path.GetDirectoryName(store.DeckPath), Path.GetFileName(store.DeckPath));
                watch.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                watch.Changed += delegate { Thread.Sleep(300); SendDeck(null); Ui(delegate { appName = "-"; appRule = null; WatchApp(); }); };
                watch.EnableRaisingEvents = true;
            }
            catch { }
        }

        // ---- 중계에서 온 것 ----
        void OnRelay(string raw)
        {
            Dictionary<string, object> outer;
            try { outer = json.DeserializeObject(raw) as Dictionary<string, object>; } catch { return; }
            if (outer == null) return;
            string from = Str(outer, "from");
            if (from == null) return;
            if (!outer.ContainsKey("m"))
            {
                // 중계가 직접 보낸 알림(join·leave)
                if (Str(outer, "t") == "leave") { lock (trusted) trusted.Remove(from); Ui(delegate { UpdateTip(true); }); }
                return;
            }
            // 폰이 보낸 것은 모두 잠겨 있어야 한다. 풀리면 같은 열쇠를 가진 기기.
            string plain = seal.Open(Str(outer, "m") ?? "");
            if (plain == null) { To(from, "{\"t\":\"bad\"}"); return; }
            Dictionary<string, object> m;
            try { m = json.DeserializeObject(plain) as Dictionary<string, object>; } catch { return; }
            if (m == null) return;
            string t = Str(m, "t");

            if (t == "hello")
            {
                var ss = new Sess { N = Store.Rand(12), Seq = 0 };
                lock (trusted) trusted[from] = ss;
                SendDeck(from);
                SendApp(from);
                Ui(delegate { UpdateTip(true); });
                return;
            }
            Sess cur; bool ok;
            lock (trusted)
            {
                ok = trusted.TryGetValue(from, out cur) && Str(m, "sn") == cur.N && Long(m, "s") > cur.Seq;
                if (ok) cur.Seq = Long(m, "s");
            }
            if (!ok) return;   // 다시 보낸 것·옛 연결의 것은 버린다

            if (t == "run") RunKey(from, Int(m, "a") == 1 ? AppKey(Int(m, "i")) : store.GetKey(Int(m, "p"), Int(m, "i")));
            else if (t == "g") Gesture(Str(m, "g"), Int(m, "n"));
            else if (t == "bye") { lock (trusted) trusted.Remove(from); Ui(delegate { UpdateTip(true); }); }
            else if (t == "tt") ToSealed(from, json.Serialize(Rec.Timetable()));
            // ---- 사진·파일 주고받기(Files.cs) ----
            else if (t == "fput")
            {
                string err = Files.Begin(from, Str(m, "id"), Str(m, "name"), Long(m, "size"), Int(m, "n"), Str(m, "kind"));
                if (err != null) FileDone(from, Str(m, "id"), false, err);
            }
            else if (t == "fchunk")
            {
                string err = Files.Piece(from, Str(m, "id"), Int(m, "i"), Str(m, "d"));
                if (err != null) FileDone(from, Str(m, "id"), false, err);
            }
            else if (t == "fend")
            {
                string err, path = Files.End(from, Str(m, "id"), out err);
                bool photo = path != null && path.IndexOf("\\사진\\", StringComparison.Ordinal) >= 0;
                FileDone(from, Str(m, "id"), path != null, err ?? (photo ? "사진을 저장했습니다" : "파일을 저장했습니다"));
                if (path != null) Ui(delegate {
                    lastSaved = path;
                    tray.ShowBalloonTip(3000, Program.AppName, (photo ? "사진을 받았습니다: " : "파일을 받았습니다: ") + Path.GetFileName(path), ToolTipIcon.Info);
                });
            }
            else if (t == "flist") ToSealed(from, json.Serialize(Files.List()));
            else if (t == "fget")
            {
                string name = Str(m, "name"), dev = from;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string err = Files.SendFile(name, delegate (Dictionary<string, object> part)
                    {
                        lock (trusted) if (!trusted.ContainsKey(dev)) return false;
                        return link.SendNow("{\"to\":\"" + dev + "\",\"m\":" + json.Serialize(seal.Close(json.Serialize(part))) + "}");
                    });
                    if (err != null) ToSealed(dev, json.Serialize(new Dictionary<string, object> { { "t", "ok" }, { "msg", err } }));
                });
            }
            else if (t == "roster") ToSealed(from, json.Serialize(Rec.Roster(Str(m, "cls"), Str(m, "p"))));
            else if (t == "att")
            {
                object mk; m.TryGetValue("marks", out mk);
                string err = Rec.SaveAtt(Str(m, "cls"), Str(m, "p"), mk as Dictionary<string, object>);
                ToSealed(from, json.Serialize(new Dictionary<string, object> { { "t", "ok" }, { "msg", err ?? "저장했습니다" }, { "saved", err == null ? "att" : null } }));
            }
            else if (t == "memo")
            {
                string err = Rec.SaveMemo(Str(m, "cls"), Str(m, "p"), Str(m, "no"), Str(m, "text"));
                ToSealed(from, json.Serialize(new Dictionary<string, object> { { "t", "ok" }, { "msg", err ?? "메모를 남겼습니다" } }));
            }
        }

        // 잠그지 않고 보내는 것은 "bad" 하나뿐
        void To(string dev, string body)
        {
            // 중계는 {"to":"기기","m":"<문자열>"} 모양만 본다
            link.Send("{\"to\":\"" + dev + "\",\"m\":" + json.Serialize(body) + "}");
        }
        void ToSealed(string dev, string body) { To(dev, seal.Close(body)); }
        void FileDone(string dev, string id, bool ok, string msg)
        {
            ToSealed(dev, json.Serialize(new Dictionary<string, object> { { "t", "fdone" }, { "id", id }, { "ok", ok }, { "msg", msg } }));
        }

        void SendDeck(string dev)
        {
            string deck = store.ReadDeckForPhone();
            if (deck == null) return;
            List<KeyValuePair<string, Sess>> list;
            lock (trusted) list = new List<KeyValuePair<string, Sess>>(trusted);
            foreach (var kv in list)
            {
                if (dev != null && kv.Key != dev) continue;
                ToSealed(kv.Key, deck.Insert(1, "\"t\":\"deck\",\"sn\":\"" + kv.Value.N + "\","));
            }
        }

        // ---- 앞에 있는 앱 따라 바뀌는 단추 ----
        void WatchApp()
        {
            string exe, title;
            if (!Fore.Get(out exe, out title)) return;      // 작업 표시줄·바탕화면·이 프로그램이면 그대로 둠
            Dictionary<string, object> hit = null;
            foreach (var o in store.Apps())
            {
                var r = o as Dictionary<string, object>; if (r == null) continue;
                object ex; r.TryGetValue("exe", out ex);
                bool exeOk = false;
                foreach (var e in (ex as object[]) ?? new object[0])
                    if (string.Equals(e.ToString(), exe, StringComparison.OrdinalIgnoreCase)) { exeOk = true; break; }
                if (!exeOk) continue;
                string tt = Str(r, "title");
                if (tt != null && title.IndexOf(tt, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hit = r; break;
            }
            string name = hit == null ? null : Str(hit, "name");
            if (name == appName) { appRule = hit; return; }
            appRule = hit; appName = name;
            SendApp(null);
        }

        void SendApp(string dev)
        {
            var o = new Dictionary<string, object>();
            o["t"] = "app";
            var rule = appRule;
            o["name"] = rule == null ? null : Str(rule, "name");
            var keys = new List<object>();
            if (rule != null)
            {
                object ks; rule.TryGetValue("keys", out ks);
                foreach (var ko in (ks as object[]) ?? new object[0])
                {
                    var k = ko as Dictionary<string, object>; if (k == null) continue;
                    var s = new Dictionary<string, object>();
                    foreach (var f in new[] { "icon", "label", "color" }) { object v; if (k.TryGetValue(f, out v)) s[f] = v; }
                    keys.Add(s);
                }
            }
            o["keys"] = keys;
            string body = json.Serialize(o);
            List<string> list;
            lock (trusted) list = new List<string>(trusted.Keys);
            foreach (var d in list) if (dev == null || d == dev) ToSealed(d, body);
        }

        Dictionary<string, object> AppKey(int i)
        {
            var rule = appRule; if (rule == null) return null;
            object ks; rule.TryGetValue("keys", out ks); var arr = ks as object[];
            if (arr == null || i < 0 || i >= arr.Length) return null;
            return arr[i] as Dictionary<string, object>;
        }

        void RunKey(string from, Dictionary<string, object> k)
        {
            if (k == null) return;
            // 동작 순서표가 있으면 차례로(Steps.cs). 기다리기가 있어 따로 돈다.
            if (k.ContainsKey("steps"))
            {
                Steps.Run(k, delegate (string err) { ToSealed(from, json.Serialize(new Dictionary<string, object> { { "t", "ok" }, { "msg", err } })); });
                return;
            }
            try
            {
                string web = Str(k, "web"), app = Str(k, "app"), key = Str(k, "key");
                Input.Wake();
                if (web != null) OpenUrl(web);
                else if (app != null) Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(app)) { UseShellExecute = true });
                else if (key != null) Input.Combo(key);
            }
            catch
            {
                ToSealed(from, "{\"t\":\"ok\",\"msg\":\"실행하지 못했습니다\"}");
            }
        }

        void Gesture(string g, int n)
        {
            if (n < 1) n = 1; if (n > 10) n = 10;
            switch (g)
            {
                case "volup": Input.Repeat(Input.VK_VOLUME_UP, n * 2); break;
                case "voldown": Input.Repeat(Input.VK_VOLUME_DOWN, n * 2); break;
                case "mute": Input.Repeat(Input.VK_VOLUME_MUTE, 1); break;
                case "undo": Input.Combo("ctrl+Z"); break;
                case "redo": Input.Combo("ctrl+Y"); break;
                case "zoomin": Input.Combo("win+="); break;
                case "zoomout": Input.Combo("win+-"); break;
                case "switch": Input.Combo("alt+Tab"); break;
                case "desktop": Input.Combo("win+D"); break;
                case "next": Input.Combo("Right"); break;
                case "prev": Input.Combo("Left"); break;
                case "pgdn": Input.Combo("PageDown"); break;
                case "pgup": Input.Combo("PageUp"); break;
            }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }
        static long Long(Dictionary<string, object> d, string k) { object v; long r; return d.TryGetValue(k, out v) && v != null && long.TryParse(v.ToString(), out r) ? r : 0; }
        static int Int(Dictionary<string, object> d, string k) { object v; int r; return d.TryGetValue(k, out v) && v != null && int.TryParse(v.ToString(), out r) ? r : 0; }
        static bool FixedEq(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int x = 0; for (int i = 0; i < a.Length; i++) x |= a[i] ^ b[i]; return x == 0;
        }
    }

    // ---- 설정·덱 파일 ----
    class Store
    {
        public readonly string Dir, DeckPath, ConfPath;
        public string Room, Key;
        public bool FirstRun;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        object[] pagesCache; DateTime deckTime;

        public Store()
        {
            Dir = Path.Combine(Home.ExeDir, "설정");
            Directory.CreateDirectory(Dir);
            DeckPath = Path.Combine(Dir, "단추.json");
            ConfPath = Path.Combine(Dir, "연결.json");
            if (!File.Exists(DeckPath))
            {
                using (var s = typeof(Store).Assembly.GetManifestResourceStream("deck.json"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    File.WriteAllText(DeckPath, r.ReadToEnd(), new UTF8Encoding(false));
            }
            try
            {
                var c = json.DeserializeObject(File.ReadAllText(ConfPath, Encoding.UTF8)) as Dictionary<string, object>;
                Room = c["room"].ToString(); Key = c["key"].ToString();
                object h, iv;
                if (c.TryGetValue("host", out h) && h != null) Host = h.ToString();
                if (c.TryGetValue("inv", out iv) && iv != null) Inv = iv.ToString();
            }
            catch
            {
                FirstRun = true; Room = Rand(18); Key = Rand(18); Save();
            }
        }

        public void NewKey() { Key = Rand(18); Save(); }
        public string Host, Inv;
        public void SetInvite(string host, string inv) { Host = host; Inv = inv; Save(); }

        void Save()
        {
            var d = new Dictionary<string, object>(); d["room"] = Room; d["key"] = Key;
            if (Host != null) d["host"] = Host;
            if (Inv != null) d["inv"] = Inv;
            File.WriteAllText(ConfPath, json.Serialize(d), new UTF8Encoding(false));
        }

        public static string Rand(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        Dictionary<string, object> LoadDeck()
        {
            try { return json.DeserializeObject(File.ReadAllText(DeckPath, Encoding.UTF8)) as Dictionary<string, object>; }
            catch { return null; }
        }

        // 폰에는 실행 정보(주소·경로·단축키)를 빼고 보인다
        public string ReadDeckForPhone()
        {
            var d = LoadDeck();
            if (d == null) return null;
            object pages; if (!d.TryGetValue("pages", out pages)) return null;
            pagesCache = pages as object[]; deckTime = DateTime.Now;
            var outPages = new List<object>();
            foreach (var po in pagesCache ?? new object[0])
            {
                var p = po as Dictionary<string, object>; if (p == null) continue;
                var keys = new List<object>();
                object ks; p.TryGetValue("keys", out ks);
                foreach (var ko in (ks as object[]) ?? new object[0])
                {
                    var k = ko as Dictionary<string, object>; if (k == null) continue;
                    var s = new Dictionary<string, object>();
                    foreach (var f in new[] { "icon", "label", "cat", "color" }) { object v; if (k.TryGetValue(f, out v)) s[f] = v; }
                    keys.Add(s);
                }
                var np = new Dictionary<string, object>(); object nm; p.TryGetValue("name", out nm); np["name"] = nm; np["keys"] = keys;
                outPages.Add(np);
            }
            var o = new Dictionary<string, object>();
            object colors; if (d.TryGetValue("colors", out colors)) o["colors"] = colors;
            o["pages"] = outPages;
            return json.Serialize(o);
        }

        // 앱 단추: 단추.json 의 "apps" 가 있으면 그것, 없으면 기본 목록
        object[] defaultApps;
        public object[] Apps()
        {
            var d = LoadDeck(); object a;
            if (d != null && d.TryGetValue("apps", out a) && a is object[]) return (object[])a;
            if (defaultApps == null)
            {
                using (var s = typeof(Store).Assembly.GetManifestResourceStream("apps.json"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    defaultApps = (json.DeserializeObject(r.ReadToEnd()) as object[]) ?? new object[0];
            }
            return defaultApps;
        }

        public Dictionary<string, object> GetKey(int p, int i)
        {
            var d = LoadDeck(); if (d == null) return null;
            object pages; if (!d.TryGetValue("pages", out pages)) return null;
            var arr = pages as object[]; if (arr == null || p < 0 || p >= arr.Length) return null;
            var pg = arr[p] as Dictionary<string, object>; if (pg == null) return null;
            object ks; pg.TryGetValue("keys", out ks); var karr = ks as object[];
            if (karr == null || i < 0 || i >= karr.Length) return null;
            return karr[i] as Dictionary<string, object>;
        }
    }

    // ---- 암호화: 폰(app.js)과 같은 방식 ----
    // K → HMAC-SHA256(K,"enc") = AES-256 열쇠, HMAC-SHA256(K,"mac") = 확인 열쇠.
    // 모양: "E" + base64(iv 16 | AES-CBC 암호문 | HMAC(iv|암호문) 32)
    // ---- 출결·메모·시간표: 리모컨 폴더 안 CSV ----
    // 명렬표\<반>.csv (번호,이름) · 시간표\시간표.csv (교시,시작,끝,월,화,수,목,금)
    // 출결\yyyy-MM\<반>.csv (날짜,교시,번호,이름,상태) · 메모\yyyy-MM\<반>.csv (날짜,시각,교시,번호,이름,메모)
    // 학생 정보는 이 폴더에만 둔다. 폰에는 저장하지 않는다.
    static class Rec
    {
        static string Root { get { return Home.ExeDir; } }
        static readonly object fileLock = new object();
        static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        public static void EnsureTemplates()
        {
            try
            {
                string md = Path.Combine(Root, "명렬표");
                if (!Directory.Exists(md))
                {
                    Directory.CreateDirectory(md);
                    File.WriteAllText(Path.Combine(md, "_보기.csv"),
                        "번호,이름\r\n1,학생1\r\n2,학생2\r\n3,학생3\r\n", Utf8Bom);
                }
                string td = Path.Combine(Root, "시간표");
                if (!Directory.Exists(td))
                {
                    Directory.CreateDirectory(td);
                    File.WriteAllText(Path.Combine(td, "시간표.csv"),
                        "교시,시작,끝,월,화,수,목,금\r\n1,08:50,09:40,,,,,\r\n2,09:50,10:40,,,,,\r\n3,10:50,11:40,,,,,\r\n4,11:50,12:40,,,,,\r\n" +
                        "5,13:30,14:20,,,,,\r\n6,14:30,15:20,,,,,\r\n7,15:30,16:20,,,,,\r\n", Utf8Bom);
                }
            }
            catch { }
        }

        // 엑셀이 저장한 CSV(대개 CP949)와 UTF-8 둘 다 읽는다
        static List<string[]> ReadCsv(string path)
        {
            var rows = new List<string[]>();
            if (!File.Exists(path)) return rows;
            byte[] b;
            try { b = File.ReadAllBytes(path); } catch { return rows; }
            string text;
            try { text = new UTF8Encoding(false, true).GetString(b); }
            catch { text = Encoding.GetEncoding(949).GetString(b); }
            text = text.TrimStart('\uFEFF');
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim() == "") continue;
                rows.Add(SplitCsv(raw, raw.IndexOf('\t') >= 0 && raw.IndexOf(',') < 0 ? '\t' : ','));
            }
            return rows;
        }

        static string[] SplitCsv(string line, char sep)
        {
            var o = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
                else if (c == '"') q = true;
                else if (c == sep) { o.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            o.Add(sb.ToString().Trim());
            return o.ToArray();
        }

        // 엑셀 수식으로 읽히지 않게
        static string Cell(string v)
        {
            v = (v ?? "").Replace("\r", " ").Replace("\n", " ");
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            if (v.IndexOfAny(new[] { ',', '"' }) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        public static List<string> Classes()
        {
            var o = new List<string>();
            string md = Path.Combine(Root, "명렬표");
            if (!Directory.Exists(md)) return o;
            foreach (var f in Directory.GetFiles(md, "*.csv"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (!n.StartsWith("_")) o.Add(n);
            }
            o.Sort(StringComparer.Ordinal);
            return o;
        }

        static bool KnownClass(string cls) { return cls != null && Classes().Contains(cls); }

        public static Dictionary<string, object> Timetable()
        {
            var o = new Dictionary<string, object>(); o["t"] = "tt";
            var periods = new List<object>();
            var rows = ReadCsv(Path.Combine(Root, "시간표", "시간표.csv"));
            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i]; if (r.Length < 3 || r[0] == "") continue;
                var days = new List<object>();
                for (int d = 3; d < 8; d++) days.Add(d < r.Length ? r[d] : "");
                periods.Add(new Dictionary<string, object> { { "p", r[0] }, { "s", r[1] }, { "e", r[2] }, { "d", days } });
            }
            o["periods"] = periods;
            o["classes"] = Classes();
            return o;
        }

        public static Dictionary<string, object> Roster(string cls, string p)
        {
            var o = new Dictionary<string, object>(); o["t"] = "roster"; o["cls"] = cls; o["p"] = p;
            var list = new List<object>();
            if (KnownClass(cls))
            {
                foreach (var r in ReadCsv(Path.Combine(Root, "명렬표", cls + ".csv")))
                {
                    int no; if (r.Length < 1 || !int.TryParse(r[0], out no)) continue;   // 머리줄 건너뜀
                    list.Add(new object[] { no, r.Length > 1 ? r[1] : "" });
                }
                // 오늘 이 교시에 저장한 출결
                var marks = new Dictionary<string, object>();
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                foreach (var r in ReadCsv(AttPath(cls)))
                    if (r.Length >= 5 && r[0] == today && r[1] == (p ?? "") && r[2] != "") marks[r[2]] = r[4];
                o["marks"] = marks;
            }
            o["list"] = list;
            return o;
        }

        static string AttPath(string cls) { return Path.Combine(Root, "출결", DateTime.Now.ToString("yyyy-MM"), cls + ".csv"); }
        static string MemoPath(string cls) { return Path.Combine(Root, "메모", DateTime.Now.ToString("yyyy-MM"), cls + ".csv"); }

        static Dictionary<string, string> Names(string cls)
        {
            var d = new Dictionary<string, string>();
            foreach (var r in ReadCsv(Path.Combine(Root, "명렬표", cls + ".csv")))
            { int no; if (r.Length >= 1 && int.TryParse(r[0], out no)) d[no.ToString()] = r.Length > 1 ? r[1] : ""; }
            return d;
        }

        // 같은 날·같은 교시로 다시 저장하면 그 줄들을 바꿔 쓴다
        public static string SaveAtt(string cls, string p, Dictionary<string, object> marks)
        {
            if (!KnownClass(cls)) return "명렬표에 없는 반입니다";
            p = p ?? "";
            string path = AttPath(cls), today = DateTime.Now.ToString("yyyy-MM-dd");
            var names = Names(cls);
            lock (fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var keep = new List<string>();
                    var rows = ReadCsv(path);
                    if (rows.Count == 0) keep.Add("날짜,교시,번호,이름,상태");
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        if (i > 0 && r.Length >= 2 && r[0] == today && r[1] == p) continue;
                        var cells = new List<string>(); foreach (var c in r) cells.Add(Cell(c));
                        keep.Add(string.Join(",", cells));
                    }
                    var nos = new List<int>();
                    if (marks != null) foreach (var k in marks.Keys) { int n; if (int.TryParse(k, out n)) nos.Add(n); }
                    nos.Sort();
                    if (nos.Count == 0) keep.Add(string.Join(",", new[] { today, Cell(p), "", "", "모두 출석" }));
                    foreach (var n in nos)
                    {
                        string st = marks[n.ToString()] == null ? "" : marks[n.ToString()].ToString();
                        if (st != "지각" && st != "결석" && st != "조퇴") continue;
                        string nm; names.TryGetValue(n.ToString(), out nm);
                        keep.Add(string.Join(",", new[] { today, Cell(p), n.ToString(), Cell(nm), st }));
                    }
                    File.WriteAllText(path, string.Join("\r\n", keep) + "\r\n", Utf8Bom);
                    return null;
                }
                catch (IOException) { return "파일이 열려 있어 저장하지 못했습니다. 엑셀을 닫아 주세요"; }
                catch { return "저장하지 못했습니다"; }
            }
        }

        public static string SaveMemo(string cls, string p, string no, string text)
        {
            if (!KnownClass(cls)) return "명렬표에 없는 반입니다";
            if (string.IsNullOrWhiteSpace(text)) return "메모가 비어 있습니다";
            if (text.Length > 500) text = text.Substring(0, 500);
            string path = MemoPath(cls);
            string nm; Names(cls).TryGetValue(no ?? "", out nm);
            lock (fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool fresh = !File.Exists(path);
                    using (var w = new StreamWriter(path, true, Utf8Bom))
                    {
                        if (fresh) w.Write("날짜,시각,교시,번호,이름,메모\r\n");
                        var now = DateTime.Now;
                        w.Write(string.Join(",", new[] { now.ToString("yyyy-MM-dd"), now.ToString("HH:mm"), Cell(p), Cell(no), Cell(nm), Cell(text) }) + "\r\n");
                    }
                    return null;
                }
                catch (IOException) { return "파일이 열려 있어 저장하지 못했습니다. 엑셀을 닫아 주세요"; }
                catch { return "저장하지 못했습니다"; }
            }
        }
    }

    class Seal
    {
        readonly byte[] enc, mac;
        public Seal(string k)
        {
            string b = k.Replace('-', '+').Replace('_', '/');
            while (b.Length % 4 != 0) b += "=";
            byte[] raw = Convert.FromBase64String(b);
            enc = Hmac(raw, Encoding.ASCII.GetBytes("enc"));
            mac = Hmac(raw, Encoding.ASCII.GetBytes("mac"));
        }
        static byte[] Hmac(byte[] k, byte[] d) { using (var h = new HMACSHA256(k)) return h.ComputeHash(d); }

        public string Close(string text)
        {
            var iv = new byte[16];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(iv);
            byte[] ct;
            using (var aes = new AesCryptoServiceProvider { Key = enc, IV = iv, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
            using (var e = aes.CreateEncryptor())
            {
                var pt = Encoding.UTF8.GetBytes(text);
                ct = e.TransformFinalBlock(pt, 0, pt.Length);
            }
            var body = new byte[16 + ct.Length];
            Buffer.BlockCopy(iv, 0, body, 0, 16); Buffer.BlockCopy(ct, 0, body, 16, ct.Length);
            var tag = Hmac(mac, body);
            var all = new byte[body.Length + 32];
            Buffer.BlockCopy(body, 0, all, 0, body.Length); Buffer.BlockCopy(tag, 0, all, body.Length, 32);
            return "E" + Convert.ToBase64String(all);
        }

        public string Open(string s)
        {
            try
            {
                if (s.Length < 2 || s[0] != 'E') return null;
                var all = Convert.FromBase64String(s.Substring(1));
                if (all.Length < 64) return null;
                int bl = all.Length - 32;
                var body = new byte[bl]; Buffer.BlockCopy(all, 0, body, 0, bl);
                var want = Hmac(mac, body);
                int x = 0; for (int i = 0; i < 32; i++) x |= want[i] ^ all[bl + i];
                if (x != 0) return null;
                var iv = new byte[16]; Buffer.BlockCopy(body, 0, iv, 0, 16);
                using (var aes = new AesCryptoServiceProvider { Key = enc, IV = iv, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
                using (var d = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(d.TransformFinalBlock(body, 16, bl - 16));
            }
            catch { return null; }
        }
    }

    // ---- 중계 연결 ----
    class Link
    {
        readonly string room;
        ClientWebSocket ws;
        public Action OnManyFails;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        CancellationTokenSource cts = new CancellationTokenSource();
        public Action<string> OnMessage;
        public Action<bool> OnState;

        public Link(string room, string inv) { this.room = room; this.inv = inv; }
        string inv;
        public int Fails;   // 잇달아 실패한 수(초대 코드가 끊겼는지 볼 때)
        public void Reconnect(string newInv) { inv = newInv; Fails = 0; try { if (ws != null) ws.Abort(); } catch { } }
        public void Start() { Task.Run(new Func<Task>(Loop)); }
        public void Stop() { cts.Cancel(); try { if (ws != null) ws.Abort(); } catch { } }

        async Task Loop()
        {
            int wait = 1000;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.Zero;   // 우리가 'p'로 직접 유지
                    await ws.ConnectAsync(new Uri("wss://" + Program.Host + "/ws/pc/" + room + "?inv=" + Uri.EscapeDataString(inv ?? "")), cts.Token);
                    Fails = 0;
                    wait = 1000;
                    if (OnState != null) OnState(true);
                    var ping = Task.Run(new Func<Task>(Ping));
                    var buf = new byte[64 * 1024];
                    var sb = new MemoryStream();
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        sb.Write(buf, 0, r.Count);
                        if (!r.EndOfMessage) continue;
                        string s = Encoding.UTF8.GetString(sb.ToArray()); sb.SetLength(0);
                        if (s == "o") continue;
                        try { if (OnMessage != null) OnMessage(s); } catch { }
                    }
                }
                catch { Fails++; if (Fails == 4 && OnManyFails != null) OnManyFails(); }
                if (OnState != null) OnState(false);
                try { ws.Abort(); } catch { }
                if (cts.IsCancellationRequested) break;
                try { await Task.Delay(wait, cts.Token); } catch { }
                wait = Math.Min(wait * 2, 30000);
            }
        }

        async Task Ping()
        {
            var me = ws;
            while (me.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                try { await Task.Delay(25000, cts.Token); } catch { return; }
                await SendAsync(me, "p");
            }
        }

        public void Send(string s) { var w = ws; if (w != null) { var t = SendAsync(w, s); } }
        // 파일 조각처럼 차례가 중요한 것: 다 보낼 때까지 기다린다
        public bool SendNow(string s)
        {
            var w = ws; if (w == null || w.State != WebSocketState.Open) return false;
            try { SendAsync(w, s).Wait(); } catch { return false; }
            return w.State == WebSocketState.Open;
        }

        async Task SendAsync(ClientWebSocket w, string s)
        {
            if (w.State != WebSocketState.Open) return;
            await sendLock.WaitAsync();
            try { await w.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, cts.Token); }
            catch { }
            finally { sendLock.Release(); }
        }
    }

    // ---- 키보드 입력 ----
    static class Input
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        const uint KEYUP = 0x2, EXT = 0x1;
        public const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

        static readonly Dictionary<string, byte> Names = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            {"ctrl",0x11},{"shift",0x10},{"alt",0x12},{"win",0x5B},
            {"Esc",0x1B},{"Enter",0x0D},{"Space",0x20},{"Tab",0x09},{"Backspace",0x08},{"Delete",0x2E},
            {"Left",0x25},{"Up",0x26},{"Right",0x27},{"Down",0x28},{"Home",0x24},{"End",0x23},{"PageUp",0x21},{"PageDown",0x22},
            {"=",0xBB},{"+",0xBB},{"-",0xBD},{".",0xBE},{",",0xBC},{"\\",0xDC},{"/",0xBF},{";",0xBA},
            {"VolumeUp",VK_VOLUME_UP},{"VolumeDown",VK_VOLUME_DOWN},{"VolumeMute",VK_VOLUME_MUTE},
            {"PlayPause",0xB3},{"NextTrack",0xB0},{"PrevTrack",0xB1},
        };
        static readonly HashSet<byte> Extended = new HashSet<byte> { 0x25, 0x26, 0x27, 0x28, 0x24, 0x23, 0x21, 0x22, 0x2E, 0x5B };

        static byte Vk(string n)
        {
            byte v;
            if (Names.TryGetValue(n, out v)) return v;
            if (n.Length == 1 && char.IsLetterOrDigit(n[0])) return (byte)char.ToUpperInvariant(n[0]);
            if (n.Length >= 2 && (n[0] == 'F' || n[0] == 'f')) { int f; if (int.TryParse(n.Substring(1), out f) && f >= 1 && f <= 24) return (byte)(0x6F + f); }
            throw new ArgumentException("모르는 키: " + n);
        }

        static void Down(byte v) { keybd_event(v, 0, Extended.Contains(v) ? EXT : 0, UIntPtr.Zero); }
        static void Up(byte v) { keybd_event(v, 0, (Extended.Contains(v) ? EXT : 0) | KEYUP, UIntPtr.Zero); }

        // 바깥 입력처럼 보이게 해 새로 여는 창이 앞으로 오게 한다
        public static void Wake() { AllowSetForegroundWindow(-1); }

        public static void Combo(string combo)
        {
            if (string.Equals(combo, "win+L", StringComparison.OrdinalIgnoreCase)) { LockWorkStation(); return; }
            var parts = combo.Split('+');
            // "win+=" → 마지막이 빈 칸이면 '+' 자체
            var keys = new List<byte>();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "" && i == parts.Length - 1) p = "+";
                if (p == "") continue;
                keys.Add(Vk(p.Trim()));
            }
            foreach (var k in keys) Down(k);
            for (int i = keys.Count - 1; i >= 0; i--) Up(keys[i]);
        }

        public static void Repeat(byte vk, int n)
        {
            for (int i = 0; i < n; i++) { Down(vk); Up(vk); }
        }
    }

    // 앞에 있는 창의 프로그램 이름·제목
    static class Fore
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        static readonly HashSet<string> Skip = new HashSet<string> {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "MultitaskingViewFrame", "ForegroundStaging", "#32768" };
        static readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        static readonly uint me = (uint)Process.GetCurrentProcess().Id;

        public static bool Get(out string exe, out string title)
        {
            exe = title = null;
            IntPtr h = GetForegroundWindow(); if (h == IntPtr.Zero) return false;
            var sb = new StringBuilder(512);
            GetClassName(h, sb, sb.Capacity);
            if (Skip.Contains(sb.ToString())) return false;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == me) return false;
            if (!names.TryGetValue(pid, out exe))
            {
                try { exe = Process.GetProcessById((int)pid).ProcessName; } catch { exe = ""; }
                if (names.Count > 200) names.Clear();
                names[pid] = exe;
            }
            sb.Clear(); GetWindowText(h, sb, sb.Capacity);
            title = sb.ToString();
            return true;
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool IsOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("StreamRemote") != null;
        }
        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue("StreamRemote", "\"" + Application.ExecutablePath + "\" --quiet");
                else k.DeleteValue("StreamRemote", false);
            }
        }
    }
}
