// 교실 리모컨 — 사진·파일 주고받기.
// 폰 → PC: 사진은 리모컨\사진\yyyy-MM-dd, 그 밖은 리모컨\받은 파일. 조각(base64)으로 받아 .part 에 쓰고 끝나면 이름을 붙인다.
// PC → 폰: 리모컨\폰으로 폴더의 파일을 조각으로 보낸다.
// 조각은 모두 암호화되어 중계를 지나가고, 중계는 저장하지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace StreamRemote
{
    static class Files
    {
        public const long MaxSize = 100L * 1024 * 1024;   // 한 파일 100MB
        public const int Chunk = 256 * 1024;              // 조각 하나(원본 바이트)

        static string Root { get { return Home.ExeDir; } }
        public static string OutDir { get { return Path.Combine(Root, "폰으로"); } }

        class Up
        {
            public string Name, Kind, Tmp; public long Size, Got; public int N, Next;
            public FileStream Fs; public DateTime T;
        }
        static readonly Dictionary<string, Up> ups = new Dictionary<string, Up>();
        static readonly object gate = new object();

        public static void EnsureDirs()
        {
            try { Directory.CreateDirectory(OutDir); } catch { }
        }

        static string SafeName(string n)
        {
            n = Path.GetFileName(n ?? "");
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimStart('.');
            if (n.Length == 0) n = "파일";
            if (n.Length > 120) n = n.Substring(0, 80) + Path.GetExtension(n);
            return n;
        }

        static string Unique(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            if (!File.Exists(p)) return p;
            string b = Path.GetFileNameWithoutExtension(name), e = Path.GetExtension(name);
            for (int i = 1; i < 1000; i++) { p = Path.Combine(dir, b + " (" + i + ")" + e); if (!File.Exists(p)) return p; }
            return Path.Combine(dir, b + " " + DateTime.Now.Ticks + e);
        }

        static void Sweep()
        {
            // 10분 넘게 멈춘 받기는 버린다
            var dead = new List<string>();
            foreach (var kv in ups) if ((DateTime.Now - kv.Value.T).TotalMinutes > 10) dead.Add(kv.Key);
            foreach (var k in dead) Drop(k);
        }
        static void Drop(string k)
        {
            Up u; if (!ups.TryGetValue(k, out u)) return;
            try { u.Fs.Dispose(); } catch { }
            try { File.Delete(u.Tmp); } catch { }
            ups.Remove(k);
        }

        // ---- 폰 → PC ----
        public static string Begin(string from, string id, string name, long size, int n, string kind)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40) return "잘못된 요청입니다";
            if (size <= 0 || size > MaxSize) return "100MB까지 보낼 수 있습니다";
            if (n != (int)((size + Chunk - 1) / Chunk)) return "잘못된 요청입니다";
            lock (gate)
            {
                Sweep();
                if (ups.Count >= 8) return "잠시 뒤에 다시 보내 주세요";
                string dir = kind == "photo"
                    ? Path.Combine(Root, "사진", DateTime.Now.ToString("yyyy-MM-dd"))
                    : Path.Combine(Root, "받은 파일");
                try
                {
                    Directory.CreateDirectory(dir);
                    var u = new Up { Name = SafeName(name), Kind = kind, Size = size, N = n, T = DateTime.Now };
                    u.Tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".part");
                    u.Fs = new FileStream(u.Tmp, FileMode.CreateNew, FileAccess.Write);
                    Drop(from + "/" + id);
                    ups[from + "/" + id] = u;
                    return null;
                }
                catch { return "저장할 곳을 만들지 못했습니다"; }
            }
        }

        public static string Piece(string from, string id, int i, string b64)
        {
            lock (gate)
            {
                Up u; if (!ups.TryGetValue(from + "/" + id, out u)) return "보내기가 끊겼습니다";
                if (i != u.Next) { Drop(from + "/" + id); return "조각 차례가 맞지 않습니다"; }
                byte[] b;
                try { b = Convert.FromBase64String(b64 ?? ""); } catch { Drop(from + "/" + id); return "잘못된 조각입니다"; }
                if (u.Got + b.Length > u.Size) { Drop(from + "/" + id); return "크기가 맞지 않습니다"; }
                try { u.Fs.Write(b, 0, b.Length); } catch { Drop(from + "/" + id); return "저장하지 못했습니다"; }
                u.Got += b.Length; u.Next++; u.T = DateTime.Now;
                return null;
            }
        }

        // 끝: 성공하면 저장한 경로, 실패하면 null 과 err
        public static string End(string from, string id, out string err)
        {
            err = null;
            lock (gate)
            {
                Up u; if (!ups.TryGetValue(from + "/" + id, out u)) { err = "보내기가 끊겼습니다"; return null; }
                try { u.Fs.Dispose(); } catch { }
                if (u.Got != u.Size || u.Next != u.N) { Drop(from + "/" + id); err = "파일이 다 오지 않았습니다"; return null; }
                string dir = Path.GetDirectoryName(u.Tmp);
                string final = Unique(dir, u.Name);
                try { File.Move(u.Tmp, final); }
                catch { Drop(from + "/" + id); err = "저장하지 못했습니다"; return null; }
                ups.Remove(from + "/" + id);
                return final;
            }
        }

        // ---- PC → 폰 ----
        public static Dictionary<string, object> List()
        {
            var o = new Dictionary<string, object>(); o["t"] = "flist";
            var list = new List<object>();
            try
            {
                EnsureDirs();
                var fs = new DirectoryInfo(OutDir).GetFiles();
                Array.Sort(fs, delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                foreach (var f in fs)
                {
                    if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (f.Name.StartsWith(".") || f.Name.StartsWith("~$")) continue;
                    list.Add(new Dictionary<string, object> { { "name", f.Name }, { "size", f.Length }, { "time", f.LastWriteTime.ToString("MM-dd HH:mm") } });
                    if (list.Count >= 50) break;
                }
            }
            catch { }
            o["files"] = list;
            return o;
        }

        // 보낼 조각 메시지를 차례로 만들어 send 로 넘긴다(따로 도는 스레드에서 부를 것)
        public static string SendFile(string name, Func<Dictionary<string, object>, bool> send)
        {
            string path = Path.Combine(OutDir, Path.GetFileName(name ?? ""));
            if (!File.Exists(path) || Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(OutDir).TrimEnd('\\')) return "파일이 없습니다";
            long size = new FileInfo(path).Length;
            if (size > MaxSize) return "100MB까지 보낼 수 있습니다";
            int n = (int)Math.Max(1, (size + Chunk - 1) / Chunk);
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[Chunk];
                    for (int i = 0; i < n; i++)
                    {
                        int got = 0, r;
                        while (got < buf.Length && (r = fs.Read(buf, got, buf.Length - got)) > 0) got += r;
                        var m = new Dictionary<string, object> {
                            { "t", "fdata" }, { "id", id }, { "name", Path.GetFileName(path) }, { "size", size },
                            { "i", i }, { "n", n }, { "d", Convert.ToBase64String(buf, 0, got) } };
                        if (!send(m)) return "연결이 끊겼습니다";
                        Thread.Sleep(30);
                    }
                }
                return null;
            }
            catch (IOException) { return "파일이 열려 있어 보내지 못했습니다"; }
            catch { return "보내지 못했습니다"; }
        }
    }
}
