# FUCK INDIAN WINDOWS

!!!!

# ImeKeyGuard

针对 Windows 微软拼音输入法的常驻按键守卫：

- 中文输入态下**吞掉 Tab**，不再展开候选或必应建议；
- 中文输入态下**接管分号 `;`**：先上屏第一个候选（发送 Space），再输出全角分号 `；`；
- 英文键盘（或非中文布局）下完全不接管，Tab 与 `;` 保持系统默认行为。

程序为普通用户权限的托盘程序（约 10 KB），通过 `WH_KEYBOARD_LL` 全局低级键盘钩子实现；不注入任何进程、不修改系统文件、不改注册表设置项（仅写入开机自启项）。

## 行为规则

仅在**中文输入法激活**（前台线程键盘布局低字为 `0x0804`）且未按 `Shift` / `Ctrl` / `Alt` 时生效：

| 场景 | 行为 |
| --- | --- |
| 中文态 `Tab` | 吞掉，无任何输入 |
| 中文态 `;`（正在拼音组合） | 吞掉 → 发送 `Space` 上屏第一个候选 → 输出 `；` |
| 中文态 `;`（无组合） | 吞掉 → 输出 `；` |
| 英文键盘 `Tab` | 正常制表符 |
| 英文键盘 `;` | 正常英文分号 |
| `Shift+;` / `Ctrl+;` / `Alt+;` | 放行，交给系统与输入法 |
| `Shift+Tab` | 放行 |

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
| --- | --- |
| `Enabled` | 总开关，取消勾选后完全不拦截 |
| `Semicolon: commit + fullwidth ;` | 单独关闭分号功能（Tab 拦截仍生效） |
| `Exit` | 退出程序 |

## 已知限制

1. **中文输入法的“英文模式”仍会被接管**。Shift 切换中英后键盘布局仍是 `0x0804`，在用户态无法与真正的中文态区分（微软拼音为 TSF-only，`ImmGetContext` 返回 `NULL`）。需要临时输入英文分号时，用托盘菜单关闭分号功能，或切换到美式键盘。
2. **组合态判定不是 100% 精确**。实现为「UIA 读取焦点元素文本（300 ms 超时）+ 按键状态机兜底」：
   - 应用不支持 UIA `TextPattern` / `ValuePattern` 且状态机漂移时，可能误判；
   - 误判表现为多插入一个空格（可关闭分号功能规避）。
3. **管理员权限窗口（UAC 提升）收不到钩子**，这是 Windows UIPI 限制，普通权限程序无法跨完整性级别拦截。
4. 全局键盘钩子可能被部分安全软件提示，属正常现象。
5. 仅验证于 Windows 11 build 26200（微软拼音、记事本宿主）。其他版本/输入法未验证。

## 实现要点

- `WH_KEYBOARD_LL` 全局钩子，回调内只做低延迟判断；
- 中文态：`GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow()))` 低 16 位等于 `0x0804`；
- 分号处理在后台线程执行：先判断组合态，再 `SendInput` 发送 `VK_SPACE` 与 Unicode `U+FF1B`；
  - 实测：组合态下直接注入 Unicode 字符会被输入法吞掉，必须先上屏；
  - 无组合时 Unicode 分号可直接插入；
- 防重入：80 ms 内的重复分号 keydown 只处理一次；
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
| --- | --- |
| 托盘无图标 | 进程未运行；运行 `install.bat` 或直接启动 exe |
| 中文态 Tab 仍插入制表符 | 前台窗口未使用中文布局（应用强制英文键盘），或程序已暂停 |
| 分号多输出空格 | 组合态误判；托盘菜单关闭分号功能，或反馈使用的应用 |
| 分号未变成 `；` | 当前不在中文输入法（布局非 `0x0804`） |
| 管理员程序中无效 | 受 UIPI 限制，需以管理员运行程序（本程序未提供该模式） |
| 卸载后目录残留 | 等待后台延迟删除完成，或手动删除 |

## 文件清单

| 文件 | 说明 |
| --- | --- |
| `ImeKeyGuard.exe` | 主程序（托盘） |
| `ImeKeyGuard.cs` | 源码 |
| `install.bat` / `install.ps1` | 安装 |
| `uninstall.bat` / `uninstall.ps1` | 卸载 |
| `readme.md` | 本文档 |

## 版本

- 2026-10-27：初版。Tab 拦截、分号上屏增强、托盘开关、开机自启、批处理安装/卸载。
