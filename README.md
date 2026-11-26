# wpf-uia

面向 Windows 桌面应用的 stdio MCP 服务，使用 ModelContextProtocol C# SDK 和 FlaUI.UIA3。工具为 `launch_or_attach`、`get_ui_tree`、`click_element`、`set_text`、`wait_for_element`、`get_element_state`、`wait_for_window`。

## 构建与运行

构建需要 .NET 8 SDK 或更高版本。运行需要 Windows、对应架构的 .NET 8 Desktop Runtime 和交互式桌面会话。默认构建为 x64。服务启动后会核验目标进程位数。

```powershell
dotnet build WpfAutomationMcp.csproj -c Release -p:RestoreLockedMode=true --source https://api.nuget.org/v3/index.json
# 为 32 位目标进程构建
dotnet build WpfAutomationMcp.csproj -c Release -r win-x86 --source https://api.nuget.org/v3/index.json
```

x64 可执行文件位于 `bin/Release/net8.0-windows/win-x64/WpfAutomationMcp.exe`，x86 版本位于对应的 `win-x86` 目录。将可执行文件的绝对路径配置为 MCP 客户端的 stdio 服务命令，从 `launch_or_attach` 开始定位目标进程。

## 行为约定

- stdio 仅传输 MCP JSON-RPC，诊断写 stderr。工具返回 JSON 文本 `{ok, elapsedMs, result}`；错误为 `ok=false`，调用方必须检查，`elapsedMs` 是 worker 内的实际耗时。
- MCP 保留一个私有 MTA UIA 工作进程，调用串行化。UIA COM连接/事务超时2秒，外层有10秒硬截止（启动35秒、等待最多35秒）。已派发请求超时或取消时，服务会终止私有 worker，目标应用继续运行。重新附加后先读取状态，确认动作是否已执行。
- `launch_or_attach` 按真实 exe 路径和进程名精确附加；多实例报歧义。`startedByServer` 用于调用方区分本次启动与用户原有实例。MCP 退出后目标应用继续运行。测试脚本按进程身份清理本次启动的实例。
- `wait_for_window(title, timeoutMs=5000)` 仅在附加 PID 中等待精确原生标题、可见且启用的顶层窗口，每 50 ms 重新枚举，多个匹配报歧义。返回句柄、标题、PID、`polls` 和 `readiness="native-visible-enabled"`，不会激活窗口、读取 UIA 属性或遍历控件树。`polls` 是正常等待次数，不是失败重试。窗口可见不代表 UIA 控件就绪，下一步应查询唯一目标控件并检查状态。需要检查内容的场景仍使用 `get_ui_tree`；该工具不修复 `wait_for_element` 自身的切窗异常。
- 每次查询重新定位当前启用窗口，优先目标进程模态窗口。定位字段 AutomationId、Name、ControlType 均为精确 AND 条件；拒绝无 ID/Name 或多个匹配。不会用“第一个”控件猜测。
- 树使用 ControlView 和属性缓存，输出扁平节点 `{AutomationId,Name,ControlType,IsEnabled,Value}`。过滤装饰节点时仍遍历其子节点。默认深度4、最多深度12，最多访问600节点、返回120节点、节点JSON16000字符、遍历5秒；到达任何边界标记 `truncated`。Value/Name会限长，密码字段脱敏。
- 单次读树复用不可变的原生缓存请求，缓存显示属性和 Pattern 可用性标记，避免逐节点取回完整 Pattern 对象。单控件状态及动作仍使用原来的 Pattern 实现。`result.timing` 按毫秒累计本次请求所有尝试的 `rootResolveMs`（定位根）、`readMs`（读取树）和 `retryDelayMs`（重试休眠）；`rootCacheMs`、`navigateMs`、`projectMs` 是 `readMs` 内的细分，不能重复相加。输出限额、遍历深度、顺序与切窗重试规则保持不变，`truncated` 树不能证明某个未返回控件不存在。
- `get_ui_tree` 遇到暂时无启用窗口、元素失效或取得候选根超时会重新枚举。取得根的 UIA 连接及事务超时设置为 250 ms，后续读树恢复常规设置。默认从首次失败起在 1.2 秒内发起重试，若取得根超时则延长为 5 秒，仍受外层 10 秒截止约束。成功的 `result.stability` 和重试耗尽时的顶层 `stability` 提供 `attempts`、`noEnabledWindow`、`elementUnavailable`、`rootTimeouts`。重试耗尽返回 `TreeTransitionException`，无控件树。
- 点击使用 Invoke、Toggle、SelectionItem 或 ExpandCollapse。控件需要通过 AutomationPeer 提供对应 Pattern。
- 写入要求可写 ValuePattern。`pressEnter` 默认 false。开启时先校验前台，再主动聚焦定位到的控件，核验后写入，并在发送 Enter 前再次校验。
- 表格按 GridPattern 返回总行列和最多10×8单元格快照。虚拟化未实现的格子不能视为业务空值；不提供按序号编辑业务行。

## 键盘诊断与恢复

`set_text(pressEnter=true)` 在成功时返回 `result.keyboard`，在错误时返回顶层 `keyboard`。原有工具参数、`ok`、`result.state`、`result.enterSent` 及错误响应字段继续使用。

