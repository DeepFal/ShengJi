# 声记（ShengJi）

Windows 小工具：同时录制「电脑声音」和「麦克风」，实时混成一个文件（可选另存分轨）。
面向非技术用户：一个窗口、一个红按钮，关窗口收到托盘，托盘图标实时显示录音状态和电平。

## 目录

```
src/ShengJi/          程序源码（C# / WPF，.NET Framework 4.7.2，无第三方依赖）
  Interop/            Windows Core Audio、Media Foundation 等 COM / Win32 声明
  Audio/              采集（设备回环 / 按应用回环 / 麦克风）、时间轴对齐、混音引擎
  Recording/          录音器（暂停/停止精确定位、分段、分轨）、MP3/WAV 写入
  Core/               设置、路径、日志、快捷键、开机启动等
  UI/                 主窗口、迷你悬浮条、托盘图标、主题样式
  SelfTest.cs         自检（--selftest），验证音频链路
installer/            Inno Setup 安装脚本 + 简体中文语言文件
docs/                 随程序分发的「使用说明」和便携版标记文件
tools/make-icon.ps1   生成 app.ico
prototype/            早期 HTML 界面原型
build.ps1             一键打包：Release 编译 → 便携版 zip → 安装包
dist/                 打包产物
```

## 构建

需要：.NET SDK（任意 6+ 版本，可编译 net472）、Inno Setup 6.5+（仅打安装包需要）。
本机安装位置：SDK 在 `%LOCALAPPDATA%\Microsoft\dotnet`，Inno Setup 在 `%LOCALAPPDATA%\Programs\Inno Setup 6`（便携模式）。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0
```

产物：`dist\声记_v1.0.0_安装版.exe`、`dist\声记_v1.0.0_便携版.zip`。

## 关键设计

- **电脑声音三种来源**：系统默认播放设备（跟随切换）/ 所有播放设备 / 指定设备 / 按应用（Win10 2004+ 进程回环，
  按 exe 名记住，自动定位同名进程树的根进程，应用重启后自动重连）。
- **统一时间轴**：所有采集源按 QPC 时间戳（不可信时用到达时间）写入各自的环形缓冲；混音线程以固定 250ms 延迟
  顺序读取。没有数据的地方就是静音，所以“没声音时不出包”的回环设备也能对齐。
- **时钟漂移补偿**：对每路采集用平滑后的时间偏差微调重采样比例（±0.3% 以内，Hermite 插值），偏差超过 120ms 才硬对齐。
- **设备变化**：调度线程每秒（以及收到设备通知时）重新计算需要的采集源；指定设备断开时自动退回默认设备，恢复后切回；
  采集出错按 0.5s→10s 退避重试；混音线程有看门狗。
- **防丢数据**：MP3 由系统 MP3 编码器 MFT 编码，帧由我们自己写文件，每秒交给系统、每 10 秒强制落盘；
  WAV 每次刷新回写文件头；WAV 每 5 小时自动换文件（4GB 限制）。没有 MP3 编码器的系统自动改用 WAV。
- **便携版**：exe 同目录有 `portable.txt` 时，设置和日志放在旁边的 `Data` 文件夹；否则放在 `%APPDATA%\ShengJi`。

## 调试参数

- `--selftest <目录> [--tone]`：自检，结果写入 `<目录>\selftest.txt`；`--tone` 会播放约 2.5 秒小音量提示音来验证回环。
- `--ui-shot <目录>`：把各界面状态渲染成 PNG 后退出（不保存设置）。
- `--minimized`：启动时只显示托盘图标（开机自启使用）。
