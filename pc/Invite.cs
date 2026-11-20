// 교실 리모컨 — 초대 코드.
// 중계 서버 주소와 코드는 프로그램에 넣지 않고, 선생님이 받은 「주소/코드」 한 줄로 받는다(예: example.pages.dev/abcde-12345).
// 확인은 GET https://<주소>/preset?inv=<코드> — 맞으면 그 코드의 기본 단추 묶음(프리셋)도 함께 온다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamRemote
{
    static class Invite
    {
        public static bool Parse(string text, out string host, out string code)
        {
            host = code = null;
            if (text == null) return false;
            string t = text.Trim();
            t = Regex.Replace(t, @"^https?://", "", RegexOptions.IgnoreCase).Trim('/', ' ');
            int i = t.LastIndexOf('/');
            if (i <= 0) return false;
            host = t.Substring(0, i).Trim('/').ToLowerInvariant();
            code = t.Substring(i + 1).Trim().ToLowerInvariant();
            if (!Regex.IsMatch(host, @"^[a-z0-9.-]+\.[a-z]{2,}$")) return false;
            if (!Regex.IsMatch(code, @"^[a-z0-9-]{5,40}$")) return false;
            return true;
        }

        // 1 = 맞음, 0 = 틀림(끊긴 코드), -1 = 연결 안 됨
        public static int Check(string host, string code, out Dictionary<string, object> preset)
        {
            preset = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://" + host + "/preset?inv=" + Uri.EscapeDataString(code));
                req.Timeout = 8000; req.ReadWriteTimeout = 8000;
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    var d = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                    if (d == null) return -1;
                    object ok, p; d.TryGetValue("ok", out ok); d.TryGetValue("preset", out p);
                    preset = p as Dictionary<string, object>;
                    return true.Equals(ok) ? 1 : 0;
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 403) return 0;
                return -1;
            }
            catch { return -1; }
        }

        // 초대 코드 입력 창. 맞는 코드를 넣으면 저장하고 true.
        public static bool Ask(Store store, bool first)
        {
            using (var f = new Form())
            {
                f.Text = Program.AppName + " · 초대 코드";
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterScreen; f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(420, 150); f.Font = new Font("맑은 고딕", 10f); f.TopMost = true;
                var lb = new Label { Text = "초대 코드", Left = 18, Top = 18, AutoSize = true };
                var tb = new TextBox { Left = 18, Top = 44, Width = 384 };
                if (store.Host != null && store.Inv != null) tb.Text = store.Host + "/" + store.Inv;
                var msg = new Label { Left = 18, Top = 76, Width = 384, Height = 22, ForeColor = Color.Firebrick };
                var ok = new Button { Text = "확인", Left = 222, Top = 106, Width = 86, Height = 30 };
                var no = new Button { Text = first ? "끝내기" : "닫기", Left = 316, Top = 106, Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lb, tb, msg, ok, no });
                f.AcceptButton = ok; f.CancelButton = no;
                bool done = false;
                ok.Click += delegate
                {
                    string host, code;
                    if (!Parse(tb.Text, out host, out code)) { msg.Text = "받은 코드를 그대로 붙여 넣어 주세요."; return; }
                    msg.ForeColor = Color.DimGray; msg.Text = "확인하는 중"; f.Refresh();
                    Dictionary<string, object> preset;
                    int r = Check(host, code, out preset);
                    msg.ForeColor = Color.Firebrick;
                    if (r == 0) { msg.Text = "맞지 않거나 끊긴 초대 코드입니다."; return; }
                    if (r < 0) { msg.Text = "연결되지 않습니다. 인터넷을 확인해 주세요."; return; }
                    store.SetInvite(host, code);
                    if (preset != null)
                    {
                        bool apply = first || MessageBox.Show(f, "이 코드의 기본 단추로 바꿀까요? 지금 단추는 지워집니다.", Program.AppName,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                        if (apply)
                        {
                            try { File.WriteAllText(store.DeckPath, new JavaScriptSerializer().Serialize(preset), new UTF8Encoding(false)); } catch { }
                        }
                    }
                    done = true; f.DialogResult = DialogResult.OK; f.Close();
                };
                f.ShowDialog();
                return done;
            }
        }
    }
}
