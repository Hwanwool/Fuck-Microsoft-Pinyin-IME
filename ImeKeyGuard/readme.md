# ImeKeyGuard

针对 Windows 微软拼音输入法的常驻按键守卫：

- **仅当待选框打开（正在拼音组合）时**拦截：
  - `Tab` 被吞掉，不再展开候选或必应建议；
  - `;` 被接管：先上屏第一个候选（发送 Space），再输出全角分号 `；`；
- **待选框未打开（无组合）时不生效**：Tab 正常插入制表符，`;` 交给输入法自己处理；
- 英文键盘下完全不接管。

程序为普通用户权限的托盘程序（约 13 KB），通过 `WH_KEYBOARD_LL` 全局低级键盘钩子实现；不注入任何进程、不修改系统文件、不改输入法设置项（仅写入开机自启项）。

## 行为规则

仅在**中文输入法激活**（前台线程键盘布局低字为 `0x0804`）且未按 `Shift` / `Ctrl` / `Alt` 时生效：

| 状态 | `Tab` | `;` |
|---|---|---|
| 中文态、待选框打开（组合中） | 吞掉，无任何输入 | 吞掉 → `Space` 上屏第一个候选 → 输出 `；` |
| 中文态、待选框未打开（无组合） | 正常制表符 | 放行，由输入法处理（通常输出 `；`） |
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

## 组合态判定

微软拼音是 TSF-only 输入法，用户态没有可靠的组合态 API（实测：`ImmGetContext` 返回 `NULL`；候选窗的 DWM cloak 状态恒定不变）。实现采用两层判定：

1. **同步初判（按键状态机）**：字母键置为组合中；`Space` / `Enter` / 数字 / `Esc` / 标点置为结束；鼠标左/右键点击、前台窗口切换重置为结束。
2. **异步校验（UIA）**：仅在状态机认为“组合中”且按键被吞后，用 UI Automation 读取焦点元素文本（300 ms 超时），判断末尾是否为拼音组合串：
   - 确认组合中：保持拦截；
   - 确认无组合：补发原按键（`Tab` 或 `;`），效果等同未拦截。

因此无组合时 `Tab` 是零延迟放行；仅在状态机误判时才会出现一次异步补发（延迟约数十毫秒）。

**终端类应用跳过 UIA 校验**：Windows Terminal 的 `TermControl`、传统控制台 `ConsoleWindowClass`、VS Code 终端 `xterm-helper-textarea` 的 UIA 文本是终端缓冲区而不是输入法组合串，直接判定会误报“无组合”并补发按键（表现为 Tab 仍触发必应提示）。识别到这些控件时直接返回 `Unknown`，完全信任按键状态机。

诊断日志：程序目录下的 `debug.log` 记录每次 Tab / 分号事件（时间、键盘布局、组合状态、拦截结果、UIA 判定），上限 512 KB 自动轮转。排查问题时可直接查看。

## 已知限制

1. **中文输入法的“英文模式”仍会被判定为中文**。Shift 切换中英后键盘布局仍是 `0x0804`，用户态无法区分。需要临时输入英文分号时，用托盘菜单关闭分号功能，或切换到美式键盘。
2. **组合态判定存在边界**。终端类应用只能依赖状态机；若在终端中先输入字母再按 Esc 取消组合，状态机会正确复位，但用鼠标点击终端后状态同样复位，均属预期。自绘控件不支持 UIA 时同样依赖状态机。
3. **管理员权限窗口（UAC 提升）收不到钩子**，这是 Windows UIPI 限制，普通权限程序无法跨完整性级别拦截。
4. 全局键盘钩子可能被部分安全软件提示，属正常现象。
5. 仅验证于 Windows 11 build 26200（微软拼音、记事本与 Windows Terminal 宿主）。其他版本/输入法未验证。

## 实现要点

- `WH_KEYBOARD_LL` 全局钩子，回调内只做低延迟判断；
- 中文态：`GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow()))` 低 16 位等于 `0x0804`；
- 被吞按键的处理在后台线程执行：先做 UIA 校验，再按结果发送 `VK_SPACE` + Unicode `U+FF1B`，或补发原键；
- 实测：组合态下直接注入 Unicode 字符会被输入法吞掉，必须先上屏；
- 防重入：80 ms 内的重复分号 keydown 只处理一次；
- 自补发的按键通过 `dwExtraInfo` 标记（`0x1EA00001`）识别并放行，避免二次拦截；
- 单实例：命名互斥体 `ImeKeyGuard_SingleInstance_Mutex`。

## 编译

使用 .NET Framework 4.8 的 C# 5 编译器（无需 SDK）：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /platform:x64 /optimize+ ^
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
| 组合中 Tab 仍插入制表符 | 状态机未识别为组合中；查看 `debug.log` 中该次按键的 `comp` 值 |
| 组合中 Tab 仍弹出必应提示 | 若日志显示 `verifyTab state=NotComposing`，说明 UIA 误判并补发；确认焦点控件是否被识别为终端 |
| 无组合 Tab 被吞 | 状态机误判，会在数十毫秒内补发；若频繁出现请记录应用 |
| 分号多输出空格 | 组合态误判；托盘菜单关闭分号功能 |
| 分号未变成 `；` | 当前不在中文输入法（布局非 `0x0804`），或日志显示 `cn=False` |
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
- 2026-10-27：修复终端类应用（`TermControl` 等）UIA 误判导致 Tab 被补发、进而触发必应提示的问题；增加 `debug.log` 诊断日志。

