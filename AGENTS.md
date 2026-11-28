# AGENTS.md —— 接手指南

写给**下一个改这个仓库的 AI**。人也可以看，但这里记的主要是"不知道就会踩"的东西。

先读这一节，够你上手：

```bat
build.cmd                 :: 编译 -> dist\DeadBand.exe（不用 Visual Studio）
package.cmd               :: 编译 + 组发布包 -> release\DeadBand-v<版本>-win64.zip
dist\DeadBand.exe --ui-check    :: 改完 UI 必跑，结果写进 %LOCALAPPDATA%\DeadBand\ui-check*.txt
```

- 源码只能用 **C# 5** 语法（见第 2 节，这是最容易踩的一条）
- 程序当前版本 **1.1.0**，已发布 v1.0.0 / v1.1.0 两个 release
- 文档地图：`README.md`（给用户）· `docs/tutorial.md`（= 发布包里的 PDF 源）· `docs/faq.md`（排障）· `docs/how-it-works.md`（原理）· `docs/development.md`（构建与源文件职责）· 本文件（给 AI）

---

## 1. 这个东西是什么

把**坏屏（面板底部出现固定横向灰线）**下面那条废掉的横条，注册成 Windows AppBar 占下来，往里面画时钟 / 频谱 / B站 字幕。

三个动作缺一不可：

1. **占位** —— `ABM_NEW` / `ABM_QUERYPOS` / `ABM_SETPOS`，贴 `ABE_BOTTOM`
2. **避线** —— 按固定行距算出坏线所在行，清成底色
3. **收编** —— 在同样位置画同色竖向栅格，横的坏线 + 竖的网格 = 一张网

第 3 步是产品存在的理由。任何改动都不能破坏"坏线看起来像图案"这个效果。

**定位是"不干扰"**，三条不变量（改代码时别破坏）：

- 三层开关全关时，区带主动让出顶层窗口（用户要往那儿拖自己的桌面歌词）
- 有自己人的对话框开着时，不抢顶层（否则设置窗口会被区带盖住按钮）
- 鼠标只在内容声明的可点区域返回 `HTCLIENT`，其余 `HTTRANSPARENT` 穿透

---

## 2. 硬约束

### 2.1 只能用 C# 5

编译器是 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（Windows 自带），语法上限 C# 5。**不能用**：

| 不能用 | 改写成 |
|---|---|
| 字符串插值 `$"{x}"` | `"a" + x` / `string.Format` |
| `a?.b` / `a ?? b` 之外的现代运算符 | 显式判空 |
| 表达式体成员 `=> expr;` | 完整方法体 |
| `nameof(x)` | 手写字符串 |
| 元组、模式匹配、`out var`、局部函数 | 传统写法 |
| 属性初始化器 `{ get; set; } = 1` | 构造函数里赋值 |
| `using static` | 全名 |

### 2.2 `build.cmd` 是**显式文件列表**，不是通配

新增 `.cs` 必须加进 `build.cmd` 第 16 行那串路径里，否则 `csc` 直接报找不到类型。

### 2.3 `build.cmd` 必须纯 ASCII + CRLF

里面的中文注释会在中文 Windows 的 GBK 代码页下解析错，`%OUT%` 变空、编译失败。要写说明就写英文。

### 2.4 `package.ps1` 必须存成 **UTF-8 带 BOM**

Windows PowerShell 5.1 对没有 BOM 的 `.ps1` 按系统 ANSI（简体中文是 GBK）解码，中文字符串直接乱码、整个脚本解析失败。用编辑器改完记得确认 BOM 还在：

```powershell
$b = [System.IO.File]::ReadAllBytes("package.ps1")[0..2]   # 应该是 EF BB BF
```

### 2.5 打包 zip 不能用 `Compress-Archive`

.NET Framework 版会把非 ASCII 文件名按**本地 ANSI（GBK）**写进 zip 且不置 UTF-8 标志位，换语言的 Windows 打开就是一串乱码。必须用：

```powershell
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $false, [System.Text.Encoding]::UTF8)
```

### 2.6 不要自己算 Label 高度

**不要** `TextRenderer.MeasureText(...)` 算高度再赋给 `Height`。Label 自己的排版带内边距，同样宽度下会比你算的多折一行，文字溢出后被下面的控件压住 —— 更糟的是自检用同一套算法，两边一起错、报 0。

