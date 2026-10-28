# Hidden native popup regression

Run from the repository root: `dotnet run --project tools/NativeDesktopPopupRegression -c Release`.

Links the production Win32 HWND, tray menu, taskbar player, layout, whitelist and command adapter sources. Playback/session, DispatcherQueue timers and logging are test doubles. Windows remain hidden, no mouse or keyboard input is sent to any other process, and the Explorer-opening command is intercepted. The taskbar starts suspended so its real startup path cannot show a window or poll the desktop.

Exercises HWND messages, native paint, each tray row, Exit, disabled/unknown/separator guards, logical scale and compact-screen hit testing, keyboard navigation, taskbar transport/busy/restore, and GDI resource release. Renders the production drawing callbacks to PNG files under outputs for inspection. This is component integration coverage; it does not establish real Explorer/mouse, multi-monitor DPI transitions, or app-wide startup performance.
