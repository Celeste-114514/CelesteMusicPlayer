import os, subprocess
env = dict(os.environ); env.update({"APPDATA": r"C:/Users/MSI/AppData/Roaming","LOCALAPPDATA": r"C:/Users/MSI/AppData/Local","ProgramData": r"C:/ProgramData","ALLUSERSPROFILE": r"C:/ProgramData","ProgramFiles": r"C:/Program Files","ProgramFiles(x86)": r"C:/Program Files (x86)","CommonProgramFiles": r"C:/Program Files/Common Files","CommonProgramW6432": r"C:/Program Files/Common Files","USERPROFILE": r"C:/Users/MSI","SystemRoot": r"C:/Windows","TEMP": r"C:/Users/MSI/AppData/Local/Temp","TMP": r"C:/Users/MSI/AppData/Local/Temp","DOTNET_ROOT": r"C:/Program Files/dotnet","DOTNET_CLI_TELEMETRY_OPTOUT":"1"})
rc = subprocess.call([r"C:/Program Files/dotnet/dotnet.exe","build","ThinHostPoC.csproj","-c","Release","-v","m"], env=env)
print("RC=", rc)