用 `AutoSize = true` + `MaximumSize = new Size(width, 0)`，让控件自己算。需要固定占位的（比如预检那三行），显式 `AutoSize = false` 并给足高度。

### 2.7 内容层不许自己清背景

`IProvider.Render` 只画内容。背景由 `ZoneHost.OnPaint` 统一铺。竖线网格和坏线挖空都画在内容**之后**（否则栅格会被内容盖掉）。

### 2.8 目标显示器不在时必须停用

`App.WatchDisplays` 每 2 秒轮询。外接屏拔掉要 `host.Suspend()`（注销 AppBar、隐藏、停音频采集），插回来自动 `Resume`。这条是用户明确要求的行为。

---

## 3. 这台开发机的环境

| 项 | 值 |
|---|---|
| 屏幕 | 单显示器 2560×1440，**任务栏在顶部 60px**（底部整条留给区带） |
| DPI | 125%，进程在 `Main` 里调 `SetProcessDpiAwarenessContext(-4)`（PMv2），所以代码里所有坐标都是**物理像素** |
| 区带 | 用户当前的配置：`zoneheight=99`，坏线每 4 行一条（相位 -78，粗细 1） |
| 配置目录 | `%LOCALAPPDATA%\DeadBand\` |
| shell | PowerShell（`pwsh`）；`gh` 在 `C:\Program Files\GitHub CLI\gh.exe`，**不在 PATH** |

诊断文件（排障先看第一个）：

| 文件 | 内容 |
|---|---|
| `last.txt` | 最近一次状态与原因、区带位置、`bottomGap`、前台窗口快照 |
| `fullscreen.txt` | 全屏避让每次尝试（`try` / `refused`） |
| `fg.txt` | 前台窗口矩形判定（变化时才写） |
| `displays.txt` | 启动时的显示器枚举 |
| `feed-log.txt` / `dom.txt` | 字幕推送请求日志 / 浏览器脚本上报的 DOM 现场 |
| `ui-check*.txt` | UI 自检结果 |
| `options.ini` | 应用级选项（隐藏播放器字幕 / 走过引导 / **隐藏区带热键**） |
| `profiles\*.ini` | 每台显示器一份，纯 `key=value` |

---

## 4. 改代码的工作流

### 4.1 先知道该改哪个文件

| 想改什么 | 去哪个文件 |
|---|---|
| 画什么、怎么叠、谁让谁 | `ZoneHost.ComposeLayers`（所有绘制规则的唯一入口） |
| 配置项 | `Model.ZoneProfile` + `ProfileStore.Load/Save`（两边都要加，否则存了读不回来） |
| 托盘 / 热键 / 全屏避让 / 热插拔 | `Program.cs` 的 `App` |
| 首次引导某一步 | `Onboarding.cs` 里对应的 `BuildXxx()` |
| 标定界面 | `Calibrator.cs`（HUD 在 `DrawHud`，步骤文案在 `NowText`） |
| 设置窗口某个标签页 | `SettingsForm.cs` 的 `BuildXxxTab()` |
| Win32 调用 | `Interop.cs`（统一放这里，别散在别的文件） |

### 4.2 改完必须跑的自检

```bat
dist\DeadBand.exe --ui-check
```

会构造取色器 / 时钟设置 / 首次引导三个窗口，把三类问题写进 `ui-check*.txt`：

- `OVERLAP` 同容器里两个控件相交
- `CLIPPED` 定高标签文字被裁
- `OVERFLOW` 控件被父容器压住（用户直接看不到）

最后一行 `overlaps=N` 必须是 **0**。

> ⚠️ **`--ui-check` 不覆盖 `SettingsForm`**。设置窗口加控件之后只能靠截图看 —— 这一页很容易撑出滚动区，把底部按钮挤到看不见的地方（踩过两次）。

### 4.3 验证行为的办法

**看日志，不靠猜**：任何状态变化都会写 `last.txt`（`LogState`）。改了行为就启动程序、操作一下、读这个文件。

**合成输入**（不需要人手操作）：

```powershell
# 全局热键测试：Ctrl+Alt+H = VK 0x11 / 0x12 / 0x48，KEYEVENTF_KEYUP = 2
keybd_event(0x11,0,0,0); keybd_event(0x12,0,0,0); keybd_event(0x48,0,0,0)
keybd_event(0x48,0,2,0); keybd_event(0x12,0,2,0); keybd_event(0x11,0,2,0)
```

**截图验收**（区带只有 99px 高，要裁着看）：

```powershell
$bmp = New-Object System.Drawing.Bitmap 2560,99
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 1341, 0, 0, (New-Object System.Drawing.Size 2560, 99))
```

**截窗口**要先用 `DwmGetWindowAttribute(hwnd, 9, ...)` 拿**可见边框**（`GetWindowRect` 比可见区域大几像素，会把桌面透出来）。

### 4.4 几个坑（合成操作的坑，不是程序的）

- 第一次合成点击常常只把窗口激活、不触发按钮 → **点两次**
- `BM_CLICK` 对**隐藏页面**上的按钮无效（首次引导所有页的控件都在控件树里，能 GetWindowText 找到，但点了没反应）→ 先确认当前在哪一步，再点
- 键盘消息只送到焦点窗口。要按某窗口的键，先点它一下抢前台

---

## 5. 发布流程

### 5.1 身份（错了贡献者会显示成另一个人）

```
git config user.name    # 必须是 GUGUQ1（字母 I 收尾，不是 guguqi）
git config user.email   # 359526197+GUGUQ1@users.noreply.github.com
```

**新环境开工前先看这两行，错了就别提交。**

### 5.2 `gh` 的坑

```powershell
$gh = 'C:\Program Files\GitHub CLI\gh.exe'    # 不在 PATH，写全路径
gh api ...                                    # 永远可用（REST）
gh release create/upload/view --repo GUGUQ1/DeadBand ...
# 不带 --repo 会报 "unable to find git executable in PATH"
```

网络：**先直连**，失败再 `$env:HTTPS_PROXY='http://127.0.0.1:7890'`。

> 这台机器上 **`gh pr create` 没验证过**（另一个项目的记录说它会报找不到 git）。要开 PR 就先用 REST：`POST /repos/{owner}/{repo}/pulls`，别在 `gh pr` 上耗时间。

### 5.3 固定顺序

```powershell
# 1. 改版本号（唯一一处）+ 写 CHANGELOG + 更新 README/docs
#    src/AssemblyInfo.cs: AssemblyVersion("1.1.0.0") -> 1.2.0.0

