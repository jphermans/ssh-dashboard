; SSH Dashboard — Windows installer
; Built manually through .github/workflows/windows-installer.yml.
#ifndef MyAppVersion
  #define MyAppVersion "2.1.4"
#endif

#define MyAppName "SSH Dashboard"
#define MyAppPublisher "JPHsystems"
#define MyAppURL "https://github.com/jphermans/ssh-dashboard"
#define MyAppExeName "SshDashboard.exe"

[Setup]
AppId={{B6FEB17A-38A3-4802-8A69-739DB2EADE53}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoCopyright=© 2026 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=SSH-Dashboard-{#MyAppVersion}-Setup
SetupIconFile=assets\setup.ico
UninstallDisplayIcon={app}\SSH Dashboard.ico
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=yes
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
AppMutex=SshDashboard.SingleInstance
AllowNoIcons=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "..\dotnet\dist\win\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\setup.ico"; DestDir: "{app}"; DestName: "SSH Dashboard.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\SSH Dashboard"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\SSH Dashboard.ico"
Name: "{group}\Uninstall SSH Dashboard"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SSH Dashboard"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\SSH Dashboard.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch SSH Dashboard"; Flags: nowait postinstall skipifsilent

[Messages]
SetupAppTitle=Setup - {#MyAppName}
SetupWindowTitle=Setup - {#MyAppName} {#MyAppVersion}
WelcomeLabel1=Welcome to {#MyAppName}
WelcomeLabel2=SSH Dashboard gives you a clear, careful view of your SSH hosts, configuration and keys.%n%nThis wizard installs the app for your Windows account. Your existing .ssh folder is not modified.
FinishedHeadingLabel={#MyAppName} is ready
FinishedLabel=Setup has finished installing {#MyAppName} on your computer.%n%nYour SSH files stay where they are — the app only reads or edits them when you ask it to.
ClickFinish=Finish
