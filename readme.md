# ImeKeyGuard

针对 Windows 微软拼音输入法的常驻按键守卫：

- **仅当待选框打开（正在拼音组合）时**拦截：
  - `Tab` 被吞掉，不再展开候选或必应建议；
  - `;` 被接管：先上屏第一个候选（发送 Space），再输出全角分号 `；`；
- **待选框未打开（无组合）时不生效**：Tab 正常插入制表符，`;` 交给输入法自己处理；
- **中文输入法的英文模式不生效**：按 `Shift` 切换到英文模式后，Tab 与 `;` 全部放行；
- 英文键盘下完全不接管。

程序为普通用户权限的托盘程序（约 15 KB），通过 `WH_KEYBOARD_LL` 全局低级键盘钩子实现；不注入任何进程、不修改系统文件、不改输入法设置项（仅写入开机自启项）。

## 行为规则

仅在**中文输入法激活**（前台线程键盘布局低字为 `0x0804`）、**中文模式**且未按 `Shift` / `Ctrl` / `Alt` 时生效：

| 状态 | `Tab` | `;` |
|---|---|---|
| 中文态、中文模式、待选框打开（组合中） | 吞掉，无任何输入 | 吞掉 → `Space` 上屏第一个候选 → 输出 `；` |
| 中文态、中文模式、待选框未打开 | 正常制表符 | 放行，由输入法处理（通常输出 `；`） |
| 中文态、英文模式（Shift 切换） | 正常制表符 | 正常英文分号 |
| 英文键盘 | 正常制表符 | 正常英文分号 |
| `Shift` / `Ctrl` / `Alt` 组合 | 放行 | 放行 |

## 安装

双击 `install.bat`，或：

```bat
install.bat
install.bat /quiet    :: 静默
```

安装脚本会：

