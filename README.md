# ChatGPT 一键修复 (FixChatGPT)

> 针对 **Clash Verge / mihomo** 环境：ChatGPT 打不开时，双击一下，自动诊断并修复。

![platform](https://img.shields.io/badge/platform-Windows-blue) ![dotnet](https://img.shields.io/badge/.NET-8.0-purple) ![size](https://img.shields.io/badge/size-179KB-green)

## 它解决什么问题

ChatGPT 打不开，在 Clash 环境下通常不是浏览器的问题，而是代理配置出了状况。本工具针对三类最常见、也最隐蔽的故障：

| 故障 | 现象 | 本工具的处理 |
|---|---|---|
| **重复 / 孤儿内核** | 两个 `verge-mihomo` 并存，孤儿进程抢占 API 端口，真正干活的内核没有控制端口 → **界面上改任何设置都不生效** | 检测出多内核或端口归属不一致时，清理后干净重启 |
| **全局模式** | 所有流量被强制走同一个节点，ChatGPT 专用分流规则被完全绕过 | 自动切回「规则模式」 |
| **节点失效 / 被风控** | 延迟测试看似正常，实际出口 IP 被 Cloudflare / OpenAI 拦截（HTTP 403） | 逐节点实测 `chatgpt.com`，只认 2xx/3xx，自动换到真正能用的节点 |

## 使用方法

### 方式一：直接运行（推荐）

1. 到 [Releases](../../releases) 下载 `FixChatGPT.exe`
2. 双击运行，等它跑完 7 个步骤
3. 看到绿色「修复完成」即可刷新浏览器

> 依赖 .NET 8 Runtime（Windows 10/11 一般已有；没有会提示安装）。

### 方式二：命令行

```text
FixChatGPT.exe              正常修复
FixChatGPT.exe --dry-run    只诊断，不修改任何设置
FixChatGPT.exe --timeout=3000   把节点探测超时调短（毫秒）
```

## 它做了什么（7 步）

```
[1/7] 定位代理软件          运行进程 → 常见路径 → 注册表 → 限时搜索
[2/7] 检查内核健康          多内核 / API 无响应 / 端口归属不一致 → 干净重启
[3/7] 检查分流模式          非 rule 则切换为规则模式
[4/7] 定位策略组            优先找含 chatgpt / openai 的 Selector 组，否则退用 GLOBAL
[5/7] 探测节点              逐个请求 chatgpt.com 测延迟；嵌套策略组排最后
[6/7] 应用节点 + 真实校验    先试当前节点，能用就不动；否则按延迟试前 5 个，严格校验 2xx/3xx
[7/7] 校验系统代理          指向不对或未开启则修正（TUN 模式自动跳过）
```

设计原则：

- **幂等**：环境正常时什么都不改，可以放心反复运行
- **最小干预**：能用就不折腾，避免反复切节点触发风控
- **可追溯**：每次运行在 `%TEMP%` 留一份日志，结尾会打印路径

## 从源码构建

```bash
cd src/FixChatGPT
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../out
```

产物为单文件 exe（约 180KB）。想零依赖分发（对方机器不装 .NET 也能跑），把 `--self-contained false` 改成 `true` 即可（体积约 65MB）。

另附 PowerShell 备用版 [`src/FixChatGPT/Fix-ChatGPT.ps1`](src/FixChatGPT/Fix-ChatGPT.ps1)，逻辑相同，适合不想运行 exe 的场景：

```powershell
powershell -ExecutionPolicy Bypass -File src/FixChatGPT/Fix-ChatGPT.ps1
```

## 常见提示对照

| 屏幕提示 | 含义 | 建议 |
|---|---|---|
| `所有节点都无法连通 chatgpt.com` | 订阅到期 / 流量用尽 / 机场线路全挂 | 更新订阅后重跑 |
| `所有候选节点都被 Cloudflare 拦截` | 链路通，但出口 IP 被 OpenAI 风控 | 换「原生 IP / 解锁 ChatGPT」的节点或机场 |
| `内核控制端口 45 秒内没有就绪` | Clash Verge 启动异常 | 手动打开主界面确认后重跑 |
| `没有找到 Clash Verge` | 未安装或安装路径特殊 | 本工具不适用于其他代理软件 |

## 适用范围与免责

- 仅针对 **Clash Verge / mihomo**（Clash Meta 系内核）设计，其他代理软件不保证可用
- 本工具只修改**你本机**的 Clash 运行时配置与系统代理设置，不接触任何账号信息
- 请遵守所在地区法律法规使用网络工具

## License

MIT