| 字段 | 含义 |
| --- | --- |
| `operationId`、`operation`、`phase`、`keys` | 单次操作标识、操作名、返回时所处阶段和计划发送的按键。 |
| `expected.pid`、`expected.hwnd` | 附加的预期 PID 和定位控件时选定的窗口句柄。 |
| `checks` | 每项记录 `status`、`expected`、`actual`、`reason` 和可选读取错误类型 `readError`。状态为 `passed`、`failed`、`not_checked`、`not_applicable`。读取失败时实际值缺失，原因另列。 |
| `checkPhase`、`blockingCheck`、`failureReason` | 最近采样阶段、第一个拦截检查的键和操作失败原因。一次采样可能报告多项失败。等待耗尽时根据 `blockingCheck` 查找对应检查的 `reason`。 |
| `waitLimitMs`、`checkIntervalMs`、`waitElapsedMs`、`checkCount` | MCP 在主动聚焦前和写值后各有 350 ms 发起轮询的预算，失败采样间最多休眠 50 ms。耗时累计两个等待阶段，包含各自的探测耗时。次数包含等待期间、写值前及最终发送前实际开始的采样。单次 UIA 调用可能跨过轮询预算，仍受 worker 10 秒总截止约束。 |
| `diagnosticsAvailable`、`diagnosticsMissingReason` | 收到 worker 报告时为 true。外层未取得报告且工具仍返回响应时为 false，并提供缺失原因。诊断缺失时不填推测的检查结果。 |
| `textWriteStatus` | `not_started` 表示尚未调用写值，`written` 表示 ValuePattern 正常返回，`unknown` 表示无法确定写值结果。 |
| `sendStatus` | `not_sent` 表示有证据证明未进入发键阶段。`sent` 表示计划内的按下和释放 API 完整正常返回。`unknown` 表示发送可能部分或全部发生，常见于发送异常或请求发出后丢失 worker 响应。 |

MCP 通过 `foregroundWindow`、`foregroundPid` 记录前台句柄和 PID，并通过 `uiaFocus`、`focusPid`、`focusAutomationId`、`focusControlType` 记录实际焦点。`targetControl` 使用 UIA 元素身份比较，`targetKeyboardFocus` 读取目标的焦点属性，`windowScope` 检查真实窗口祖先。目标为模态窗口时，`modalScope` 校验已定位的模态窗口。MCP 的 `documentScope` 为 `not_applicable`，因为工具参数未提供文档上下文。

常见原因码包括 `foreground_window_mismatch`、`foreground_window_changed`、`foreground_pid_mismatch`、`uia_focus_unavailable`、`focus_pid_mismatch`、`target_control_mismatch`、`target_keyboard_focus_mismatch`、`window_scope_mismatch`、`modal_scope_mismatch`、`focus_wait_timeout`。读取失败使用对应的 `*_unavailable` 原因码。外层错误区分 `worker_timeout`、`worker_cancelled`、`worker_pipe_failure`。操作异常还有 `target_resolution_failed`、`set_focus_exception`、`text_write_exception`、`focus_check_exception`、`send_exception`、`readback_exception`。

诊断对象只包含焦点和发送元数据。文本、控件值及 UIA Name 不进入该对象。既有工具状态字段继续执行密码脱敏。发送前完成独立最终采样并重读前台，焦点仍可能在采样和输入 API 之间改变。对 `sent` 必须读回页面来确认目标是否收到、业务是否提交。

**取消后 MCP SDK 可能不回包。** worker 层可生成 `worker_cancelled` 诊断，客户端仍可能收不到对应的 stdio 响应。若取消后无回包，调用方应将写入和发键状态按 `unknown` 处理，即使操作看起来已经停下。遇到无回包取消、超时、管道错误或 `unknown`，如有需要先重新附加，再只读核对项目、当前窗口、字段、选中项或保存状态，根据读回结果决定如何恢复。创建、保存、提交等可能重复生效的动作禁止自动重放。若已收到 `textWriteStatus=written` 且 `sendStatus=not_sent` 的响应，按已写值、未发送 Enter 的现场核对。

## 验证

使用 Python 3.10 或更高版本运行测试脚本。请在 Windows 交互式桌面上使用独立的 WPF Fixture，每个测试使用新的输出目录。

```powershell
dotnet build tests/Fixture/Fixture.csproj -c Release
$server = 'bin/Release/net8.0-windows/win-x64/WpfAutomationMcp.exe'
$fixture = 'tests/Fixture/bin/Release/net8.0-windows/win-x64/Wpf.Automation.Fixture.exe'
python tests/integration_test.py --server $server --fixture $fixture --output artifacts/integration
python tests/keyboard_diagnostics_test.py --server $server --fixture $fixture --output artifacts/keyboard
```

键盘测试由 Fixture 独立记录 Enter 接收、实际提交和写值时的焦点变化。默认测试覆盖成功提交、前台窗口不符，以及写值后焦点同步切换时拦截 Enter。异常、超时和管道中断测试需要将预先构建的故障替身作为 `--server` 传入，并选择对应的 `--variant throw`、`timeout` 或 `pipe`。

对自己的目标应用做只读冒烟测试时，传入可执行文件路径和已核验的主窗标识。输出目录用于保存协议、状态和截图。

```powershell
$app = './path/to/YourApp.exe'
$mainId = 'YourMainWindow'
python scripts/smoke_test.py --server $server --app $app --output artifacts/smoke --expected-main-id $mainId
```

参考 [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html) 和 [FlaUI](https://github.com/FlaUI/FlaUI)。
