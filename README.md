# AI-GO · AI 双窗助手

Windows 桌面侧栏，用于切换、并排显示 ChatGPT 和 Claude，以及把文件拖入当前或新建的对话。把文件拖到并排图标，会加入两边当前的对话。文件队列支持逐项重试。

ChatGPT 一侧会识别 OpenAI 签名的桌面客户端：Codex 版和 ChatGPT 版都可以，哪个窗口在前就跟随哪个。套餐用量仍通过 Codex 客户端读取。

## 构建与运行

在 64 位 Windows 的 PowerShell 中，从仓库根目录运行以下命令。构建脚本使用系统的 .NET Framework 4.x 编译器、WPF 和 Windows 元数据。

```powershell
.\build.ps1
.\build\AISwitchDesk.exe
```

应用源码、XAML 界面和图标位于 `src/`。根目录的 `*Tests.cs` 和 `Verify.cs` 是回归检查，用 `test.ps1` 运行（见下）。`deploy.ps1` 用于更新当前 Windows 用户已安装的侧栏，并备份旧版本。

## 后台交接

后台进程的错误记录在 `%LOCALAPPDATA%\AI Switch Desk\background.log`（含完整堆栈，超过约 500 KB 时轮换为 `background.log.old`）。已完成或已取消的交接在闲置 30 天后连同文件副本自动清理；未完成和已暂停的交接不会被清理。

## 套餐用量

Claude 的额度只在你自己打开 Claude 的「设置 → 用量」页时读取，侧栏不会替你跳转页面。较早的读数会继续显示并标明读取时间；额度在重置前只减不增，所以旧读数是上限。

## 测试

```powershell
.\test.ps1 -FetchWebView2
```

脚本会先构建侧栏，再编译并运行全部回归程序，最后汇总 PASS / FAIL / SKIP。测试会打开真实窗口并拖放真实文件，运行期间请不要动鼠标和键盘。

- `-FetchWebView2`：从 nuget.org 下载新建聊天测试所需的 WebView2 包到 `build\packages`；不加时这部分测试会跳过。
- `-Only NativeDrop,Verify`：只跑名称匹配的测试。
- `-CompileOnly`：只编译不运行。`-SkipBuild`：测试已有的 `build\AISwitchDesk.exe`。
- `-Baseline <exe>`：`Verify` 用来对照的旧版本，默认是已安装的侧栏。

每次推送，GitHub Actions 会构建侧栏并编译测试（失败会拦截），然后在默认分支和当前分支上各跑一遍测试作为参考（不拦截，因为托管机器上的窗口测试不稳定）。

## 界面预览

```powershell
.\preview.ps1
```

用侧栏自己的绘制代码，把各种状态和六套主题渲染成图片，输出到 `build\preview`。不需要启动侧栏，改完界面可以先看图。加 `-Frames` 会把动效的每一帧也导出。

侧栏的绘制集中在 `src/AISwitchDesk/RailArt.cs`，只依赖 System.Drawing。应用图标由 `tools/make-artwork.py` 用 `compare-mark.png` 合成（需要 Python 和 Pillow）；侧栏收起后显示的也是这幅图。
