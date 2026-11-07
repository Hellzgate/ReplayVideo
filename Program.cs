using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Text.Json;
using WindBotAIEditor.Services;
using ReplayVideoCore;

namespace ReplayVideo
{
    /// <summary>
    /// 리플레이(.yrpX) → 보드 영상(MP4).
    ///   리플레이에 든 MSG_* 스트림으로 상태를 재구성하고, 이동은 보간 애니메이션으로 그린다.
    ///   사용: ReplayVideo &lt;replay.yrpX&gt; &lt;out.mp4&gt; [--pics DIR] [--fps 8] [--cdb FILE] [--frames DIR] [--layout portrait] [--img-api] [--no-api] [--no-translate] [--dump]
    /// </summary>
    public static class Program
    {
        // 필드 존
        private const int LOC_DECK = 0x01, LOC_HAND = 0x02, LOC_MZONE = 0x04, LOC_SZONE = 0x08;
        private const int LOC_GRAVE = 0x10, LOC_REMOVED = 0x20, LOC_EXTRA = 0x40;

        // 위치(position) 비트: 0x1 공격표시, 0x2 뒷면공격, 0x4 수비표시, 0x8 뒷면수비
        private const int POS_FACEUP = 0x5;

        // 레이아웃 (가로/세로 전환 가능)
        private static int W = 1400, H = 820;
        private static int CardW = 86, CardH = 123;
        private const int Gap = 8;
        private static int FieldX = 176;
        private static int OppSzY = 76, OppMzY = 76 + 123 + 6;
        private static int MyMzY = 468, MySzY = 468 + 123 + 6;
        private static bool Portrait;

        private static void SetupLayout(bool portrait)
        {
            Portrait = portrait;
            if (portrait)
            {
                W = 1080; H = 1920;
                CardW = 190; CardH = 273;
                FieldX = 49;                        // 5*190 + 4*8 = 982 → (1080-982)/2
                OppSzY = 96; OppMzY = OppSzY + CardH + 8;
                MyMzY = 770; MySzY = MyMzY + CardH + 8;
            }
            else
            {
                W = 1400; H = 820;
                CardW = 86; CardH = 123;
                FieldX = 176;
                OppSzY = 76; OppMzY = OppSzY + CardH + 6;
                MyMzY = 468; MySzY = MyMzY + CardH + 6;
            }

            // 공용 Sim(ReplayVideoCore.Layout)과 좌표 동기화 — 이게 없으면 카드가 엉뚱한 곳에 그려진다
            Layout.W = W; Layout.H = H;
            Layout.CardW = CardW; Layout.CardH = CardH; Layout.Gap = Gap;
            Layout.FieldX = FieldX;
            Layout.OppSzY = OppSzY; Layout.OppMzY = OppMzY;
            Layout.MyMzY = MyMzY; Layout.MySzY = MySzY;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ReplayVideo <replay.yrpX> <out.mp4> [--pics DIR] [--fps N] [--cdb FILE] [--frames DIR] [--layout portrait] [--img-api] [--no-api] [--no-translate] [--dump]");
                return 1;
            }

            var req = new RenderRequest { ReplayPath = args[0], OutPath = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pics": req.PicsDir = args[++i]; break;
                    case "--fps": req.Fps = int.Parse(args[++i]); break;
                    case "--cdb": req.CdbPath = args[++i]; break;
                    case "--frames": req.FramesDir = args[++i]; break;
                    case "--no-api": req.Api = false; break;
                    case "--img-api": req.ImageApiFirst = true; break;
                    case "--no-translate": req.Translate = false; break;
                    case "--layout": req.Portrait = (args[++i] == "portrait"); break;
                    case "--dump": req.Dump = true; break;
                }
            }
            if (args.Length > 3) req.DumpKind = args[3];