# 2. 重新生成教程 PDF（改了 docs/tutorial.md 或 docs/images 才需要）
python tools/make-pdf.py

# 3. 组包（会先杀掉正在运行的 DeadBand，否则编译器写不进去 CS0016）
package.cmd

# 4. 提交（复杂信息写文件，别用行内引号）
git add -A; git status --short          # 确认没有 _backup/ dist/ release/ *.pdf
git commit -F "$env:TEMP\commit.txt"

# 5. annotated tag
git tag -a v1.2.0 -F "$env:TEMP\tag.txt"

# 6. 推送
git push origin main
git push origin v1.2.0

# 7. 发 release（notes 要写成用户能读的，别只贴 changelog）
gh release create v1.2.0 --repo GUGUQ1/DeadBand --title "..." --notes-file ... --latest "release\DeadBand-v1.2.0-win64.zip"

# 8. 核对
gh release view v1.2.0 --repo GUGUQ1/DeadBand --json isDraft,assets --jq ...
```

### 5.4 红线

- **已推送的 tag 不许 force push、不许移动**。改错就发新 tag
- **不许提交**：`_backup/`（用户的配置快照）、`dist/`、`release/`、`docs/DeadBand使用教程.pdf`（生成物，只作为 release 附件）、`*.zip`、`*.exe`
- 发布前跑一遍 `git status --short`，并抽查提交内容里有没有本机绝对路径 / 邮箱 / 用户名
- 下载 release 附件后比对 SHA256，确认上传没被截断

---

## 6. 已经踩过的坑（照着这张表避）

| 现象 | 根因 | 正确做法 |
|---|---|---|
| 编译报 CS0016 无法写入 exe | 程序正在运行，文件被占 | `package.ps1` 会先杀进程；手工编译前自己关掉 |
| `build.cmd` 编译失败、`%OUT%` 是空的 | 文件里有中文注释，GBK 代码页解析错 | `build.cmd` 保持纯 ASCII |
| `package.ps1` 一堆语法错误 | 没有 BOM，PS 5.1 按 GBK 解码 | 存成 UTF-8 **带 BOM** |
| 解压后中文文件名乱码 | `Compress-Archive` 写 GBK 名字且不置标志位 | `ZipFile.CreateFromDirectory(..., Encoding.UTF8)` |
| 文字被下面的控件压住，自检却报 0 | 手算 Label 高度比实际少一行 | `AutoSize` + `MaximumSize`，别自己量 |
| 全屏视频出现时区带消失 | 处理 `ABN_FULLSCREENAPP` 时 `Hide()` 了 | **绝不 Hide**，只 `ReassertTopmost()`；同时把全屏窗口压到区带上沿 |
| AppBar 注册不上 | 没有窗口句柄 | 先访问 `Handle` 强制创建窗口 |
| 坏线挖空位置偏了几十像素 | 用"屏幕底边 − 窗口高度"算窗口顶部 | 用 `PointToScreen(Point.Empty).Y`（AppBar 会被系统顶到任务栏上方） |
| 点击穿透判定错乱 | `WM_NCHITTEST` 的 lParam 是**屏幕**坐标，`WM_LBUTTONDOWN` 是**客户区**坐标 | 两个分开处理 |
| 引导打开后区带又冒出来 / 预检报"底部被占用 99px" | 显示器轮询把"故意挂起"的区带恢复了；预检把自己占的算成别人占的 | 加 `onboardingOpen` / `hiddenByUser` 标记；预检用 `ownZoneHeight` 扣掉自己那份 |
| 设置窗口底部按钮点不到 | 页面内容撑出滚动区 | 加高窗口 / 并排按钮；`--ui-check` 查不到这一页，要截图看 |
| 热键按了没反应 | 组合键被别的软件占用 | 注册失败要**弹提示**并写 `last.txt`，不能静默 |
| 开机自启后区带位置错 | 登录瞬间 explorer/任务栏还没就绪，`WorkingArea` 是过期的 | 带 `--startup` 时先等 `Shell_TrayWnd`（≥2.5s，最多 15s） |

---

## 7. 当前状态与已知未做

**已发布**：v1.0.0（首版）、v1.1.0（隐藏区带热键）

**功能**：像素时钟 / 音频频谱 / B站 字幕 / 番茄钟 / 三层叠加 / 背景网格 / 坏线挖空 / 全屏避让 / 开机自启 / 多显示器热插拔 / 隐藏热键 / 八步引导

**明确不支持**（用户已知悉，写进了 README 和教程）：

- 坏线在**顶部 / 中间散布 / 左右竖向** —— AppBar 只能贴四条边之一，贴中间是遮挡不是腾空间
- **多显示器同时**服务（一次一台，可在引导里换）
- 字幕只支持 **B站 网页播放器**（依赖 Tampermonkey 脚本）
- 程序界面**只有中文**（英文只有 README.en.md）

**待办 / 候选**（没被要求，别自己加）：

- 顶部区带（`ABE_TOP`）—— 技术上可行，但用户在顶部有任务栏，做了也验证不了
- 左右竖向坏线（同一套机制扩展到 `ABE_LEFT/RIGHT`）
- 界面 i18n（17 个源文件里全是硬编码中文，工作量大）
- 自动更新（现在靠"下载新 zip 覆盖 exe"）

---

## 8. 与这个项目的主人协作的规矩

- **先方案后实施**：改动稍大就先把"改哪些文件、怎么验收"说清楚，等一句确认再动手
- **诚实披露**：做不到的要写进文档（已知限制那一节），别用模糊话术糊过去
- **物理硬件相关的只能他自己测**：标定手感、面板坏线位置、开机自启后的实际落位。别让他做技术性诊断
- **截图用真实的**：README 的对照图是手机实拍（`tools/make-compare.py` 处理），不是渲染图
- **不确定就问**：他有时用语音输入，会漏字（比如"界面背景"其实是"标定界面背景"），歧义处确认一句比返工便宜
- 发现顺手的改进**记下来提一句**，不要顺手做进去
