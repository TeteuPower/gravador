; Instalador do Gravador (Inno Setup 6).
;
; Por usuario e sem elevacao: gravar audio e capturar a tela nao pedem privilegio nenhum, e um
; instalador que pede administrador nao passa em maquina de empresa sem chamado aberto.

#define AppNome "Gravador"
#define AppExe "Gravador.exe"
#define AppPublisher "TeteuPower"
#define AppURL "https://github.com/TeteuPower/gravador"
#ifndef AppVersao
  #define AppVersao "0.1.0"
#endif

[Setup]
AppId={{7A3C1E44-9B2D-4F58-A6E1-0C3D9B7F2A15}
AppName={#AppNome}
AppVersion={#AppVersao}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppNome}
DefaultGroupName={#AppNome}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=Gravador-Setup-{#AppVersao}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=src\Gravador.App\Assets\app.ico

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "atalhonadesktop"; Description: "Criar um atalho na area de trabalho"; GroupDescription: "Atalhos:"
Name: "iniciarcomwindows"; Description: "Iniciar o Gravador com o Windows (direto na bandeja)"; GroupDescription: "Inicializacao:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppNome}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppNome}"; Filename: "{app}\{#AppExe}"; Tasks: atalhonadesktop

[Registry]
; A chave Run, e nao uma tarefa agendada: e onde a pessoa procura quando quer desligar alguma
; coisa (aba Inicializar do Gerenciador de Tarefas). Um programa que grava audio comecando
; sozinho sem constar em lugar nenhum e exatamente o que nao se deve fazer.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Gravador"; ValueData: """{app}\{#AppExe}"" --minimizado"; \
  Flags: uninsdeletevalue; Tasks: iniciarcomwindows

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o Gravador"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Configuracao e login ficam em %APPDATA%\Gravador. As GRAVACOES nao: elas moram em
; Documentos\Gravador e sao do usuario — desinstalar o programa nao apaga o trabalho dele.
Type: filesandordirs; Name: "{userappdata}\Gravador"