1. 停止已在运行的实例；
2. 将 `ImeKeyGuard.exe`、源码、脚本复制到 `%LOCALAPPDATA%\Programs\ImeKeyGuard`（若当前目录即安装目录则跳过复制）；
3. 写入自启项 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ImeKeyGuard`；
4. 启动程序。

## 卸载

```bat
uninstall.bat
uninstall.bat /quiet
```

卸载脚本会停止进程、删除自启项，并把安装目录移入回收站。若目录被占用，会在后台每秒重试、最多 90 秒。

也可以直接退出托盘程序：退出后所有拦截立即失效，系统恢复原行为（自启项仍在，下次登录会再次启动）。

## 托盘菜单

| 菜单项 | 说明 |
|---|---|
| `Enabled` | 总开关，取消勾选后完全不拦截 |
| `Semicolon: commit + fullwidth ;` | 单独关闭分号增强（Tab 拦截仍生效） |
| `Exit` | 退出程序 |

## 组合态与中/英模式判定

微软拼音是 TSF-only 输入法，用户态没有可靠的组合态 API（实测：`ImmGetContext` 返回 `NULL`，`WM_IME_CONTROL` 始终返回 0；候选窗的 DWM cloak 状态恒定不变）。实现分三层：

### 1. 中/英模式（托盘输入指示器）

Windows 任务栏的“输入指示”按钮，其 UIA `Name` 会随模式变化：

- 中文模式：`任务栏输入指示 中文模式`
- 英文模式：`任务栏输入指示 英语模式`

程序缓存该元素，在每次 `Tab` / `;` 按下时同步读取一次名称：

- 英文模式 → 直接放行，不做任何拦截；
- 中文模式 → 进入组合态判定。

### 2. 同步初判（按键状态机）

字母键置为组合中；`Space` / `Enter` / 数字 / `Esc` / 标点置为结束；鼠标左/右键点击、前台窗口切换重置为结束。

### 3. 异步校验（UIA）

仅在状态机认为“组合中”且按键被吞后，用 UI Automation 读取焦点元素文本（300 ms 超时），判断末尾是否为拼音组合串：

- 确认组合中：保持拦截；
- 确认无组合：补发原按键（`Tab` 或 `;`），效果等同未拦截。

因此无组合时 `Tab` 是零延迟放行；仅在状态机误判时才会出现一次异步补发（延迟约数十毫秒）。

**终端类应用跳过 UIA 校验**：Windows Terminal 的 `TermControl`、传统控制台 `ConsoleWindowClass`、VS Code 终端 `xterm-helper-textarea` 的 UIA 文本是终端缓冲区而不是输入法组合串，直接判定会误报“无组合”并补发按键（表现为 Tab 仍触发必应提示）。识别到这些控件时直接返回 `Unknown`，完全信任按键状态机。

诊断日志：程序目录下的 `debug.log` 记录每次 Tab / 分号事件（时间、键盘布局、模式、组合状态、拦截结果、UIA 判定），上限 512 KB 自动轮转。

## 已知限制

1. **依赖系统托盘输入指示器**。如果任务栏隐藏了输入指示、或使用第三方输入法状态栏，`ReadChineseMode` 可能取不到值，此时回退为按中文模式处理（可能误拦截英文模式）。
2. **组合态判定存在边界**。终端类应用只能依赖状态机；自绘控件不支持 UIA 时同样依赖状态机。
3. **管理员权限窗口（UAC 提升）收不到钩子**，这是 Windows UIPI 限制，普通权限程序无法跨完整性级别拦截。
4. 全局键盘钩子可能被部分安全软件提示，属正常现象。
5. 仅验证于 Windows 11 build 26200（微软拼音、记事本与 Windows Terminal 宿主）。其他版本/输入法未验证。

## 实现要点

- `WH_KEYBOARD_LL` 全局钩子，回调内只做低延迟判断；
- 键盘布局：`GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow()))` 低 16 位等于 `0x0804`；
- 中/英模式：UIA 读取任务栏输入指示器名称，缓存元素、按需刷新（Shift 抬起、按键时）；
- 被吞按键的处理在后台线程执行：先做 UIA 校验，再按结果发送 `VK_SPACE` + Unicode `U+FF1B`，或补发原键；
- 实测：组合态下直接注入 Unicode 字符会被输入法吞掉，必须先上屏；
- 防重入：80 ms 内的重复分号 keydown 只处理一次；
- 自补发的按键通过 `dwExtraInfo` 标记（`0x1EA00001`）识别并放行；
- 单实例：命名互斥体 `ImeKeyGuard_SingleInstance_Mutex`。

## 编译

使用 .NET Framework 4.8 的 C# 5 编译器（无需 SDK）：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /out:ImeKeyGuard.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll" ^
  /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll" ^
  ImeKeyGuard.cs
```

## 故障排除

| 现象 | 处理 |
|---|---|
| 托盘无图标 | 进程未运行；运行 `install.bat` 或直接启动 exe |
| 英文模式下 Tab / 分号被吞 | 查看 `debug.log` 中 `mode=` 值；若为 1 但实际是英文模式，说明托盘指示器读取失败 |
| 组合中 Tab 仍插入制表符 | 查看日志中该次按键的 `comp` 值 |
| 组合中 Tab 仍弹出必应提示 | 若日志显示 `verifyTab state=NotComposing`，说明 UIA 误判并补发 |
| 分号多输出空格 | 组合态误判；托盘菜单关闭分号功能 |
| 管理员程序中无效 | 受 UIPI 限制 |
| 卸载后目录残留 | 等待后台延迟删除完成，或手动删除 |

## 文件清单

| 文件 | 说明 |
|---|---|
| `ImeKeyGuard.exe` | 主程序（托盘） |
| `ImeKeyGuard.cs` | 源码 |
| `install.bat` / `install.ps1` | 安装 |
| `uninstall.bat` / `uninstall.ps1` | 卸载 |
| `readme.md` | 本文档 |
| `debug.log` | 运行时诊断日志（自动生成，可删除） |

## 版本

- 2026-10-27：初版。Tab 拦截、分号上屏增强、托盘开关、开机自启、批处理安装/卸载。
- 2026-10-27：改为**仅组合态生效**；增加 UIA 校验与误判补发；增加鼠标点击/前台切换重置状态。
- 2026-10-27：修复终端类应用（`TermControl` 等）UIA 误判导致 Tab 被补发的问题；增加 `debug.log`。
- 2026-10-27：增加**中/英模式判定**（读取任务栏输入指示器），英文模式下完全放行。

