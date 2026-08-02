# WebView2 probe

Throwaway BepInEx plugin that answers, in a single game launch, whether the
official WebView2 managed wrapper can host a browser window inside EFT's Mono
runtime. This cannot be tested outside the game: SPT ships only the embedded
Mono runtime, no standalone `mono.exe`.

## Run

```powershell
dotnet build tools\WebViewProbe\WebViewProbe.csproj -c Release -t:Rebuild -p:DeployToSpt=true -p:SptRoot=C:\SPT
```

Start EFT and reach the main menu. Then collect:

- `%TEMP%\craftqueue-webview2-probe.txt` (survives a hard native crash)
- the `[WV2]` lines in `<SptRoot>\BepInEx\LogOutput.log`

Remove `<SptRoot>\BepInEx\plugins\CraftQueueWebViewProbe` afterwards.

## Result of run 1 (managed wrapper) - FAILED

`Microsoft.Web.WebView2.Core.dll` cannot be used under EFT's Mono. The probe
reached stage 5 (runtime 150.0.4078.105 detected, so assembly load and the
native loader are fine) and the process died inside stage 6, the first call
that makes Mono build COM callable wrappers - a hard native crash with no
managed exception and no log entry.

Cause: `CreateAsync` hands native code wrappers for the SDK's interfaces, and
Mono ignores the `_VtblGap` markers those interfaces use for inherited vtable
slots (84 of 331 types carry them), so native calls land on the wrong function.

The probe now uses `RawWebView.cs` instead: hand-built vtables in the slot
order taken from the official `WebView2.h`, and callbacks created with
`Marshal.GetFunctionPointerForDelegate`. No wrapper is involved, so the gap
defect cannot apply, and the managed assembly is not shipped at all - only the
native `WebView2Loader.dll`.

## Stages

| Stage | Meaning |
|---|---|
| 0 | environment info: apartment state of the Unity main thread, paths, bitness |
| 1 | EFT main window found via `EnumThreadWindows` + `UnityWndClass` |
| 2 | dedicated STA thread running |
| 3 | borderless owned popup window created |
| 4 | `WebView2Loader.dll` loaded from the plugin folder |
| 5 | `GetAvailableCoreWebView2BrowserVersionString` - runtime present and reachable |
| 6 | `CreateCoreWebView2EnvironmentWithOptions` called with a hand-built handler |
| 7 | environment callback fired - **native calls back into Mono correctly** |
| 8 | `CreateCoreWebView2Controller` requested |
| 9 | controller created |
| 10 | bounds and visibility set (v1 interface members only) |
| 11 | navigation requested |
| 12 | success, window shown |

A stage that stops without a `FAILED`/`TIMEOUT` line means the process died
inside that step - which is itself the answer for that stage.

Slot numbers used (from `WebView2.h`): `ICoreWebView2Environment[3]`
CreateCoreWebView2Controller; `ICoreWebView2Controller[4]` put_IsVisible,
`[6]` put_Bounds, `[24]` Close, `[25]` get_CoreWebView2; `ICoreWebView2[5]`
Navigate. Handler interfaces have a single `Invoke` at slot 3.
