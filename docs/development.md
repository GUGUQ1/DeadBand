# 开发说明

> **如果你是被派来改这个仓库的 AI**，先读根目录的 [AGENTS.md](../AGENTS.md)。那份记的是"不知道就会踩"的硬约束和踩坑记录，这份偏构建与结构。

## 编译

不需要 Visual Studio，用的是 Windows 自带的 .NET Framework 编译器：

```bat
build.cmd
```

产物：`dist\DeadBand.exe`（约 155 KB，单文件，无依赖）。

组发布包：

```bat
package.cmd
```

`package.ps1` 会：读 `src\AssemblyInfo.cs` 里的版本号 → 编译 → 摆目录 → 打 zip。**注意 `package.ps1` 必须存成 UTF-8 带 BOM**，否则 Windows PowerShell 5.1 会按 GBK 解码中文，整个脚本解析失败。

## 硬约束：只能用 C# 5

编译器是 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，语法上限是 C# 5。**不能用**：

| 不能用 | 替代 |
|---|---|
| 字符串插值 `$"{x}"` | `"a" + x` 或 `string.Format` |
| 空条件运算符 `a?.b` | `a == null ? null : a.b` |
| 表达式体成员 `=> expr;` | 完整方法体 |
| `nameof(x)` | 手写字符串 |
| 元组、模式匹配、`out var` | 传统写法 |
| 属性初始化器 | 在构造函数里赋值 |

## 源文件职责

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口、`App`（托盘、引导调度、全屏避让、显示器热插拔、信号） |
| `ZoneHost.cs` | AppBar 窗口 + 分层合成器（`ComposeLayers` 是所有绘制规则的唯一入口） |
| `Model.cs` | `ZoneProfile` / `LineModel` / `ProfileStore`（ini 读写）/ 内容层接口 |
| `Interop.cs` | 全部 P/Invoke：DPI、显示器枚举、AppBar、SetWindowPos、进程名 |
| `Clock.cs` | 像素时钟（5×7 点阵字形表 + 排版） |
| `Spectrum.cs` / `AudioSpectrum.cs` | 频谱绘制 / WASAPI loopback 采集 + FFT |
| `Caption.cs` | 字幕数据、选项、`FeedServer`（本地 HTTP）、字幕层绘制 |
| `Pomodoro.cs` | 番茄钟状态机与绘制 |
| `Calibrator.cs` | 整屏标定界面（白底 + 四步引导 + 洋红标记） |
| `SettingsForm.cs` | 设置窗口（四个标签页，即时生效） |
| `ColorPicker.cs` | 取色器（可从屏幕像素取色） |
| `ClockSettings.cs` | 时钟字号与位置的拖动界面 |
| `Onboarding.cs` | 首次使用引导（八步） |
| `Autostart.cs` | HKCU 启动项读写 |
| `AssemblyInfo.cs` | 版本 / 产品 / 版权（`package.ps1` 从这里读版本号） |

## 改 UI 之后必须跑的自检

```bat
dist\DeadBand.exe --ui-check
```

它会构造取色器、时钟设置、首次引导三个窗口，把三类问题写进 `%LOCALAPPDATA%\DeadBand\ui-check*.txt`：

- `OVERLAP` —— 同一容器里两个控件相交
- `CLIPPED` —— 定高标签的文字被裁（`AutoSize` 的标签自己算高度，不在此列）
- `OVERFLOW` —— 控件被父容器压住（等于用户看不到）

最后一行 `overlaps=N`，必须是 0。

> 教训：**不要自己用 `TextRenderer.MeasureText` 算高度再赋给 `Height`**。Label 自己的排版带内边距，同样宽度下会比你算的多折一行，文字溢出后又被下面的控件压住，而且自检用的是同一套算法 —— 两边一起错，报 0。要用就 `AutoSize = true` + `MaximumSize`，让控件自己算。

## 启动参数

| 参数 | 作用 |
|---|---|
| `--calibrate` | 直接进标定 |
| `--settings` | 打开设置窗口 |
| `--preview` | 切换预览模式（示例频谱/字幕） |
| `--onboard` | 强制重跑首次引导 |
| `--startup` | 开机自启模式：等任务栏就绪再占位，且不弹引导 |
| `--ui-check` | 只跑 UI 自检，不启动主程序 |
| `--color` / `--clock` / `--off` | 取色 / 时钟设置 / 退出 |

单实例运行：第二个实例会通过命名事件把参数转给已在运行的实例，然后自己退出。

## 诊断文件

都在 `%LOCALAPPDATA%\DeadBand\`：

| 文件 | 什么时候看 |
|---|---|
| `last.txt` | 先看这个：最近一次状态、区带位置、`bottomGap`、前台窗口 |
| `fullscreen.txt` | 全屏避让试了几次、对方让没让 |
| `fg.txt` | 前台窗口矩形与判定快照（变化时才写） |
| `displays.txt` | 启动时的显示器枚举结果 |
| `ui-check*.txt` | UI 自检结果 |

## 截图与实拍

README 里的「改造前后」对照图是手机实拍，用脚本处理（切变摆平 + 裁掉桌面杂物）：

```bash
python tools/make-compare.py <改造前照片> <改造后照片> docs/images
```

裁切参数是对着特定照片量的，换照片要重调 `prep()` 里的 crop 值。

## 教程 PDF

`docs/DeadBand使用教程.pdf` 由 `docs/tutorial.md` 生成，链路是 Markdown → HTML(A4 打印 CSS) → Edge headless `--print-to-pdf`：

```bash
python tools/make-pdf.py
```

改了 tutorial.md 或 docs/images 里的图之后重跑一次。校验方法：用 poppler 把 PDF 逐页转图看一眼

```bash
pdftoppm -png -r 96 docs/DeadBand使用教程.pdf %TEMP%\p
```

重点看三件事：中文有没有豆腐块、标题有没有被挤到页底（孤行）、图有没有被切断。
