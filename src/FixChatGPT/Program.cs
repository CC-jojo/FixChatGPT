using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace FixChatGPT
{
    /// <summary>
    /// ChatGPT 打不开 · 一键修复
    /// 针对 Clash Verge / mihomo 环境：
    ///   1) 检测重复内核 / 孤儿内核 / 控制端口被抢占
    ///   2) 切回规则模式（全局模式会绕过 ChatGPT 专用分流）
    ///   3) 自动挑出真正能连通 OpenAI 的节点
    ///   4) 校验系统代理指向
    ///   5) 真实请求校验 chatgpt.com
    /// </summary>
    internal static class Program
    {
        // ---------- 常量 ----------
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        static readonly string[] CoreNames = { "verge-mihomo", "mihomo", "clash-meta" };
        static readonly int[] MixedPortPrefer = { 7897, 7890, 7891, 1080, 10808, 7080 };
        static readonly int[] ApiPortPrefer = { 9097, 9090, 9091, 6170 };

        // ---------- 运行状态 ----------
        static int apiPort = 0;
        static int mixedPort = 0;
        static string vergeExe = null;
        static int probeTimeoutMs = 6000;
        static bool dryRun = false;
        static readonly List<string> fixes = new List<string>();
        static string logPath = "";
        static readonly StringBuilder logBuf = new StringBuilder();

        static HttpClient quick;   // 探测本机 API 是否存活（短超时）
        static HttpClient api;     // 调用本机 API
        static Dictionary<int, List<int>> portMap = new Dictionary<int, List<int>>();

        // ---------- 入口 ----------
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            foreach (var a in args)
            {
                if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
                if (a.StartsWith("--timeout=", StringComparison.OrdinalIgnoreCase))
                {
                    int t;
                    if (int.TryParse(a.Substring(10), out t) && t >= 1000) probeTimeoutMs = t;
                }
            }

            logPath = Path.Combine(Path.GetTempPath(), "Fix-ChatGPT_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

            quick = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            api = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };

            bool ok = false;
            try { ok = Run(); }
            catch (FixAbort ex) { PrintAbortHint(ex.Code); }
            catch (Exception ex)
            {
                Bad("执行中断：" + ex.Message);
                Dim(ex.GetType().Name);
            }

            Summary(ok);
            SaveLog();
            Pause();
            return ok ? 0 : 1;
        }

        // ================= 主流程 =================
        static bool Run()
        {
            Console.WriteLine();
            Line("  ================================================", ConsoleColor.Cyan);
            Line("        ChatGPT 打不开  ·  一键修复", ConsoleColor.Cyan);
            Line("  ================================================", ConsoleColor.Cyan);
            if (dryRun) Note("当前为「只看不改」模式，不会修改任何设置");

            const int TOTAL = 7;

            // ---------- 1 定位程序 ----------
            Step(1, TOTAL, "定位代理软件");
            vergeExe = FindVergeExe();
            if (string.IsNullOrEmpty(vergeExe)) throw new FixAbort("not-found");
            Ok("程序位置：" + vergeExe);

            bool vergeRunning = VergeProcs().Count > 0;
            var cores = Cores();
            if (vergeRunning) Ok("Clash Verge 正在运行");
            else Note("Clash Verge 未运行，稍后会启动它");
            Ok("内核进程数量：" + cores.Count);

            // ---------- 2 内核健康 ----------
            Step(2, TOTAL, "检查内核与控制端口是否健康");
            apiPort = ResolveApiPort();

            bool needRestart = false;
            string reason = "";
            if (!vergeRunning) { needRestart = true; reason = "Clash Verge 未运行"; }
            else if (cores.Count == 0) { needRestart = true; reason = "没有找到内核进程"; }
            else if (cores.Count > 1) { needRestart = true; reason = "检测到 " + cores.Count + " 个内核同时运行（重复 / 孤儿内核）"; }
            else if (apiPort == 0) { needRestart = true; reason = "内核控制端口（API）无法访问"; }

            if (!needRestart)
            {
                mixedPort = ResolveMixedPort(apiPort);
                var apiPids = ListenerPids(apiPort);
                var mixPids = mixedPort > 0 ? ListenerPids(mixedPort) : new List<int>();
                bool sameProc = apiPids.Any(p => mixPids.Contains(p));

                if (mixPids.Count == 0)
                {
                    needRestart = true;
                    reason = "没有找到本地代理监听端口";
                }
                else if (!sameProc)
                {
                    needRestart = true;
                    reason = "控制端口与代理端口不在同一个内核上（" + apiPort + " vs " + mixedPort + "）";
                }
            }

            if (needRestart)
            {
                Bad("异常：" + reason);
                Dim("这会导致「界面上改设置不生效」，ChatGPT 因此一直打不开。");
                if (dryRun)
                {
                    Note("（只看不改）本应清理内核并重启 Clash Verge");
                }
                else
                {
                    if (cores.Count > 0) { KillAll(cores); Thread.Sleep(800); }
                    KillAll(VergeProcs());
                    Thread.Sleep(1500);
                    KillAll(Cores());
                    Thread.Sleep(1200);

                    fixes.Add(vergeRunning ? "清理重复 / 孤儿内核并重启 Clash Verge" : "启动 Clash Verge");
                    StartDetached(vergeExe);
                    Dim("已请求启动，等待内核就绪……");
                    if (!WaitApiReady(45)) throw new FixAbort("api-not-ready");

                    mixedPort = ResolveMixedPort(apiPort);
                    cores = Cores();
                    Ok("内核已就绪：进程 " + cores.Count + " 个，API 端口 " + apiPort + "，代理端口 " + mixedPort);
                }
            }
            else
            {
                Ok("内核状态正常（单内核），API 端口 " + apiPort + "，代理端口 " + mixedPort);
            }

            // ---------- 3 分流模式 ----------
            Step(3, TOTAL, "检查分流模式");
            string mode = "";
            bool tunOn = false;
            var cfg = Api("GET", "/configs");
            if (cfg.code != 200) throw new FixAbort("api-lost");
            using (var doc = JsonDocument.Parse(cfg.body))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("mode", out var mEl)) mode = mEl.GetString() ?? "";
                if (root.TryGetProperty("tun", out var tun) && tun.TryGetProperty("enable", out var en) && en.ValueKind == JsonValueKind.True) tunOn = true;
            }
            Ok("当前模式：" + mode + "（TUN：" + (tunOn ? "开启" : "关闭") + "）");

            if (!string.Equals(mode, "rule", StringComparison.OrdinalIgnoreCase))
            {
                Bad("处于「" + mode + "」模式：所有流量被强制走同一个节点，ChatGPT 专用分流被绕过");
                if (!dryRun)
                {
                    var r = Api("PATCH", "/configs", "{\"mode\":\"rule\"}");
                    if (r.code >= 200 && r.code < 300)
                    {
                        fixes.Add("分流模式已切换为「规则模式」");
                        Ok("已切换为规则模式");
                    }
                    else Bad("切换失败（HTTP " + r.code + "）");
                }
                else Note("（只看不改）本应切换为规则模式");
            }
            else Ok("规则模式正确，ChatGPT 会走专用策略组");

            // ---------- 4 策略组 ----------
            Step(4, TOTAL, "定位 ChatGPT 策略组");
            var px = Api("GET", "/proxies");
            if (px.code != 200) throw new FixAbort("api-lost");

            string groupName = null;
            var members = new List<string>();
            var selectorNames = new HashSet<string>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(px.body))
            {
                var root = doc.RootElement.GetProperty("proxies");
                foreach (var prop in root.EnumerateObject())
                {
                    var v = prop.Value;
                    if (!v.TryGetProperty("type", out var t) || t.GetString() != "Selector") continue;
                    selectorNames.Add(prop.Name);
                    if (Regex.IsMatch(prop.Name, "chatgpt|openai", RegexOptions.IgnoreCase) && groupName == null)
                    {
                        groupName = prop.Name;
                        if (v.TryGetProperty("all", out var all)) members.AddRange(all.EnumerateArray().Select(x => x.GetString()));
                    }
                }

                if (groupName == null && root.TryGetProperty("GLOBAL", out var g))
                {
                    groupName = "GLOBAL";
                    if (g.TryGetProperty("all", out var all2)) members.AddRange(all2.EnumerateArray().Select(x => x.GetString()));
                    Note("订阅里没有 ChatGPT 专用策略组，改用 GLOBAL 组挑选节点");
                }
            }

            if (string.IsNullOrEmpty(groupName) || members.Count == 0) throw new FixAbort("no-group");

            string current = GetGroupNow(groupName);
            Ok("策略组：" + groupName + "，共 " + members.Count + " 个候选");
            Ok("当前选中：" + current);

            string finalNode = null;
            int lastBlockedCode = 0;

            // 当前节点如果已经能用，就不折腾它（避免反复切换触发风控）
            bool alreadyGood = false;
            if (!dryRun && !string.IsNullOrEmpty(current))
            {
                int c0; string m0; bool g0;
                if (VerifyChatGPT(out c0, out m0, out g0) && c0 >= 200 && c0 < 400)
                {
                    Ok("当前节点已能正常打开 chatgpt.com（HTTP " + c0 + "），无需更换");
                    finalNode = current;
                    alreadyGood = true;
                }
            }

            // ---------- 5 探测节点 ----------
            Step(5, TOTAL, "逐个探测节点（目标 chatgpt.com）");
            var usable = new List<KeyValuePair<string, int>>();
            int tried = 0;
            if (alreadyGood)
            {
                Note("当前节点可用，跳过探测");
                Step(6, TOTAL, "应用节点并做真实请求校验");
                Note("保持不变");
            }
            else
            {
                foreach (var n in members)
                {
                    if (Regex.IsMatch(n, "^(DIRECT|REJECT|REJECT-DROP|PASS|COMPATIBLE)$", RegexOptions.IgnoreCase)) continue;
                    tried++;
                    Console.Write("       · 探测 " + n);
                    int? d = NodeDelay(n);
                    if (d.HasValue)
                    {
                        Pad(n);
                        Line("  " + d.Value + "ms", ConsoleColor.Green);
                        usable.Add(new KeyValuePair<string, int>(n, d.Value));
                    }
                    else
                    {
                        Pad(n);
                        Line("  不通", ConsoleColor.DarkGray);
                    }
                }

                if (usable.Count == 0) throw new FixAbort("no-node");
                // 真正的节点优先，嵌套策略组（如「节点选择」）放到最后：它只是转发，容易被风控
                usable.Sort((a, b) =>
                {
                    bool sa = selectorNames.Contains(a.Key), sb = selectorNames.Contains(b.Key);
                    if (sa != sb) return sa ? 1 : -1;
                    return a.Value.CompareTo(b.Value);
                });
                Ok("可用节点 " + usable.Count + " / " + tried + " 个，首选：" + usable[0].Key + "（" + usable[0].Value + "ms）");

                // ---------- 6 应用节点 + 真实校验 ----------
                Step(6, TOTAL, "应用节点并做真实请求校验");

                foreach (var cand in usable.Take(5))
                {
                    if (dryRun)
                    {
                        Note("（只看不改）本应切换到：" + cand.Key);
                        finalNode = cand.Key;
                        break;
                    }

                    var setRes = Api("PUT", "/proxies/" + Uri.EscapeDataString(groupName), "{\"name\":\"" + cand.Key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
                    if (setRes.code < 200 || setRes.code >= 300)
                    {
                        Note("切换 " + cand.Key + " 失败（HTTP " + setRes.code + "），跳过");
                        continue;
                    }
                    Thread.Sleep(400);

                    int code; string msg; bool gotResp;
                    bool reachable = VerifyChatGPT(out code, out msg, out gotResp);
                    if (reachable)
                    {
                        Ok("切换到 " + cand.Key + " → chatgpt.com 返回 " + code + "，连通正常");
                        finalNode = cand.Key;
                        break;
                    }

                    if (gotResp)
                    {
                        Note(cand.Key + " 返回 " + code + "（被 Cloudflare 判定为异常流量），换下一个");
                        if (lastBlockedCode == 0) lastBlockedCode = code;
                    }
                    else
                    {
                        Note(cand.Key + " 延迟正常但请求失败：" + Short(msg));
                    }
                }
            }

            if (finalNode == null)
            {
                if (lastBlockedCode != 0)
                {
                    Bad("所有候选节点都被 Cloudflare 拦截（最近一次 HTTP " + lastBlockedCode + "），不是代理不通，而是线路 IP 被风控");
                    throw new FixAbort("blocked");
                }
                throw new FixAbort("verify-failed");
            }
            if (!dryRun && !string.Equals(current, finalNode, StringComparison.Ordinal))
                fixes.Add("ChatGPT 节点：" + (string.IsNullOrEmpty(current) ? "(未知)" : current) + " → " + finalNode);

            // ---------- 7 系统代理 ----------
            Step(7, TOTAL, "校验系统代理");
            bool enabled; string server, autoCfg;
            GetSysProxy(out enabled, out server, out autoCfg);
            string want = "127.0.0.1:" + mixedPort;

            if (tunOn)
            {
                Ok("检测到 TUN 模式，无需依赖系统代理");
            }
            else if (!string.IsNullOrEmpty(autoCfg))
            {
                Note("检测到自动配置脚本（PAC）：" + autoCfg + "，不自动修改");
            }
            else if (!enabled)
            {
                Bad("系统代理处于关闭状态，浏览器不会走代理");
                if (!dryRun) { SetSysProxy(want); fixes.Add("系统代理已开启并指向 " + want); Ok("已开启系统代理：" + want); }
                else Note("（只看不改）本应开启系统代理：" + want);
            }
            else if (!string.Equals(server, want, StringComparison.OrdinalIgnoreCase))
            {
                Bad("系统代理指向 " + server + "，但本地代理端口是 " + mixedPort);
                if (!dryRun) { SetSysProxy(want); fixes.Add("系统代理已修正为 " + want); Ok("已修正系统代理：" + want); }
                else Note("（只看不改）本应修正为 " + want);
            }
            else Ok("系统代理正确：" + server);

            return true;
        }

        // ================= 进程 / 端口 =================
        static List<Process> Cores()
        {
            var set = new HashSet<string>(CoreNames, StringComparer.OrdinalIgnoreCase);
            var list = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                try { if (set.Contains(p.ProcessName)) list.Add(p); } catch { }
            }
            return list;
        }

        static List<Process> VergeProcs()
        {
            var list = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.StartsWith("clash-verge", StringComparison.OrdinalIgnoreCase)) list.Add(p); } catch { }
            }
            return list;
        }

        static void KillAll(IEnumerable<Process> procs)
        {
            foreach (var p in procs)
            {
                try { p.Kill(true); } catch { try { p.Kill(); } catch { } }
            }
        }

        static void RefreshPorts()
        {
            var map = new Dictionary<int, List<int>>();
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        var rx = new Regex(@"^\s*TCP\s+(\S+)\s+\S+\s+LISTENING\s+(\d+)\s*$");
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            var m = rx.Match(line);
                            if (!m.Success) continue;
                            var addr = m.Groups[1].Value;
                            int idx = addr.LastIndexOf(':');
                            if (idx < 0) continue;
                            int port;
                            if (!int.TryParse(addr.Substring(idx + 1), out port)) continue;
                            int pid;
                            if (!int.TryParse(m.Groups[2].Value, out pid)) continue;
                            if (!map.ContainsKey(port)) map[port] = new List<int>();
                            if (!map[port].Contains(pid)) map[port].Add(pid);
                        }
                        try { p.WaitForExit(5000); } catch { }
                    }
                }
            }
            catch { }
            portMap = map;
        }

        static List<int> ListenerPids(int port)
        {
            if (portMap == null || portMap.Count == 0) RefreshPorts();
            List<int> v;
            if (portMap.TryGetValue(port, out v)) return v;
            return new List<int>();
        }

        static List<int> PortsOfPid(int pid)
        {
            var res = new List<int>();
            foreach (var kv in portMap) if (kv.Value.Contains(pid)) res.Add(kv.Key);
            return res;
        }

        // ================= Clash API =================
        static bool ApiAlive(int port)
        {
            if (port <= 0) return false;
            try
            {
                using (var resp = quick.GetAsync("http://127.0.0.1:" + port + "/version").GetAwaiter().GetResult())
                    return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        static (int code, string body) Api(string method, string path, string json = null)
        {
            try
            {
                var req = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:" + apiPort + path);
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var resp = api.SendAsync(req).GetAwaiter().GetResult())
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return ((int)resp.StatusCode, body);
                }
            }
            catch (Exception ex) { return (0, ex.Message); }
        }

        static int ResolveApiPort()
        {
            RefreshPorts();
            var cands = new List<int>();
            foreach (var c in Cores()) cands.AddRange(PortsOfPid(c.Id));
            cands.AddRange(ApiPortPrefer);
            foreach (var pt in cands.Distinct())
                if (ApiAlive(pt)) return pt;
            return 0;
        }

        static int ResolveMixedPort(int apiP)
        {
            RefreshPorts();
            var ports = new List<int>();
            foreach (var c in Cores()) ports.AddRange(PortsOfPid(c.Id));
            ports = ports.Where(p => p > 0 && p != apiP).Distinct().ToList();
            foreach (var pref in MixedPortPrefer)
                if (ports.Contains(pref)) return pref;
            return ports.Count > 0 ? ports[0] : 0;
        }

        static bool WaitApiReady(int maxSec)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSec)
            {
                if (apiPort > 0 && ApiAlive(apiPort)) return true;
                foreach (var c in Cores())
                {
                    RefreshPorts();
                    foreach (var pt in PortsOfPid(c.Id))
                    {
                        if (ApiAlive(pt)) { apiPort = pt; return true; }
                    }
                }
                Thread.Sleep(700);
            }
            return false;
        }

        static string GetGroupNow(string group)
        {
            var r = Api("GET", "/proxies/" + Uri.EscapeDataString(group));
            if (r.code != 200) return "";
            try
            {
                using (var d = JsonDocument.Parse(r.body))
                {
                    JsonElement e;
                    if (d.RootElement.TryGetProperty("now", out e)) return e.GetString() ?? "";
                }
            }
            catch { }
            return "";
        }

        static int? NodeDelay(string node)
        {
            var path = "/proxies/" + Uri.EscapeDataString(node) + "/delay?timeout=" + probeTimeoutMs +
                       "&url=" + Uri.EscapeDataString("https://chatgpt.com");
            var r = Api("GET", path);
            if (r.code != 200) return null;
            try
            {
                using (var d = JsonDocument.Parse(r.body))
                {
                    JsonElement e;
                    if (d.RootElement.TryGetProperty("delay", out e) && e.ValueKind == JsonValueKind.Number)
                        return e.GetInt32();
                }
            }
            catch { }
            return null;
        }

        // ================= 网络校验 =================
        static bool VerifyChatGPT(out int code, out string msg, out bool gotResponse)
        {
            code = 0; msg = ""; gotResponse = false;
            try
            {
                var handler = new HttpClientHandler
                {
                    UseProxy = true,
                    Proxy = new WebProxy("http://127.0.0.1:" + mixedPort, false),
                    AllowAutoRedirect = true,
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true
                };
                using (var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) })
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com"))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", UA);
                    using (var resp = c.SendAsync(req).GetAwaiter().GetResult())
                    {
                        code = (int)resp.StatusCode;
                        gotResponse = true;
                        // 只有 2xx/3xx 才算真正能打开；401/403/421 属于被 OpenAI / Cloudflare 风控
                        return code >= 200 && code < 400;
                    }
                }
            }
            catch (Exception ex) { msg = ex.Message; return false; }
        }

        // ================= 系统代理 =================
        static void GetSysProxy(out bool enabled, out string server, out string autoCfg)
        {
            enabled = false; server = ""; autoCfg = "";
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return;
                    var en = k.GetValue("ProxyEnable");
                    enabled = en != null && Convert.ToInt32(en) == 1;
                    server = Convert.ToString(k.GetValue("ProxyServer") ?? "");
                    autoCfg = Convert.ToString(k.GetValue("AutoConfigURL") ?? "");
                }
            }
            catch { }
        }

        static void SetSysProxy(string server)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (k == null) return;
                    k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    k.SetValue("ProxyServer", server, RegistryValueKind.String);
                }
                try
                {
                    InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
                    InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
                }
                catch { }
            }
            catch { }
        }

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        // ================= 定位程序 =================
        static string FindVergeExe()
        {
            foreach (var p in VergeProcs())
            {
                try
                {
                    var f = p.MainModule.FileName;
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) return f;
                }
                catch { }
            }

            var cands = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(pf)) cands.Add(Path.Combine(pf, "Clash Verge", "clash-verge.exe"));
            if (!string.IsNullOrEmpty(pf86)) cands.Add(Path.Combine(pf86, "Clash Verge", "clash-verge.exe"));
            if (!string.IsNullOrEmpty(lad))
            {
                cands.Add(Path.Combine(lad, "Programs", "Clash Verge", "clash-verge.exe"));
                cands.Add(Path.Combine(lad, "Clash Verge", "clash-verge.exe"));
            }
            cands.Add(@"D:\APP\CLASH\INSTALL PLACE\clash-verge.exe");
            cands.Add(@"C:\APP\CLASH\INSTALL PLACE\clash-verge.exe");
            foreach (var c in cands) if (File.Exists(c)) return c;

            // 注册表卸载项
            var roots = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var root in roots)
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using (var un = hive.OpenSubKey(root))
                        {
                            if (un == null) continue;
                            foreach (var sub in un.GetSubKeyNames())
                            {
                                using (var sk = un.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    var name = Convert.ToString(sk.GetValue("DisplayName") ?? "");
                                    if (!Regex.IsMatch(name, "clash", RegexOptions.IgnoreCase)) continue;
                                    foreach (var key in new[] { "DisplayIcon", "InstallLocation" })
                                    {
                                        var val = Convert.ToString(sk.GetValue(key) ?? "").Trim('"', ' ');
                                        if (string.IsNullOrEmpty(val)) continue;
                                        if (Directory.Exists(val))
                                        {
                                            var t = Path.Combine(val, "clash-verge.exe");
                                            if (File.Exists(t)) return t;
                                        }
                                        else if (File.Exists(val)) return val;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 最后：限定深度搜索（最多 6 秒）
            var sw = Stopwatch.StartNew();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var hit = SearchExe(drive.RootDirectory.FullName, 3, sw, 6000);
                if (hit != null) return hit;
            }
            return null;
        }

        static string SearchExe(string dir, int depth, Stopwatch sw, int budgetMs)
        {
            if (sw.ElapsedMilliseconds > budgetMs) return null;
            try
            {
                var direct = Path.Combine(dir, "clash-verge.exe");
                if (File.Exists(direct)) return direct;
                if (depth <= 0) return null;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (sw.ElapsedMilliseconds > budgetMs) return null;
                    var name = Path.GetFileName(sub);
                    if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    var r = SearchExe(sub, depth - 1, sw, budgetMs);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        static void StartDetached(string exe)
        {
            try
            {
                // 借 explorer 启动，避免子进程随本程序退出被回收
                var psi = new ProcessStartInfo("explorer.exe", "\"" + exe + "\"");
                Process.Start(psi);
            }
            catch { }
        }

        // ================= 输出 =================
        static void Line(string s, ConsoleColor c)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
            logBuf.AppendLine(s);
        }
        static void Step(int i, int total, string t)
        {
            Console.WriteLine();
            Line("[" + i + "/" + total + "] " + t, ConsoleColor.White);
        }
        static void Ok(string m) { Line("       [OK] " + m, ConsoleColor.Green); }
        static void Bad(string m) { Line("       [!!] " + m, ConsoleColor.Red); }
        static void Note(string m) { Line("       [--] " + m, ConsoleColor.Yellow); }
        static void Dim(string m) { Line("            " + m, ConsoleColor.DarkGray); }
        static void Pad(string s)
        {
            int w = 0;
            foreach (var ch in s) w += ch > 127 ? 2 : 1;   // 中日韩字符占两格
            int target = 22;
            if (w < target) Console.Write(new string(' ', target - w));
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        static void PrintAbortHint(string code)
        {
            Console.WriteLine();
            switch (code)
            {
                case "not-found":
                    Bad("没有找到 Clash Verge。");
                    Dim("请确认已安装 Clash Verge；本工具不适用于其他代理软件。");
                    break;
                case "api-not-ready":
                    Bad("Clash Verge 已启动，但内核控制端口在 45 秒内没有就绪。");
                    Dim("请手动打开 Clash Verge 主界面确认是否正常启动，然后重新运行本工具。");
                    break;
                case "api-lost":
                    Bad("无法读取 Clash 配置（API 无响应）。");
                    Dim("请确认 Clash Verge 界面能正常打开后重试。");
                    break;
                case "no-group":
                    Bad("没有找到任何可选策略组，无法自动选节点。");
                    Dim("请在 Clash Verge 中确认订阅已成功导入。");
                    break;
                case "no-node":
                    Bad("所有节点都无法连通 chatgpt.com。");
                    Dim("通常是订阅到期、流量用尽，或该机场的 OpenAI 线路全部失效。");
                    Dim("建议：打开 Clash Verge → 订阅 → 更新订阅后重新运行本工具。");
                    break;
                case "verify-failed":
                    Bad("没有节点能真正打开 chatgpt.com。");
                    Dim("建议：在 Clash Verge 里做一次「延迟测试」，换一个机场后重新运行本工具。");
                    break;
                case "blocked":
                    Dim("说明：代理链路是通的，但该线路的出口 IP 被 OpenAI 限制。");
                    Dim("建议：换用标注「原生 IP / 解锁 ChatGPT」的节点或机场后重试。");
                    break;
                default:
                    Bad("修复未完成：" + code);
                    break;
            }
        }

        static void Summary(bool ok)
        {
            Console.WriteLine();
            Line("  ================================================", ConsoleColor.Cyan);
            if (ok) Line("   修复完成，现在可以刷新浏览器打开 ChatGPT", ConsoleColor.Green);
            else Line("   修复未完成，请查看上方红色提示", ConsoleColor.Red);
            Line("  ================================================", ConsoleColor.Cyan);

            if (fixes.Count > 0)
            {
                Line("  本次做过的改动：", ConsoleColor.White);
                for (int i = 0; i < fixes.Count; i++)
                    Line("    " + (i + 1) + ". " + fixes[i], ConsoleColor.Gray);
            }
            else
            {
                Line("  本次没有修改任何设置（环境本来就正常）", ConsoleColor.Gray);
            }

            if (ok)
            {
                Console.WriteLine();
                Line("  提示：", ConsoleColor.Yellow);
                Line("   · 若下次又打不开，直接再运行一次本工具即可", ConsoleColor.Gray);
                Line("   · 关闭 Clash Verge 请用托盘图标「退出」，不要用任务管理器强杀", ConsoleColor.Gray);
                Line("   · 详细日志：" + logPath, ConsoleColor.DarkGray);
            }
            Console.WriteLine();
        }

        static void SaveLog()
        {
            try { File.WriteAllText(logPath, logBuf.ToString(), new UTF8Encoding(true)); } catch { }
        }

        static void Pause()
        {
            try
            {
                if (Console.IsInputRedirected) return;
                Console.Write("按任意键退出…");
                Console.ReadKey(true);
                Console.WriteLine();
            }
            catch { }
        }
    }

    internal class FixAbort : Exception
    {
        public string Code { get; private set; }
        public FixAbort(string code) : base(code) { Code = code; }
    }
}
