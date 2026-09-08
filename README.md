# Gravador

Ferramenta para Windows que grava **o áudio do computador e o seu microfone ao mesmo tempo**, em
trilhas separadas, e junta a isso as telas que você capturou durante a reunião. O produto é uma
pasta pronta para arrastar inteira para dentro de uma IA e pedir a transcrição, a ata ou a análise.

Motivação: quem grava reunião pelo próprio Teams ou Meet depende de o organizador permitir, recebe
um vídeo pesado e não tem nada acionável no fim. Gravar pelo Windows resolve o acesso, mas o
Gravador de Voz só pega o microfone — o que os outros falam não entra. E nenhum deles responde à
pergunta que importa depois: **"o que eu falei ali os outros ouviram, ou eu estava mudo?"**

## Stack

| Camada | Escolha | Por quê |
|---|---|---|
| Captura de áudio | **C# / .NET 10** com WASAPI via NAudio (loopback + captura) | O loopback do WASAPI é a única forma de pegar "o que está tocando" sem driver virtual. Em C# o interop com o áudio do Windows é direto e o custo é irrelevante perto do trabalho do próprio kernel. |
| Interface | **WPF**, bandeja, tema escuro | O requisito era rodar em máquina modesta ao lado de uma chamada de vídeo. WPF sobre .NET ocupa uma fração de um Chromium e não disputa CPU com a reunião. Electron foi recusado por isso — ver [a conta medida](#o-que-isso-custa-medido). |
| Compressão | **Media Foundation** (codificador de MP3 do próprio Windows) | Uma reunião de duas horas sai com 57 MB em vez dos 690 MB do WAV, **sem carregar ffmpeg junto** (~80 MB de binário para o mesmo resultado em voz). |
| Integração | **CLI + IPC por JSON linha a linha** | O mesmo contrato do motor de varredura do Limpador: quando estas ferramentas forem agrupadas, quem estiver por cima fala com todas do mesmo jeito. |
| IA | **Claude**, pelo `claude` em modo não interativo | Usa a assinatura já conectada na máquina. Ver [por que o Claude não transcreve](#por-que-o-claude-não-faz-a-transcrição). |

Detalhes das decisões que o código não explica sozinho em [docs/arquitetura.md](docs/arquitetura.md).

## Como usar

Pré-requisito: Windows 10/11. Para compilar, o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# compilar e abrir
.\iniciar.cmd

# ou, à mão
dotnet build -c Release
.\src\Gravador.App\bin\Release\net10.0-windows\Gravador.exe
```

O Gravador fica na bandeja. Os atalhos funcionam com qualquer programa na frente — é isso que
permite marcar e capturar sem sair da reunião:

| Atalho | O que faz |
|---|---|
| `Ctrl+Alt+G` | começa / para e salva |
| `Ctrl+Alt+Espaço` | pausa / retoma (o tempo pausado não entra no arquivo) |
| `Ctrl+Alt+P` | captura a tela e prende no instante do áudio |
| `Ctrl+Alt+N` | marca "isto aqui importa" |

Todos são parametrizáveis em Configurações — clique no campo e aperte a combinação.

## O que uma sessão deixa

Cada gravação vira uma pasta em `Documentos\Gravador`, auto-suficiente:

```
2026-09-08_14-30-00/
├── sistema.mp3       o que os outros falaram
├── microfone.mp3     o que você falou
├── mixado.mp3        os dois juntos, como a reunião soou
├── capturas/
│   ├── 001_04m12s.jpg
│   └── 002_18m47s.jpg
├── transcricao.md    se algum motor de transcrição estiver ligado
├── resumo.md         se você pedir ao Claude
└── sessao.json       a linha do tempo que amarra tudo
```

**Por que trilhas separadas.** É o que mais melhora o resultado da IA: com a sua voz em um arquivo e
a reunião em outro, ela sabe quem falou o quê sem precisar adivinhar por timbre. A mixada existe
para você ouvir; as separadas, para a máquina ler. Dá para escolher só uma das duas.

O `sessao.json` guarda tudo em tempo **relativo ao início** — "aos 14:32" é uma instrução que dá
para seguir com o arquivo na mão; "às 15h47 de terça" não é.

## O microfone mudo

Esta é a parte que os outros gravadores não fazem. Existem **dois mudos diferentes** no Windows, e
eles têm confiabilidade muito diferente:

1. **O do dispositivo.** O mudo do endpoint de captura, no Windows. É fato verificável: com ele
   ligado, nenhum aplicativo recebe áudio nenhum seu.

2. **O do aplicativo.** O botão de mudo do Teams, do Meet, do Zoom. **Este não existe do lado do
   Windows**: o aplicativo continua lendo o microfone normalmente e apenas deixa de mandar o seu
   áudio para os outros. Nenhuma API do sistema conta isso — e é justamente o que você quer saber
   ao rever a gravação.

Para o primeiro, o Gravador lê o endpoint e trata como certeza. Para o segundo, ele **observa o
atalho de mudo do aplicativo que está em primeiro plano** (Teams `Ctrl+Shift+M`, Meet `Ctrl+D`,
Zoom `Alt+A`, e mais) e trata o resultado pelo que ele é: um palpite — marcado como palpite na
linha do tempo, pintado de amarelo em vez de vermelho na tela, e corrigível por você a qualquer
momento pelo botão ou pelo menu da bandeja.

O palpite erra em dois casos conhecidos: quando você se muta pelo **clique no botão** em vez do
atalho, e quando **trocou o atalho** nas preferências do aplicativo. Por isso a correção manual
existe e vale mais que a detecção.

O gancho de teclado é **observador puro**: devolve toda tecla para a fila, sempre. Se ele engolisse
o `Ctrl+Shift+M`, o Teams deixaria de receber o próprio atalho de mudo — a ferramenta anotaria
"mudo" e você seguiria falando para a reunião inteira.

Você escolhe o que fazer com o áudio dos trechos mudos:

- **Guardar na sua trilha, silenciar na mistura** (padrão) — o arquivo misturado vira o que a
  reunião ouviu, e nada do que você disse se perde.
- **Gravar tudo e só anotar** na linha do tempo.
- **Não gravar nada** do que for dito no mudo.

## Transcrição

Três caminhos, do mais leve ao mais preciso:

| Motor | Onde roda | Qualidade | Custo |
|---|---|---|---|
| **Nenhum** (padrão) | — | você manda o áudio para a IA que preferir | zero |
| **Windows** | nesta máquina, offline, ao vivo | fraca: foi feita para comando de voz, e entende uma voz por vez | zero |
| **Remoto** | serviço compatível com a API de áudio da OpenAI | a melhor em português | por minuto de áudio |

O motor do Windows exige o pacote de fala do idioma instalado, o que **não é o padrão no Windows em
português** — a interface diz isso em vez de simplesmente não transcrever nada.

O caminho remoto roda quando a gravação termina e fatia o áudio em pedaços de dez minutos, porque
o limite de upload desses serviços costuma ser 25 MB e uma reunião de duas horas passa disso. A
chave nunca entra no arquivo de configuração: o que fica guardado é o **nome da variável de
ambiente** que a contém.

## O Claude

O login da conta Claude é feito dentro da ferramenta (OAuth com PKCE, o mesmo fluxo do
`claude setup-token`), e o token fica só nesta máquina, protegido pelo DPAPI. Se você já usa o
Claude Code neste computador, ele é aproveitado sem nenhum login novo.

### Por que o Claude não faz a transcrição

Os modelos do Claude leem texto, imagem e PDF — **não leem áudio**. Mandar o som para ele não é
possível, e prometer isso na interface seria mentira.

O que ele faz é o passo seguinte, e é onde ele ganha de um transcritor: lê a transcrição junto com
as telas que você capturou e a linha do tempo — inclusive os trechos em que você estava mudo — e
devolve o que ficou decidido, o que ficou pendente e o que vale voltar a ouvir. Sem transcrição, o
pedido muda de forma em vez de deixar de existir: ele analisa as imagens e a linha do tempo, e o
resumo diz claramente que foi feito só com isso.

A instrução do resumo é editável em Conta Claude.

## Linha de comando e integração

O mesmo motor, sem janela:

```powershell
gravador-cli dispositivos           # lista os endpoints de áudio
gravador-cli gravar --segundos 60   # grava e converte
gravador-cli sessoes                # lista as gravações
gravador-cli resumir <pasta>        # pede o resumo ao Claude
gravador-cli entrar                 # login da conta Claude
gravador-cli ipc                    # modo de integração
```

O nome tem hífen por um motivo prático: o Windows não distingue maiúsculas, então `gravador.exe`
seria o mesmo arquivo que o `Gravador.exe` da janela. Com nomes distintos os dois moram na mesma
pasta e **compartilham o runtime** — 157 MB instalados em vez de 264 MB.

O modo `ipc` fala JSON linha a linha no stdin/stdout — o mesmo contrato do motor de varredura do
Limpador, para as ferramentas do conjunto conversarem sem aprender um protocolo por ferramenta:

```
> {"id":1,"cmd":"iniciar","titulo":"Reunião de terça"}
< {"id":1,"type":"iniciada","pasta":"...","sessao":"2026-09-08_14-30-00"}
< {"type":"niveis","sistema":0.31,"microfone":0.08,"vocefalando":true,"segundos":12.4}
> {"cmd":"capturar"}
< {"type":"capturada","ok":true,"arquivo":"...","janela":"Reunião | Microsoft Teams"}
> {"id":2,"cmd":"parar"}
< {"id":2,"type":"resultado","arquivos":["sistema.mp3","microfone.mp3","mixado.mp3"],...}
```

## O que isso custa (medido)

Nesta máquina (i7-14700HX, Windows 11), gravando duas trilhas a 48 kHz e mixando:

| | Memória (privada) | CPU |
|---|---:|---:|
| Motor sozinho (`gravador-cli ipc`) | **12 MB** | **~1% de um núcleo** |
| Aplicação na bandeja, sem janela | 155 MB | ocioso |
| Aplicação com a janela aberta | 194 MB | ocioso |
| *(referência)* app WPF **vazio** | 164 MB | — |

A leitura que importa: o código do Gravador soma cerca de **30 MB sobre o piso do WPF**, e a
gravação em si custa **1% de um núcleo**. Ajustar o GC (`gen0size`, `conserveMemory`, `retainVM`)
não mudou nada — a memória é reserva do runtime, não do código. Quem quiser o mínimo absoluto usa
o modo linha de comando, que não carrega interface nenhuma.

Tamanho em disco, por hora de reunião: **~28 MB por trilha** em MP3 a 64 kbps (~84 MB com as três),
contra 345 MB por trilha em WAV.

## Compilar

```powershell
dotnet build -c Release

# pacote self-contained (não depende do .NET instalado); os dois exes dividem o runtime
dotnet publish src/Gravador.App -c Release -r win-x64 --self-contained -o publish
dotnet publish src/Gravador.Cli -c Release -r win-x64 --self-contained -o publish

# conferir a interface sem olhar para a tela (desenha as abas em PNG)
.\src\Gravador.App\bin\Release\net10.0-windows\Gravador.exe --render C:\temp\telas
```

O modo `--render` existe porque a interface precisa ser conferida em máquina de esteira, em sessão
remota ou com o protetor de tela ligado — situações em que uma captura de tela comum não devolve
nada. Ele desenha por software, sem depender da área de trabalho, e foi o que pegou dois defeitos
reais durante a construção deste projeto.

## Licença

MIT.
