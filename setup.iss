[Setup]
AppName=MP4 Merger and Upscaler
AppVersion=1.0
DefaultDirName={autopf}\Mp4Merger
DefaultGroupName=Mp4Merger
UninstallDisplayIcon={app}\Mp4Merger.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.\Installer
OutputBaseFilename=Mp4Merger_Setup
SetupIconFile=icon.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "ReleaseBuild\Mp4Merger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Mp4Merger"; Filename: "{app}\Mp4Merger.exe"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\Mp4Merger"; Filename: "{app}\Mp4Merger.exe"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
