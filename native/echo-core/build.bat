@echo off
rem Celeste audio core (ECHO-derived) build.
rem Requires: Visual Studio 2022+ C++ tools (vcvars64). No CMake/FFmpeg needed.
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 (
    echo VCVARS_FAILED
    exit /b 1
)
if not exist build mkdir build
cl /nologo /LD /EHsc /std:c++17 /O2 /MD /W3 /MP /utf-8 /DNOMINMAX ^
  /I audio-host\src /I audio-engine ^
  audio-host\src\wasapi_exclusive.cpp ^
  audio-host\src\PcmRingAudioSource.cpp ^
  audio-host\src\celeste_bridge.cpp ^
  audio-engine\ChannelBalanceProcessor.cpp ^
  audio-engine\ConvolutionProcessor.cpp ^
  audio-engine\DspChain.cpp ^
  audio-engine\DspHeadroomProcessor.cpp ^
  audio-engine\EqProcessor.cpp ^
  audio-engine\LevelMeterProcessor.cpp ^
  audio-engine\PlaybackRateProcessor.cpp ^
  audio-engine\ReplayGainProcessor.cpp ^
  /Fobuild\ /Febuild\celeste_audio_core.dll ^
  /link shell32.lib ole32.lib avrt.lib winmm.lib advapi32.lib uuid.lib
set CL_RC=%errorlevel%
echo CL_RC=%CL_RC%
if %CL_RC%==0 (
    dir /b build\celeste_audio_core.dll && echo BUILD_OK || echo BUILD_NO_DLL
)
exit /b %CL_RC%
