// 교실 리모컨 — 동작 순서(단추 하나에 여러 동작).
// 단추의 "steps": [ {"do":"open"|"wait"|"waitwin"|"focus"|"key"|"type"|"click", ...} ]
// 기다리기가 있으므로 따로 돈다. 한 번에 한 순서만(돌고 있으면 새로 누른 것은 넘긴다).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamRemote
{
    static class Steps
    {
        static readonly object gate = new object();
        static bool busy;
        const int Gap = 60, MaxMs = 30000;

        // 순서를 따로 돌리고, 어느 동작이 실패했을 때만 onError 를 부른다
        public static void Run(Dictionary<string, object> key, Action<string> onError)
        {
            if (key == null) return;
            var list = List(key);
            if (list.Count == 0) return;
            lock (gate) { if (busy) return; busy = true; }
            var t = new Thread(delegate ()
            {
                string err = null;
                try
                {
                    foreach (var o in list)
                    {
                        var s = o as Dictionary<string, object>;
                        if (s == null) continue;
                        err = One(s);
                        if (err != null) break;
                        Thread.Sleep(Gap);
                    }
                }
                catch { err = "실행하지 못했습니다"; }
                finally { lock (gate) busy = false; }
                if (err != null && onError != null) { try { onError(err); } catch { } }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);   // 셸로 여는 일에 STA가 편하다
            t.Start();
        }

        static List<object> List(Dictionary<string, object> key)
        {
            object v; key.TryGetValue("steps", out v);
            var r = new List<object>();
            var e = v as IEnumerable;
            if (e == null || v is string) return r;
            foreach (var o in e) r.Add(o);
            return r;
        }

        // 옛 단추(web·app·key 하나)를 동작 순서로
        public static List<object> FromLegacy(Dictionary<string, object> key)
        {
            var r = new List<object>();
            if (key == null) return r;
            string web = Str(key, "web"), app = Str(key, "app"), k = Str(key, "key");
            if (web != null) r.Add(Step("open", "target", web));
            else if (app != null) r.Add(Step("open", "target", app));
            else if (k != null) r.Add(Step("key", "key", k));
            return r;
        }

        static Dictionary<string, object> Step(string d, string f, string v)
        {
            var s = new Dictionary<string, object>(); s["do"] = d; s[f] = v; return s;
        }

        static string One(Dictionary<string, object> s)
        {
            string d = Str(s, "do") ?? "";
            switch (d)
            {
                case "open":
                    {
                        string target = Str(s, "target");
                        if (string.IsNullOrEmpty(target)) return null;
                        try
                        {
                            Input.Wake();
                            Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target.Trim())) { UseShellExecute = true });
                        }
                        catch { return "열지 못했습니다"; }
                        return null;
                    }
                case "wait":
                    Thread.Sleep(Ms(s, 1000));
                    return null;
                case "waitwin":
                    {
                        string title = Str(s, "title");
                        if (string.IsNullOrEmpty(title)) return null;
                        int ms = Ms(s, 10000);
                        var sw = Stopwatch.StartNew();
                        while (true)
                        {
                            if (Win.Find(title) != IntPtr.Zero) return null;
                            if (sw.ElapsedMilliseconds >= ms) return "창이 뜨지 않았습니다";
                            Thread.Sleep(150);
                        }
                    }
                case "focus":
                    {
                        string title = Str(s, "title");
                        if (string.IsNullOrEmpty(title)) return null;
                        IntPtr h = Win.Find(title);
                        if (h == IntPtr.Zero) return "창을 찾지 못했습니다";
                        Win.Front(h);
                        Thread.Sleep(120);
                        return null;
                    }
                case "key":
                    {
                        string k = Str(s, "key");
                        if (string.IsNullOrEmpty(k)) return null;
                        try { Input.Combo(k); } catch { return "키를 누르지 못했습니다"; }
                        return null;
                    }
                case "type":
                    {
                        string text = Str(s, "text");
                        if (string.IsNullOrEmpty(text)) return null;
                        if (!Win.Type(text)) return "글자를 넣지 못했습니다";
                        return null;
                    }
                case "click":
                    {
                        int x = Int(s, "x", 0), y = Int(s, "y", 0);
                        string rel = Str(s, "rel") ?? "win";
                        if (rel != "screen")
                        {
                            Win.RECT r;
                            IntPtr h = Win.GetForegroundWindow();
                            if (h == IntPtr.Zero || !Win.GetWindowRect(h, out r)) return "창을 찾지 못했습니다";
                            x += r.Left; y += r.Top;
                        }
                        if (!Win.Click(x, y)) return "누르지 못했습니다";
                        return null;
                    }
            }
            return null;   // 모르는 동작은 넘긴다
        }

        static int Ms(Dictionary<string, object> s, int def)
        {
            int ms = Int(s, "ms", def);
            if (ms < 0) ms = 0; if (ms > MaxMs) ms = MaxMs;
            return ms;
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

    // 창 찾기·앞으로·글자·클릭
    static class Win
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        // type 뒤 공용체는 64비트에서 8바이트 자리에 맞춰진다(Sequential 이 IntPtr 정렬을 따른다)
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4;

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        static readonly uint me = (uint)Process.GetCurrentProcess().Id;

        // 제목에 글이 들어 있는 보이는 창(이 프로그램 창은 뺀다). 앞쪽 창부터.
        public static IntPtr Find(string part)
        {
            IntPtr found = IntPtr.Zero;
            string want = part.Trim();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                int n = GetWindowTextLength(h); if (n <= 0) return true;
                var sb = new StringBuilder(n + 1);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == me) return true;
                found = h; return false;
            }, IntPtr.Zero);
            return found;
        }

        public static void Front(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            IntPtr fg = GetForegroundWindow();
            uint dummy;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out dummy);
            uint my = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != my && AttachThreadInput(my, fgThread, true);
            // Alt 를 한 번 눌렀다 떼면 앞으로 가져오기 제한이 풀린다
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            if (attached) AttachThreadInput(my, fgThread, false);
        }

        static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            var i = new INPUT(); i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk; i.u.ki.wScan = scan; i.u.ki.dwFlags = flags;
            return i;
        }

        // 유니코드 그대로 넣기(한글 포함). 줄바꿈은 Enter, 탭은 Tab.
        public static bool Type(string text)
        {
            var list = new List<INPUT>();
            foreach (char c in text.Replace("\r\n", "\n"))
            {
                if (c == '\r') continue;
                if (c == '\n' || c == '\t')
                {
                    ushort vk = (ushort)(c == '\n' ? 0x0D : 0x09);
                    list.Add(Key(vk, 0, 0)); list.Add(Key(vk, 0, KEYEVENTF_KEYUP));
                    continue;
                }
                list.Add(Key(0, c, KEYEVENTF_UNICODE));
                list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            int size = Marshal.SizeOf(typeof(INPUT));
            // 조금씩 나눠 보내 받는 앱이 놓치지 않게
            for (int i = 0; i < list.Count; i += 40)
            {
                int n = Math.Min(40, list.Count - i);
                var part = list.GetRange(i, n).ToArray();
                if (SendInput((uint)n, part, size) != (uint)n) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        public static bool Click(int x, int y)
        {
            if (!SetCursorPos(x, y)) return false;
            Thread.Sleep(30);
            var a = new INPUT[2];
            a[0].type = INPUT_MOUSE; a[0].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            a[1].type = INPUT_MOUSE; a[1].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
            return SendInput(2, a, Marshal.SizeOf(typeof(INPUT))) == 2;
        }
    }
}