            try { Render(req, null); return 0; }
            catch (OperationCanceledException) { Console.WriteLine("  취소됨"); return 2; }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        }

        // ── GUI/CLI 공용 파이프라인 ────────────────────────────────
        public sealed class RenderRequest
        {
            public string ReplayPath;
            public string OutPath;
            public string PicsDir = @"D:\edopro\pics";
            public string CdbPath = @"D:\edopro\expansions\cards.cdb";
            public string FramesDir;
            public int Fps = 12;
            public bool Portrait;
            public bool Api = true;
            public bool Translate = true;
            public bool ImageApiFirst;
            public bool Dump;
            public string DumpKind;
        }

        // ── 저장 위치: C: 용량이 부족하므로 **D: 우선**(E: → F: → 마지막 수단 LOCALAPPDATA) ──
        //    (2026-11-06 사용자 지시: "기본 저장 경로는 항상 D드라이브")
        private static string _baseDir;

        /// <summary>기본 저장 루트. D:\ReplayVideo (없으면 E:/F:, 그다음 LOCALAPPDATA).</summary>
        public static string BaseDir()
        {
            if (!string.IsNullOrEmpty(_baseDir)) return _baseDir;
            string[] roots = { @"D:\", @"E:\", @"F:\" };
            foreach (string r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    string d = Path.Combine(r, "ReplayVideo");
                    Directory.CreateDirectory(d);
                    _baseDir = d;
                    return d;
                }
                catch { }
            }
            string fb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayVideo");
            try { Directory.CreateDirectory(fb); } catch { }
            _baseDir = fb;
            return fb;
        }

        /// <summary>캐시 하위 폴더(없으면 만든다).</summary>
        private static string CacheDir(string sub)
        {
            string d = Path.Combine(BaseDir(), "cache", sub);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        /// <summary>예전 캐시(%LOCALAPPDATA%\ReplayVideo\*)를 D: 로 1회 이동(재다운로드 방지).
        ///   Directory.Move 는 대상 폴더가 이미 있으면 실패하므로 **파일 단위**로 옮긴다.</summary>
        public static void MigrateOldCache()
        {
            try
            {
                string oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayVideo");
                string newRoot = BaseDir();
                if (!Directory.Exists(oldRoot)) return;
                if (string.Equals(Path.GetFullPath(oldRoot).TrimEnd('\\'),
                                  Path.GetFullPath(newRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

                int moved = 0;
                foreach (string sub in new[] { "cards", "imgcache", "trans" })
                {
                    string from = Path.Combine(oldRoot, sub);
                    string to = Path.Combine(newRoot, "cache", sub);
                    if (!Directory.Exists(from)) continue;
                    try { Directory.CreateDirectory(to); } catch { continue; }
                    try
                    {
                        foreach (string f in Directory.GetFiles(from))
                        {
                            string dst = Path.Combine(to, Path.GetFileName(f));
                            if (File.Exists(dst)) continue;
                            try { File.Move(f, dst); moved++; } catch { }
                        }
                    }
                    catch { }
                    try
                    {
                        if (Directory.GetFiles(from).Length == 0) Directory.Delete(from, false);
                    }
                    catch { }
                }
                if (moved > 0) Console.WriteLine(string.Format("  캐시 이동: {0}개 파일 → {1}", moved, Path.Combine(newRoot, "cache")));
            }
            catch { }
        }

        /// <summary>취소 요청(GUI). 다음 이벤트에서 중단한다.</summary>
        public static volatile bool CancelRequested;
        /// <summary>진행률 콜백 (현재 프레임, 전체, 설명).</summary>
        public static Action<int, int, string> ProgressCallback;

        /// <summary>리플레이 → MP4. GUI 는 progress 로 진행률을 받고 CancelRequested 로 중단한다.</summary>
        public static void Render(RenderRequest req, Action<int, int, string> progress)
        {
            CancelRequested = false;
            ProgressCallback = progress;
            MigrateOldCache();

            ReplayFile rep = ReplayParser.Parse(req.ReplayPath);

            if (req.Dump)
            {
                int shown = 0;
                foreach (ReplayPacket pk in rep.Packets)
                {
                    if (req.DumpKind == "START_FULL" && pk.Message == ReplayMsg.START) // START_FULL
                    {
                        var sb3 = new StringBuilder();
                        int lim = Math.Min(120, pk.Length);
                        for (int k = 0; k < lim; k++) sb3.Append(pk.Data[k].ToString("X2") + " ");
                        Console.WriteLine(string.Format("START len={0} {1}", pk.Length, sb3));
                        continue;
                    }
                    if (req.DumpKind == "DRAW_FULL" && pk.Message == ReplayMsg.DRAW) // DRAW_FULL
                    {
                        var sb2 = new StringBuilder();
                        for (int k = 0; k < pk.Length; k++) sb2.Append(pk.Data[k].ToString("X2") + " ");
                        Console.WriteLine(string.Format("DRAW len={0} {1}", pk.Length, sb2));
                        continue;
                    }
                    if (pk.Message != ReplayMsg.MOVE && pk.Message != ReplayMsg.ATTACK &&
                        pk.Message != ReplayMsg.BATTLE && pk.Message != ReplayMsg.POS_CHANGE) continue;
                    var sb = new StringBuilder();
                    for (int k = 0; k < Math.Min(34, pk.Length); k++) sb.Append(pk.Data[k].ToString("X2") + " ");
                    Console.WriteLine(string.Format("  {0,4} {1,-18} len={2,2} {3}", shown, ReplayMsg.Name(pk.Message), pk.Length, sb));
                    if (++shown >= 20) break;
                }
                return;
            }

            Console.WriteLine("FILE " + Path.GetFileName(req.ReplayPath));
            Console.WriteLine(string.Format("  players={0} · packets={1} · turns={2}",
                rep.PlayerSummary, rep.PacketCount, rep.TurnCount));

            LoadCards(req.CdbPath);
            SetupLayout(req.Portrait);
            _translate = req.Translate;
            _apiEnabled = req.Api;
            _picsDir = req.PicsDir;
            _imgApiFirst = req.ImageApiFirst;
            if (!req.Api) Console.WriteLine("  (API 모드 꺼짐 — cdb/로컬 pics 만 사용)");
            Prefetch(rep);
            if (_translate) WarmTranslations();

            var sim = new Sim(rep) { Lib = new CdbText() };
            string dir = req.FramesDir ?? Path.Combine(BaseDir(), "frames", "rv-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            int frameCount = BuildFrames(sim, rep, dir);
            Console.WriteLine(string.Format("  events={0} frames={1} · {2}", sim.Events, frameCount, sim.Summary));
            Console.WriteLine("  frames → " + dir + " (" + frameCount + ")");

            if (CancelRequested) throw new OperationCanceledException();

            try
            {
                string outDir = Path.GetDirectoryName(Path.GetFullPath(req.OutPath));
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            }
            catch { }

            string ff = FindFfmpeg();
            if (ff == null) { Console.WriteLine("  ⚠ ffmpeg 없음 → PNG 시퀀스만 생성"); return; }

            progress?.Invoke(frameCount, frameCount, "MP4 인코딩…");
            string ffArgs = string.Format(
                "-y -framerate {0} -i \"{1}\\%05d.png\" -c:v libx264 -pix_fmt yuv420p -crf 20 -movflags +faststart \"{2}\"",
                req.Fps, dir, req.OutPath);
            Console.WriteLine("  ffmpeg …");
            var psi = new ProcessStartInfo(ff, ffArgs) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string tail = err.Length > 700 ? err.Substring(err.Length - 700) : err;
                    Console.WriteLine("  ⚠ ffmpeg 실패: " + tail);
                    return;
                }
            }
            var fi = new FileInfo(req.OutPath);
            Console.WriteLine(string.Format("OUT {0}  {1:N2}MB", req.OutPath, fi.Length / 1048576.0));
            progress?.Invoke(frameCount, frameCount, "완료");
        }

        private static void LoadCards(string cdb)
        {
            try
            {
                var paths = new List<string>();
                if (File.Exists(cdb)) paths.Add(cdb);

                // EDOPro 폴더의 모든 cdb(확장/커스텀 포함)를 함께 연다 → 카드 이름/공격력 표시
                string root = Path.GetDirectoryName(Path.GetDirectoryName(cdb) ?? "") ?? "";
                if (root.Length > 0 && Directory.Exists(root))
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(root, "*.cdb", SearchOption.AllDirectories))
                            if (!paths.Contains(f)) paths.Add(f);
                    }
                    catch { }
                }
                // CardDatabase 는 64개까지만 열고 **먼저 연 cdb 가 이름을 이긴다** → 한국어 cdb 를 맨 앞에
                // (List.Sort 는 불안정 정렬이라 같은 순위의 기존 순서가 뒤집힌다 → 안정 정렬로 재구성)
                var ordered = new List<string>();
                for (int r = 0; r <= 2; r++)
                    foreach (string f in paths) if (Rank(f) == r) ordered.Add(f);
                paths = ordered;
                if (paths.Count > 0)
                {
                    CardDatabase.Instance.OpenMany(paths);
                    Console.WriteLine(string.Format("  cdb={0}", paths.Count));
                }
            }
            catch { }
        }

        /// <summary>cdb 우선순위: 한국어 번역 cdb 를 **먼저** (CardDatabase 는 64개까지만 열고, 먼저 연 cdb 가 이긴다).</summary>
        private static int Rank(string path)
        {
            string p = path.ToLowerInvariant();
            bool ko = p.Contains("한국") || p.Contains("korean");
            if (!ko) return 2;
            return p.Contains("languages") ? 0 : 1;
        }

        private static readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> _atkCache = new Dictionary<int, int>();

        // ── 카드 정보: cdb 우선, 없으면 외부 API(YGOPRODeck) ──
        private sealed class CardRec { public string Name; public int Atk; public int Def; public string Desc; public string Type; }

        private static bool _apiEnabled = true;
        private static readonly Dictionary<int, CardRec> _api = new Dictionary<int, CardRec>();
        private static readonly string _apiCacheDir = CacheDir("cards");

        private static CardRec ApiGet(int code)
        {
            if (!_apiEnabled || code <= 0) return null;
            CardRec r;
            if (_api.TryGetValue(code, out r)) return r;

            // 캐시: <ID>.ko.json / <ID>.en.json
            foreach (string lang in new[] { "ko", "en" })
            {
                string p2 = Path.Combine(_apiCacheDir, code + "." + lang + ".json");
                try
                {
                    if (File.Exists(p2))
                    {
                        r = ParseCard(File.ReadAllText(p2));
                        if (r != null && !string.IsNullOrEmpty(r.Name)) { _api[code] = r; return r; }
                    }
                }
                catch { }
            }

            // ko 우선 → 실패(404)면 언어 없이(영어)
            foreach (string lang in new[] { "ko", "" })
            {
                try
                {
                    string url = "https://db.ygoprodeck.com/api/v7/cardinfo.php?id=" + code
                                 + (lang.Length > 0 ? "&language=" + lang : "");
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Timeout = 8000;
                    req.UserAgent = "ReplayVideo/0.9";
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var sr = new StreamReader(s, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        CardRec rec = ParseCard(json);
                        if (rec != null && !string.IsNullOrEmpty(rec.Name))
                        {
                            try
                            {
                                Directory.CreateDirectory(_apiCacheDir);
                                File.WriteAllText(Path.Combine(_apiCacheDir, code + "." + (lang.Length > 0 ? lang : "en") + ".json"), json);
                            }
                            catch { }
                            _api[code] = rec;
                            return rec;
                        }
                    }
                }
                catch { }
            }

            _api[code] = r;
            return r;
        }

        /// <summary>한국어 카드 정보만 (없으면 null). 이름/설명의 '공식 한국어' 판정에 쓴다.</summary>
        private static readonly HashSet<int> _apiKoMiss = new HashSet<int>();

        private static CardRec ApiGetKo(int code)
        {
            if (!_apiEnabled || code <= 0 || _apiKoMiss.Contains(code)) return null;
            string p2 = Path.Combine(_apiCacheDir, code + ".ko.json");
            try
            {
                if (File.Exists(p2))
                {
                    CardRec rc = ParseCard(File.ReadAllText(p2));
                    if (rc != null && !string.IsNullOrEmpty(rc.Name)) return rc;
                }
            }
            catch { }
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "https://db.ygoprodeck.com/api/v7/cardinfo.php?id=" + code + "&language=ko");
                req.Timeout = 8000;
                req.UserAgent = "ReplayVideo/1.0";
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    CardRec rc = ParseCard(json);
                    if (rc != null && !string.IsNullOrEmpty(rc.Name))
                    {
                        try { Directory.CreateDirectory(_apiCacheDir); File.WriteAllText(p2, json); } catch { }
                        return rc;
                    }
                }
            }
            catch { }
            _apiKoMiss.Add(code);
            return null;
        }

        // ── 카드 이미지: 로컬 pics → 내려받은 캐시(EDOPro 없이도 그림이 나온다) ──
        private static string _picsDir;
        private static bool _imgApiFirst;      // --img-api: 내려받은 이미지(API)를 로컬 pics 보다 우선
        private static readonly string _imgDir = CacheDir("imgcache");

        /// <summary>이미지 파일 경로(로컬 pics → 캐시). 없으면 null.</summary>
        private static string FindImage(int code)
        {
            string local = null, cached = null;
            try
            {
                if (!string.IsNullOrEmpty(_picsDir))
                {
                    string p = Path.Combine(_picsDir, code + ".jpg");
                    if (File.Exists(p)) local = p;
                }
            }
            catch { }
            try
            {
                string c = Path.Combine(_imgDir, code + ".jpg");
                if (File.Exists(c)) cached = c;
            }
            catch { }
            return _imgApiFirst ? (cached ?? local) : (local ?? cached);
        }

        /// <summary>로컬에 없으면 API 이미지를 받아 캐시한다(프리페치 단계에서만 호출).</summary>
        private static void EnsureImage(int code, bool force)
        {
            if (!_apiEnabled || code <= 0) return;
            if (!force && FindImage(code) != null) return;
            try { if (File.Exists(Path.Combine(_imgDir, code + ".jpg"))) return; } catch { }
            try
            {
                Directory.CreateDirectory(_imgDir);
                string dst = Path.Combine(_imgDir, code + ".jpg");
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                    "https://images.ygoprodeck.com/images/cards/" + code + ".jpg");
                req.Timeout = 10000;
                req.UserAgent = "ReplayVideo/1.0";
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var fs = File.Create(dst)) s.CopyTo(fs);
            }
            catch { try { File.Delete(Path.Combine(_imgDir, code + ".jpg")); } catch { } }
        }

        private static CardRec ParseCard(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("data", out JsonElement data)) return null;
                    if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
                    JsonElement c = data[0];
                    var rec = new CardRec();
                    rec.Name = c.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    rec.Type = c.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                    rec.Desc = c.TryGetProperty("desc", out JsonElement d) ? d.GetString() : null;
                    rec.Atk = -1; rec.Def = -1;
                    if (c.TryGetProperty("atk", out JsonElement a) && a.ValueKind == JsonValueKind.Number) rec.Atk = a.GetInt32();
                    if (c.TryGetProperty("def", out JsonElement df) && df.ValueKind == JsonValueKind.Number) rec.Def = df.GetInt32();
                    return rec;
                }
            }
            catch { return null; }
        }

        /// <summary>리플레이에 나오는 카드 코드를 미리 모아 API 를 데운다(4 병렬).</summary>
        private static void Prefetch(ReplayFile rep)
        {
            if (!_apiEnabled) return;
            var codes = new List<int>();
            var seen = new HashSet<int>();
            var hiCodes = new HashSet<int>();
            foreach (ReplayPacket p in rep.Packets)
            {
                int code = 0;
                switch (p.Message)
                {
                    case ReplayMsg.MOVE:
                    case ReplayMsg.SUMMONING:
                    case ReplayMsg.SPSUMMONING:
                    case ReplayMsg.SET:
                    case ReplayMsg.CHAINING:
                    case ReplayMsg.ATTACK:
                    case ReplayMsg.POS_CHANGE:
                        code = p.Length >= 4 ? (int)p.U32(0) : 0; break;
                    case ReplayMsg.DRAW:
                        {
                            int cnt = p.U8(1), off = 5;
                            for (int k = 0; k < cnt && off + 4 <= p.Length; k++, off += 8)
                            {
                                int cc = (int)p.U32(off);
                                if (cc > 0 && seen.Add(cc)) codes.Add(cc);
                            }
                            continue;
                        }
                }
                if (code > 0 && seen.Add(code)) codes.Add(code);
                if (code > 0 && p.Message == ReplayMsg.CHAINING) hiCodes.Add(code);   // 컷인용(고해상도)
            }

            int total = codes.Count;
            Console.WriteLine("  카드 정보 조회 대상 " + total + "종…");
            int next = -1, done = 0;
            var tasks = new List<System.Threading.Tasks.Task>();
            for (int t = 0; t < 4; t++)
                tasks.Add(System.Threading.Tasks.Task.Run(delegate
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next);
                        if (i >= total) break;
                        ApiGet(codes[i]);
                        EnsureImage(codes[i], _imgApiFirst || hiCodes.Contains(codes[i]));
                        System.Threading.Interlocked.Increment(ref done);
                    }
                }));
            try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray()); } catch { }
            int hit = 0;
            foreach (int cd in codes) { CardRec rr; if (_api.TryGetValue(cd, out rr) && rr != null) hit++; }
            Console.WriteLine(string.Format("  카드 정보 {0}/{1} 확보 (캐시 {2})", hit, total, _apiCacheDir));
        }

        // ── 번역(효과 텍스트) ───────────────────────────────
        private static bool _translate = true;
        private static readonly Dictionary<int, string> _transCache = new Dictionary<int, string>();
        private static readonly string _transDir = CacheDir("trans");

        private static bool HasHangul(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s) if (ch >= 0xAC00 && ch <= 0xD7A3) return true;
            return false;
        }

        private static string Sha1(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>카드 설명의 한국어 번역(없으면 null). 디스크 캐시: %LOCALAPPDATA%\ReplayVideo\trans\&lt;sha1&gt;.txt</summary>
        private static string TranslateCard(int code)
        {
            if (!_translate || code <= 0) return null;
            string v;
            if (_transCache.TryGetValue(code, out v)) return v;

            string desc = DescOf(code);
            if (string.IsNullOrEmpty(desc)) { _transCache[code] = null; return null; }
            if (HasHangul(desc)) { _transCache[code] = desc; return desc; }

            string key = Sha1(desc);
            string path = Path.Combine(_transDir, key + ".txt");
            try { if (File.Exists(path)) { v = File.ReadAllText(path); _transCache[code] = v; return v; } } catch { }

            try
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ko&dt=t&q="
                             + Uri.EscapeDataString(desc);
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 8000;
                req.UserAgent = "ReplayVideo/0.8";
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    var sb = new StringBuilder();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                        {
                            JsonElement seg = root[0];
                            if (seg.ValueKind == JsonValueKind.Array)
                                foreach (JsonElement line in seg.EnumerateArray())
                                    if (line.ValueKind == JsonValueKind.Array && line.GetArrayLength() > 0 &&
                                        line[0].ValueKind == JsonValueKind.String)
                                        sb.Append(line[0].GetString());
                        }
                    }
                    v = sb.Length > 0 ? sb.ToString() : null;
                }
            }
            catch { v = null; }

            if (!string.IsNullOrEmpty(v))
            {
                try { Directory.CreateDirectory(_transDir); File.WriteAllText(path, v); } catch { }
            }
            _transCache[code] = v;
            return v;
        }

        /// <summary>번역을 미리 데운다(2 병렬, 최대 40종).</summary>
        private static void WarmTranslations()
        {
            var codes = new List<int>();
            lock (_api) { foreach (int k in _api.Keys) codes.Add(k); }
            if (codes.Count == 0) return;
            int total = Math.Min(codes.Count, 40);
            int next = -1;
            var tasks = new List<System.Threading.Tasks.Task>();
            for (int t = 0; t < 2; t++)
                tasks.Add(System.Threading.Tasks.Task.Run(delegate
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next);
                        if (i >= total) break;
                        TranslateCard(codes[i]);
                    }
                }));
            try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray()); } catch { }
            Console.WriteLine("  효과 번역 " + total + "종 준비 (캐시 " + _transDir + ")");
        }

        private static string NameOf(int code)
        {
            string v;
            if (_nameCache.TryGetValue(code, out v)) return v;

            // 1) cdb 이름이 이미 한국어(공식 한국어 cdb) → 2) API 한국어 → 3) cdb 영문 → 4) API 영문 → 5) #ID
            string cdbName = null;
            try { cdbName = CardDatabase.Instance.NameOf(code); } catch { }
            if (!string.IsNullOrEmpty(cdbName) && HasHangul(cdbName)) { _nameCache[code] = cdbName; return cdbName; }

            CardRec ko = ApiGetKo(code);
            if (ko != null && !string.IsNullOrEmpty(ko.Name) && HasHangul(ko.Name)) { _nameCache[code] = ko.Name; return ko.Name; }

            if (!string.IsNullOrEmpty(cdbName)) { _nameCache[code] = cdbName; return cdbName; }

            CardRec r = ApiGet(code);
            v = (r != null && !string.IsNullOrEmpty(r.Name)) ? r.Name : ("#" + code);
            _nameCache[code] = v;
            return v;
        }

        private static int AtkOf(int code)
        {
            int v;
            if (_atkCache.TryGetValue(code, out v)) return v;
            try { CardDatabase.CardInfo ci = CardDatabase.Instance.Info(code); if (ci != null) { _atkCache[code] = ci.Atk; return ci.Atk; } }
            catch { }
            CardRec r = ApiGet(code);
            v = (r != null) ? r.Atk : -1;
            _atkCache[code] = v;
            return v;
        }

        private static readonly Dictionary<int, string> _descCache = new Dictionary<int, string>();
        private static string DescOf(int code)
        {
            string v;
            if (_descCache.TryGetValue(code, out v)) return v;

            // 1) cdb 설명이 한국어 → 2) API 한국어 → 3) cdb 영문 → 4) API 영문
            string cdbDesc = null;
            try
            {
                CardDatabase.CardInfo ci = CardDatabase.Instance.Info(code);
                if (ci != null && !string.IsNullOrEmpty(ci.Desc)) cdbDesc = ci.Desc;
            }
            catch { }
            if (!string.IsNullOrEmpty(cdbDesc) && HasHangul(cdbDesc)) { _descCache[code] = cdbDesc; return cdbDesc; }

            CardRec ko = ApiGetKo(code);
            if (ko != null && !string.IsNullOrEmpty(ko.Desc) && HasHangul(ko.Desc)) { _descCache[code] = ko.Desc; return ko.Desc; }

            if (!string.IsNullOrEmpty(cdbDesc)) { _descCache[code] = cdbDesc; return cdbDesc; }

            CardRec r = ApiGet(code);
            v = (r != null) ? r.Desc : null;
            _descCache[code] = v;
            return v;
        }

        private static string FindFfmpeg()
        {
            if (File.Exists(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe")) return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
            return "ffmpeg.exe";
        }


        /// <summary>Sim 에 카드 이름/설명/공격력을 공급(정적 NameOf/DescOf/AtkOf 재사용).</summary>
        private sealed class CdbText : ICardText
        {
            public string NameOf(int code) { return Program.NameOf(code); }
            public string DescOf(int code) { return Program.DescOf(code); }
            public int AtkOf(int code) { return Program.AtkOf(code); }
        }

        private static readonly Dictionary<int, Image> _img = new Dictionary<int, Image>();
        private static int _chainLog;

    public static int BuildFrames(Sim sim, ReplayFile rep, string framesDir)
        {
            int n = 0;
            foreach (ReplayPacket p in rep.Packets)
            {
                Dictionary<string, Vis> before = sim.Snapshot();     // 이벤트 직전
                if (!sim.Apply(p)) continue;
                sim.Events++;
                Emit(RenderState(sim, before, 0.45f), framesDir, n++);   // 이동 보간
                Emit(RenderState(sim, before, 0.75f), framesDir, n++);
                Emit(RenderState(sim, before, 1f), framesDir, n++);      // 최종
                if (sim.ZoomCode > 0)                                  // 발동 컷인은 조금 더 머문다
                {
                    Emit(RenderState(sim, before, 1f), framesDir, n++);
                    Emit(RenderState(sim, before, 1f), framesDir, n++);
                }
                if (sim.Caption.StartsWith("체인") && _chainLog < 5) { Console.WriteLine("  chainFrame=" + (n - 1) + " " + sim.Caption); _chainLog++; }
                if ((sim.Events & 15) == 0)
                {
                    if (CancelRequested) throw new OperationCanceledException();
                    if (ProgressCallback != null) ProgressCallback(n, 3 * rep.PacketCount, sim.Caption);
                }
                if (n > 9000) break;
            }
            if (n == 0) Emit(RenderState(sim, new Dictionary<string, Vis>(), 1f), framesDir, n++);
            return n;
        }

        // 프레임을 즉시 디스크로 (메모리 폭발 방지)
        private static void Emit(Frame f, string dir, int index)
        {
            f.Save(Path.Combine(dir, string.Format("{0:00000}.png", index)));
        }


            // 축소본 캐시 — 원본(813x1186)을 프레임마다 다시 줄이면 매우 느리다
            private static readonly Dictionary<string, Bitmap> _scaled = new Dictionary<string, Bitmap>();
            private static readonly Dictionary<int, Image> _imgHi = new Dictionary<int, Image>();

            /// <summary>컷인용 — 내려받은 고해상도(API) 이미지를 우선 쓰고, 없으면 로컬 pics.</summary>
            private static Image PicHiScaled(int code, int w, int h)
            {
                if (code <= 0 || w <= 0 || h <= 0) return null;
                Image src;
                if (!_imgHi.TryGetValue(code, out src))
                {
                    src = null;
                    try
                    {
                        string p = Path.Combine(_imgDir, code + ".jpg");
                        if (File.Exists(p)) src = Image.FromFile(p);
                    }
                    catch { src = null; }
                    if (src != null) _imgHi[code] = src;
                }
                if (src == null) return PicScaled(code, w, h);

                string key = "hi_" + code + "_" + w + "x" + h;
                Bitmap b;
                if (_scaled.TryGetValue(key, out b)) return b;
                try
                {
                    b = new Bitmap(w, h);
                    using (var sg = Graphics.FromImage(b))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        sg.DrawImage(src, 0, 0, w, h);
                    }
                    if (_scaled.Count < 600) _scaled[key] = b;
                    return b;
                }
                catch { return null; }
            }

            private static Image PicScaled(int code, int w, int h)
            {
                if (code <= 0 || w <= 0 || h <= 0) return null;
                string key = code + "_" + w + "x" + h;
                Bitmap b;
                if (_scaled.TryGetValue(key, out b)) return b;
                Image src = Pic(code);
                if (src == null) return null;
                try
                {
                    b = new Bitmap(w, h);
                    using (var sg = Graphics.FromImage(b))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        sg.DrawImage(src, 0, 0, w, h);
                    }
                    if (_scaled.Count < 600) _scaled[key] = b;
                    return b;
                }
                catch { return null; }
            }

            private static Image Pic(int code)
            {
                if (code <= 0) return null;
                Image img;
                if (_img.TryGetValue(code, out img)) return img;
                img = null;
                try
                {
                    string p = FindImage(code);
                    if (p != null) img = Image.FromFile(p);
                }
                catch { img = null; }
                // 실패(깨진 파일 등)는 캐시하지 않는다 — null 을 캐시하면 Dispose 에서 NRE
                if (img != null) _img[code] = img;
                return img;
            }

            // ── 프레임 ────────────────────────────────────
            private static Frame RenderState(Sim sim, Dictionary<string, Vis> before, float t)
            {
                Dictionary<string, Vis> cur = sim.Snapshot();

                var bmp = new Bitmap(W, H);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                        Color.FromArgb(10, 22, 40), Color.FromArgb(20, 40, 70), 90f))
                        g.FillRectangle(bg, 0, 0, W, H);

                    using (var fTitle = new Font("Malgun Gothic", 16, FontStyle.Bold))
                    using (var fCap = new Font("Malgun Gothic", 12, FontStyle.Bold))
                    using (var fLp = new Font("Malgun Gothic", 13, FontStyle.Bold))
                    using (var fSmall = new Font("Malgun Gothic", 7.5f))
                    using (var fTiny = new Font("Malgun Gothic", 6.5f))
                    using (var white = new SolidBrush(Color.FromArgb(240, 247, 255)))
                    using (var sub = new SolidBrush(Color.FromArgb(150, 175, 205)))
                    using (var acc = new SolidBrush(Color.FromArgb(120, 195, 255)))
                    using (var red = new SolidBrush(Color.FromArgb(255, 120, 120)))
                    {
                        // 헤더
                        g.DrawString(string.Format("TURN {0}   ·   {1}", sim.Turn, Sim.PhaseName(sim.Phase)), fTitle, white, 18, 10);
                        g.DrawString(sim.Caption, fCap, acc, 300, 14);
                        if (sim.Winner.Length > 0)
                        {
                            using (var wb = new SolidBrush(Color.FromArgb(255, 215, 90)))
                                g.DrawString(sim.Winner, fTitle, wb, W - 260, 10);
                        }

                        // LP
                        int lpY = 336;
                        if (Portrait)
                        {
                            using (var fLp2 = new Font("Malgun Gothic", 11, FontStyle.Bold))
                            {
                                string lpText = string.Format("상대 LP {0:N0}    내 LP {1:N0}", sim.Lp[1], sim.Lp[0]);
                                SizeF ls = g.MeasureString(lpText, fLp2);
                                g.DrawString(lpText, fLp2, sim.Lp[0] <= 2000 ? red : white, W - ls.Width - 18, 12);
                                if (sim.DmgPlayer >= 0 && sim.DmgValue > 0)
                                {
                                    using (var fBig = new Font("Malgun Gothic", 16, FontStyle.Bold))
                                        g.DrawString("-" + sim.DmgValue.ToString("N0"), fBig, red, W - 160, 40);
                                }
                            }
                        }
                        else
                        {
                            g.DrawString("상대 LP  " + sim.Lp[1].ToString("N0"), fLp, sim.Lp[1] <= 2000 ? red : white, 8, lpY);
                            g.DrawString("내 LP     " + sim.Lp[0].ToString("N0"), fLp, sim.Lp[0] <= 2000 ? red : white, 8, lpY + 36);
                            if (sim.DmgPlayer >= 0 && sim.DmgValue > 0)
                            {
                                int dy = sim.DmgPlayer == 0 ? lpY + 36 : lpY;
                                using (var fBig = new Font("Malgun Gothic", 18, FontStyle.Bold))
                                    g.DrawString("-" + sim.DmgValue.ToString("N0"), fBig, red, 8, dy + 24);
                            }
                        }

                        // 존 배경
                        DrawZoneRow(g, OppSzY, "마함");
                        DrawZoneRow(g, OppMzY, "몬스터");
                        DrawZoneRow(g, MyMzY, "몬스터");
                        DrawZoneRow(g, MySzY, "마함");

                        // 손패(양쪽) — 리플레이에는 코드가 모두 들어 있어 앞면으로 보여준다
                        if (Portrait)
                        {
                            DrawHandH(g, sim, 1, 24, 1432, fSmall, fTiny, sub, white);
                            DrawHandH(g, sim, 0, 24, 1528, fSmall, fTiny, sub, white);
                            DrawPile(g, sim, 24, 1640, "내 묘지", sim.Grave[0], fSmall, fTiny, sub, white, 66, 94, 6);
                            DrawPile(g, sim, 560, 1640, "내 제외", sim.Banished[0], fSmall, fTiny, sub, white, 66, 94, 6);
                            DrawPile(g, sim, 24, 1748, "상대 묘지", sim.Grave[1], fSmall, fTiny, sub, white, 66, 94, 6);
                            DrawPile(g, sim, 560, 1748, "상대 제외", sim.Banished[1], fSmall, fTiny, sub, white, 66, 94, 6);
                        }
                        else
                        {
                            DrawHand(g, sim, 1, 8, 78, fSmall, fTiny, sub, white);
                            DrawHand(g, sim, 0, 8, 452, fSmall, fTiny, sub, white);
                            DrawPile(g, sim, 700, OppSzY, "상대 묘지", sim.Grave[1], fSmall, fTiny, sub, white, 52, 75, 8);
                            DrawPile(g, sim, 700, OppSzY + 128, "상대 제외", sim.Banished[1], fSmall, fTiny, sub, white, 52, 75, 8);
                            DrawPile(g, sim, 700, MyMzY, "내 묘지", sim.Grave[0], fSmall, fTiny, sub, white, 52, 75, 8);
                            DrawPile(g, sim, 700, MyMzY + 128, "내 제외", sim.Banished[0], fSmall, fTiny, sub, white, 52, 75, 8);
                        }

                        // 카드 그리기 (보간)
                        sim.RebuildChainMarks();
                        var all = new HashSet<string>();
                        foreach (var k in cur.Keys) all.Add(k);
                        foreach (var k in before.Keys) all.Add(k);

                        foreach (string key in all)
                        {
                            Vis c2, b2;
                            cur.TryGetValue(key, out c2);
                            before.TryGetValue(key, out b2);
                            if (c2 == null && b2 == null) continue;

                            float x, y, alpha;
                            Vis proto = c2 ?? b2;
                            if (c2 != null && b2 != null)
                            {
                                x = b2.X + (c2.X - b2.X) * t;
                                y = b2.Y + (c2.Y - b2.Y) * t;
                                alpha = 1f;
                            }
                            else if (c2 != null) { x = c2.X; y = c2.Y; alpha = t; }          // 새로 등장
                            else { x = b2.X; y = b2.Y; alpha = 1f - t; }                       // 사라짐

                            DrawCard(g, sim, x, y, proto.Code, proto.Pos, alpha, sim.Hi.Contains(key), sim.Tg.Contains(key), fTiny, white, key, sim.Reveal.Contains(key));
                        }

                        // 체인 목록 (가운데 빈 공간)
                        if (sim.ChainCount > 0)
                        {
                            int cpx = FieldX, cpy = Portrait ? 660 : 340, cpw = 5 * (CardW + Gap);
                            using (var cb = new SolidBrush(Color.FromArgb(20, 36, 58)))
                                g.FillRectangle(cb, cpx, cpy, cpw, 14 + Math.Min(sim.ChainCount, 6) * 15);
                            using (var fCh = new Font("Malgun Gothic", 8f, FontStyle.Bold))
                            using (var fChN = new Font("Consolas", 8f, FontStyle.Bold))
                            {
                                g.DrawString("체인 " + sim.ChainCount, fCh, acc, cpx + 6, cpy + 2);
                                int cy = cpy + 18;
                                for (int k = 0; k < sim.ChainCount && k < 6; k++)
                                {
                                    Sim.Link lk = sim.ChainAt(k);
                                    string mark = lk.Negated ? "✕" : (lk.Solving ? "▶" : " ");
                                    string nm = sim.Lib.NameOf(lk.Code);
                                    string locTag = "[" + Sim.LocName(lk.Loc) + "] ";
                                    if (nm.Length > 26) nm = nm.Substring(0, 25) + "…";
                                    using (var b = new SolidBrush(lk.Negated ? Color.FromArgb(255, 120, 120)
                                        : (lk.Solving ? Color.FromArgb(120, 240, 240) : Color.FromArgb(200, 180, 240))))
                                        g.DrawString(string.Format("{0} {1}. {2}{3}", mark, lk.N, locTag, nm), fChN, b, cpx + 6, cy);
                                    cy += 15;
                                }
                            }
                        }

                        // 카드 설명(마지막 이벤트 카드)
                        string desc = sim.CurCode > 0 ? sim.Lib.DescOf(sim.CurCode) : null;
                        if (!string.IsNullOrEmpty(desc))
                        {
                            int dy = MySzY + CardH + (Portrait ? 12 : 8);
                            int dw = 5 * (CardW + Gap) + 20;
                            int dh = Portrait ? 78 : 46;
                            using (var db = new SolidBrush(Color.FromArgb(18, 32, 52)))
                                g.FillRectangle(db, FieldX, dy, dw, dh);
                            using (var fD = new Font("Malgun Gothic", 7.5f))
                            using (var bD = new SolidBrush(Color.FromArgb(185, 205, 230)))
                            {
                                string ko = TranslateCard(sim.CurCode);
                                string body = !string.IsNullOrEmpty(ko) ? ko : desc;
                                string t2 = sim.Lib.NameOf(sim.CurCode) + " — " + body.Replace("\r", " ").Replace("\n", " ");
                                if (t2.Length > 150) t2 = t2.Substring(0, 149) + "…";
                                g.DrawString(t2, fD, bD, new RectangleF(FieldX + 4, dy + 4, dw - 8, dh - 6));
                            }
                        }

                        // 하단 정보
                        g.DrawString(string.Format("패 {0} / {1}     묘지 {2} / {3}     제외 {4} / {5}     덱 {6} / {7}     엑스트라 {8} / {9}",
                            sim.Hand[0], sim.Hand[1], sim.Grave[0].Count, sim.Grave[1].Count, sim.Banished[0].Count, sim.Banished[1].Count,
                            sim.RemainDeck[0], sim.RemainDeck[1], sim.RemainExtra[0], sim.RemainExtra[1]),
                            fSmall, sub, 18, H - 26);
                        if (sim.Grave[0].Count > 0)
                            g.DrawString("내 묘지 최근: " + sim.Lib.NameOf(sim.Grave[0][sim.Grave[0].Count - 1]), fTiny, sub, 18, H - 44);
                        if (sim.Grave[1].Count > 0)
                            g.DrawString("상대 묘지 최근: " + sim.Lib.NameOf(sim.Grave[1][sim.Grave[1].Count - 1]), fTiny, sub, W - 420, H - 44);
                    }

                    // ── 발동 컷인 (맨 마지막에 그려 화면 전체를 덮는다) ──
                    if (sim.ZoomCode > 0)
                    {
                        int zw = Portrait ? 520 : 320;
                        int zh = (int)Math.Round(zw * 254.0 / 177.0);
                        int zx = (W - zw) / 2, zy = (H - zh) / 2;
                        using (var dim = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                            g.FillRectangle(dim, 0, 0, W, H);
                        Image zi = PicHiScaled(sim.ZoomCode, zw, zh);
                        if (zi != null) g.DrawImage(zi, zx, zy);
                        else
                        {
                            using (var b = new SolidBrush(Color.FromArgb(40, 60, 90))) g.FillRectangle(b, zx, zy, zw, zh);
                        }
                        using (var pen = new Pen(Color.FromArgb(255, 215, 90), 5f)) g.DrawRectangle(pen, zx, zy, zw - 1, zh - 1);
                        using (var fZ = new Font("Malgun Gothic", Portrait ? 22 : 16, FontStyle.Bold))
                        using (var bZ = new SolidBrush(Color.FromArgb(255, 235, 150)))
                        using (var bb = new SolidBrush(Color.FromArgb(200, 20, 14, 0)))
                        {
                            string t3 = "발동 !   " + sim.Lib.NameOf(sim.ZoomCode);
                            SizeF ts = g.MeasureString(t3, fZ);
                            g.FillRectangle(bb, zx - 8, zy - ts.Height - 10, zw + 16, ts.Height + 8);
                            g.DrawString(t3, fZ, bZ, zx, zy - ts.Height - 6);
                        }
                    }
                }
                return new Frame(bmp);
            }

            // 손패: 제목 + 썸네일(3열) 최대 9장 + "외 N"
            private static void DrawHand(Graphics g, Sim sim, int pl, int x, int y, Font fSmall, Font fTiny, Brush sub, Brush white)
            {
                List<int> hand = sim.HandCards[pl];
                g.DrawString(string.Format("{0} 패  {1}장", pl == 0 ? "내" : "상대", hand.Count), fSmall, sub, x, y - 18);
                int cols = 3, tw = 40, th = 57, gapx = 3, gapy = 3;
                int shown = Math.Min(hand.Count, 15);
                for (int k = 0; k < shown; k++)
                {
                    int cx = x + (k % cols) * (tw + gapx);
                    int cy = y + (k / cols) * (th + gapy);
                    DrawThumb(g, sim, cx, cy, tw, th, hand[k], fTiny, white);
                }
                if (hand.Count > shown)
                    g.DrawString("외 " + (hand.Count - shown), fTiny, sub, x, y + 5 * (th + gapy) + 2);
            }

            // 묘지/제외 더미: 제목 + 장수 + 최근 카드 5장
            // 세로 레이아웃용: 손패를 가로로
            private static void DrawHandH(Graphics g, Sim sim, int pl, int x, int y, Font fSmall, Font fTiny, Brush sub, Brush white)
            {
                List<int> hand = sim.HandCards[pl];
                g.DrawString(string.Format("{0} 패  {1}장", pl == 0 ? "내" : "상대", hand.Count), fSmall, sub, x, y - 18);
                int tw = Portrait ? 58 : 46, th = Portrait ? 83 : 66, gapx = Portrait ? 6 : 5;
                int shown = Math.Min(hand.Count, 16);
                for (int k = 0; k < shown; k++)
                    DrawThumb(g, sim, x + k * (tw + gapx), y, tw, th, hand[k], fTiny, white);
                if (hand.Count > shown)
                    g.DrawString("외 " + (hand.Count - shown), fTiny, sub, x + shown * (tw + gapx) + 2, y + th / 2);
            }
            private static void DrawPile(Graphics g, Sim sim, int x, int y, string title, List<int> pile, Font fSmall, Font fTiny, Brush sub, Brush white, int tw = 52, int th = 75, int maxShow = 8)
            {
                g.DrawString(string.Format("{0}  {1}장", title, pile.Count), fSmall, sub, x, y - 18);
                int gapx = 4;
                int shown = Math.Min(pile.Count, maxShow);
                for (int k = 0; k < shown; k++)
                {
                    int idx = pile.Count - 1 - k;      // 최근 것이 왼쪽
                    DrawThumb(g, sim, x + k * (tw + gapx), y, tw, th, pile[idx], fTiny, white);
                }
                if (pile.Count > shown)
                    g.DrawString("외 " + (pile.Count - shown), fTiny, sub, x + shown * (tw + gapx) + 2, y + th / 2);
            }

            private static void DrawThumb(Graphics g, Sim sim, int x, int y, int w, int h, int code, Font fTiny, Brush white)
            {
                Image img = PicScaled(code, w, h);
                if (img != null) g.DrawImage(img, x, y);
                else
                {
                    using (var b = new SolidBrush(Color.FromArgb(34, 52, 78))) g.FillRectangle(b, x, y, w, h);
                    g.DrawString(sim.Lib.NameOf(code), fTiny, white, new RectangleF(x + 2, y + 4, w - 4, h - 8));
                }
                using (var pen = new Pen(Color.FromArgb(70, 105, 150), 1f)) g.DrawRectangle(pen, x, y, w - 1, h - 1);
            }
            private static float ZoneX(int seq) { return FieldX + seq * (CardW + Gap); }

            private static void DrawZoneRow(Graphics g, int y, string label)
            {
                using (var pen = new Pen(Color.FromArgb(38, 60, 88), 1f))
                {
                    for (int i = 0; i < 5; i++)
                        g.DrawRectangle(pen, ZoneX(i), y, CardW - 1, CardH - 1);
                }
            }

            private static void DrawCard(Graphics g, Sim sim, float x, float y, int code, int pos, float alpha, bool hi, bool target, Font fTiny, Brush white, string key, bool reveal)
            {
                if (alpha <= 0.02f) return;
                int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                bool faceDown = (pos & 0x2) != 0 || (pos & 0x8) != 0;

                if (alpha < 1f)
                {
                    // 페이드: 알파 블렌딩 대신 밝기 완화로 근사
                    using (var b = new SolidBrush(Color.FromArgb(30, 48, 72))) g.FillRectangle(b, ix, iy, CardW, CardH);
                }
                else if (faceDown)
                {
                    using (var b = new SolidBrush(Color.FromArgb(26, 44, 74))) g.FillRectangle(b, ix, iy, CardW, CardH);
                    using (var pen = new Pen(Color.FromArgb(70, 110, 170), 1f))
                        g.DrawRectangle(pen, ix + 8, iy + 10, CardW - 17, CardH - 21);
                    using (var f = new Font("Malgun Gothic", 7f, FontStyle.Bold))
                    using (var b = new SolidBrush(Color.FromArgb(120, 165, 220)))
                        g.DrawString("SET", f, b, ix + CardW / 2 - 12, iy + CardH / 2 - 6);
                }
                else
                {
                    Image img = PicScaled(code, CardW, CardH);
                    if (img != null) g.DrawImage(img, ix, iy);
                    else
                    {
                        using (var b = new SolidBrush(Color.FromArgb(34, 52, 78))) g.FillRectangle(b, ix, iy, CardW, CardH);
                        g.DrawString(sim.Lib.NameOf(code), fTiny, white, new RectangleF(ix + 3, iy + 6, CardW - 6, CardH - 24));
                    }

                    // 이름 + ATK
                    string nm = sim.Lib.NameOf(code);
                    if (nm.Length > 22) nm = nm.Substring(0, 21) + "…";
                    using (var lb = new SolidBrush(Color.FromArgb(210, 8, 18, 30)))
                        g.FillRectangle(lb, ix, iy + CardH - 15, CardW, 15);
                    g.DrawString(nm, fTiny, white, new RectangleF(ix + 2, iy + CardH - 15, CardW - 4, 14));

                    int atk = sim.Lib.AtkOf(code);
                    if (atk >= 0)
                    {
                        string s = atk >= 0 ? atk.ToString("N0") : "";
                        using (var ab = new SolidBrush(Color.FromArgb(200, 12, 24, 40)))
                            g.FillRectangle(ab, ix + CardW - 34, iy + 2, 32, 12);
                        using (var f6 = new Font("Consolas", 6.5f, FontStyle.Bold))
                            g.DrawString(s, f6, white, ix + CardW - 32, iy + 2);
                    }
                }

                if (hi)
                    using (var pen = new Pen(Color.FromArgb(255, 120, 120), 3f)) g.DrawRectangle(pen, ix, iy, CardW - 1, CardH - 1);
                else if (target)
                    using (var pen = new Pen(Color.FromArgb(255, 190, 90), 3f)) g.DrawRectangle(pen, ix, iy, CardW - 1, CardH - 1);
                else
                    using (var pen = new Pen(Color.FromArgb(90, 130, 180), 1f)) g.DrawRectangle(pen, ix, iy, CardW - 1, CardH - 1);

                // 발동으로 뒤집힌 카드 = 금색 테두리
                if (reveal)
                {
                    using (var pen = new Pen(Color.FromArgb(255, 215, 90), 3.5f)) g.DrawRectangle(pen, ix, iy, CardW - 1, CardH - 1);
                    using (var bb = new SolidBrush(Color.FromArgb(235, 70, 50, 0)))
                        g.FillRectangle(bb, ix + CardW - 46, iy + 2, 44, 14);
                    using (var f = new Font("Malgun Gothic", 7.5f, FontStyle.Bold))
                    using (var b = new SolidBrush(Color.FromArgb(255, 235, 150)))
                        g.DrawString("발동", f, b, ix + CardW - 42, iy + 2);
                }

                // 체인 발동 표시 (보라 테두리 + C번호 / 해결 중 = 청록 / 무효 = 빨강 X)
                Sim.Link lk = sim.ChainMark(key);
                if (lk != null)
                {
                    Color bc = lk.Negated ? Color.FromArgb(255, 90, 90)
                             : lk.Solving ? Color.FromArgb(80, 230, 230)
                             : Color.FromArgb(190, 130, 255);
                    using (var pen = new Pen(bc, 3f)) g.DrawRectangle(pen, ix, iy, CardW - 1, CardH - 1);

                    using (var bb = new SolidBrush(Color.FromArgb(230, 40, 20, 60)))
                        g.FillRectangle(bb, ix + 2, iy + 2, 22, 14);
                    using (var f = new Font("Consolas", 8f, FontStyle.Bold))
                    using (var b = new SolidBrush(bc))
                        g.DrawString("C" + lk.N, f, b, ix + 3, iy + 2);

                    if (lk.Negated)
                        using (var pen = new Pen(Color.FromArgb(255, 90, 90), 3f))
                        {
                            g.DrawLine(pen, ix + 4, iy + 4, ix + CardW - 5, iy + CardH - 5);
                            g.DrawLine(pen, ix + CardW - 5, iy + 4, ix + 4, iy + CardH - 5);
                        }
                }
            }

        private sealed class Frame
        {
            private readonly Bitmap _bmp;
            public Frame(Bitmap bmp) { _bmp = bmp; }
            public void Save(string path) { _bmp.Save(path, ImageFormat.Png); _bmp.Dispose(); }
        }
    }
}
