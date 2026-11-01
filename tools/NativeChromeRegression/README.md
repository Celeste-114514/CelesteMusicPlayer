# Native chrome regression

This test uses the production NativeMainWebWindow and NativeWebViewPanel, without the playback engine, user music library, or app preferences.

The launcher creates a private Win32 desktop and starts the test child on it. It never switches desktops, injects mouse/keyboard input, or activates a window on the user's desktop.

Run from the repository root:

```powershell
dotnet build tools/NativeChromeRegression/NativeChromeRegression.csproj -c Release -p:Platform=x64
& tools/NativeChromeRegression/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/NativeChromeRegression.exe C:\path\to\test-artifacts
```

The checks cover native caption styles, the managed border model, client and browser HWND rectangles, maximized work-area bounds, restored/top-left resized bounds, and the actual negative child translation observed during the reported bug. Each stage captures a fixture page and checks the left/center/right header pixels.

Limit: this isolates native geometry and browser painting; it does not validate active-desktop DWM animations or physical drag-to-edge behavior. Those still need a later interactive check. A test timeout terminates only its own child process.
