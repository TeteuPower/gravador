; Instalador do Gravador (Inno Setup 6).
;
; Por usuario e sem elevacao: gravar audio e capturar a tela nao pedem privilegio nenhum, e um
; instalador que pede administrador nao passa em maquina de empresa sem chamado aberto.

#define AppNome "Gravador"
#define AppExe "Gravador.exe"
#define AppCli "gravador-cli.exe"
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
AppUpdatesURL={#AppURL}/releases
; O app compara esta versao com a das releases para se atualizar sozinho; sem ela, a versao que o
; Windows mostra nas Propriedades do instalador fica em branco.
VersionInfoVersion={#AppVersao}
UninstallDisplayName={#AppNome}
DefaultDirName={autopf}\{#AppNome}
DefaultGroupName={#AppNome}
DisableProgramGroupPage=yes
; Numa atualizacao, a pasta e as opcoes escolhidas antes sao reaproveitadas sem perguntar de novo —
; e o app instala em silencio, sem ninguem para responder.
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
; Quem fecha o que esta rodando e o codigo la embaixo, nao o Restart Manager: a janela costuma
; estar oculta na bandeja, e o Restart Manager so sabe pedir para fechar janelas.
CloseApplications=no
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
; Atualizacao feita pelo proprio app: ele foi fechado para a troca dos arquivos, entao quem o
; devolve e o instalador. Sem esta linha, atualizar equivale a fechar o programa.
;
; Com a JANELA aberta, e nao na bandeja. Quem atualiza acabou de clicar num botao dentro da janela;
; devolver o programa escondido faz parecer que ele nao voltou. (A linha veio do claude-indicator,
; que e um indicador de bandeja e nao tem janela para voltar.)
Filename: "{app}\{#AppExe}"; Parameters: "--apos-atualizar"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; Configuracao e login ficam em %APPDATA%\Gravador. As GRAVACOES nao: elas moram em
; Documentos\Gravador e sao do usuario — desinstalar o programa nao apaga o trabalho dele.
Type: filesandordirs; Name: "{userappdata}\Gravador"

[Code]
const
  ChaveDeDesinstalacao =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A3C1E44-9B2D-4F58-A6E1-0C3D9B7F2A15}_is1';

var
  EhAtualizacao: Boolean;

{ Fecha o que estiver rodando: sem isso os arquivos ficam em uso e nao podem ser trocados.

  Os DOIS executaveis. O gravador-cli sobe como servidor MCP durante uma conversa com o Claude e
  pode ficar de pe depois dela; ele divide a mesma pasta e o mesmo runtime .NET do Gravador.exe,
  entao um cli esquecido tranca a instalacao inteira.

  As preferencias e o login ficam em %APPDATA% e sao gravados na hora em que mudam; as gravacoes,
  em Documentos. Nada se perde no fechamento. O app so oferece atualizar quando nao ha gravacao em
  andamento, justamente porque aqui o fechamento e a forca. }
procedure FecharOQueEstiverRodando;
var
  Codigo: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '',
       SW_HIDE, ewWaitUntilTerminated, Codigo);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppCli} /F', '',
       SW_HIDE, ewWaitUntilTerminated, Codigo);
  { o Windows leva um instante para soltar os arquivos depois que o processo morre }
  Sleep(1200);
end;

function InitializeSetup(): Boolean;
var
  Anterior: String;
begin
  EhAtualizacao := RegQueryStringValue(HKA, ChaveDeDesinstalacao, 'UninstallString', Anterior);
  Result := True;
end;

{ Atualizacao nao pergunta atalhos nem pede confirmacao de novo: as respostas de antes valem. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := EhAtualizacao and ((PageID = wpSelectTasks) or (PageID = wpReady));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  FecharOQueEstiverRodando;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  FecharOQueEstiverRodando;
  Result := True;
end;
