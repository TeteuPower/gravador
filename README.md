# Gravador

Ferramenta para Windows que grava **o áudio do computador e o seu microfone ao mesmo tempo**, em
trilhas separadas, junta a isso as telas que você capturou durante a reunião — e faz o mesmo com
uma **gravação que já existe**: importa o MP4, extrai o áudio, reduz o vídeo aos slides, transcreve,
traduz e deixa você **conversar com o Claude** sobre o conteúdo.

O produto é uma pasta auto-suficiente por sessão: áudio, slides, transcrição, tradução, resumo e a
linha do tempo que amarra tudo. Pronta para arrastar inteira para dentro de uma IA — ou para ser
lida pelo Claude de dentro da própria ferramenta, pelas ferramentas dela.

Motivação: quem grava reunião pelo próprio Teams ou Meet depende de o organizador permitir, recebe
um vídeo pesado e não tem nada acionável no fim. Gravar pelo Windows resolve o acesso, mas o
Gravador de Voz só pega o microfone — o que os outros falam não entra. E nenhum deles responde à
pergunta que importa depois: **"o que eu falei ali os outros ouviram, ou eu estava mudo?"**

## Stack

| Camada | Escolha | Por quê |
|---|---|---|
| Captura de áudio | **C# / .NET 10** com WASAPI via NAudio (loopback + captura) | O loopback do WASAPI é a única forma de pegar "o que está tocando" sem driver virtual. |
| Interface | **WPF**, bandeja, tema escuro | O requisito era rodar em máquina modesta ao lado de uma chamada de vídeo. WPF ocupa uma fração de um Chromium. Ver [a conta medida](#o-que-isso-custa-medido). |
| Compressão | **Media Foundation** (codificador de MP3 do Windows) | Duas horas em 57 MB em vez de 690 MB, sem carregar ffmpeg para isso. |
| Importação de vídeo | **ffmpeg**, baixado sob demanda na primeira importação | O Windows não decodifica vídeo de forma utilizável (WinRT: 2,7 s por quadro). Quem só grava reunião nunca baixa. |
| Transcrição offline | **whisper.cpp**, baixado sob demanda | Excelente em inglês, bom em português, de graça. 48 min em 6m45s numa CPU comum. |
| IA | **Claude**, pelo `claude` com **as ferramentas nossas por MCP** | Usa a assinatura já conectada. Ferramentas nativas desligadas, system prompt nosso: 148 tokens por pergunta em vez de milhares. |
| Integração | **CLI + IPC por JSON linha a linha** | O mesmo contrato do motor do Limpador, para as ferramentas do conjunto conversarem. |

Detalhes das decisões que o código não explica sozinho em [docs/arquitetura.md](docs/arquitetura.md).

## Como usar

Pré-requisito: Windows 10/11. Para compilar, o [.NET 10 SDK](https://dotnet.microsoft.com/download).
Para o Claude: o [Claude Code](https://docs.anthropic.com/claude-code) instalado (`npm install -g @anthropic-ai/claude-code`) e logado, ou o login pela aba Conta.

```powershell
.\iniciar.cmd            # compila e abre
.\iniciar.cmd cli ...    # o modo linha de comando
```

O Gravador fica na bandeja. Os atalhos funcionam com qualquer programa na frente:

| Atalho | O que faz |
|---|---|
| `Ctrl+Alt+G` | começa / para e salva |
| `Ctrl+Alt+Espaço` | pausa / retoma |
| `Ctrl+Alt+P` | captura a tela e prende no instante do áudio |
| `Ctrl+Alt+N` | marca "isto aqui importa" |

Todos parametrizáveis em Configurações — clique no campo e aperte a combinação.

## Importar uma gravação que já existe

Na aba **Gravações**, *Importar áudio ou vídeo…* — ou arraste o arquivo para a janela. Pela linha de
comando:

```powershell
gravador-cli importar "C:\Videos\apresentacao.mp4"
```

O que acontece, na ordem do valor:

1. **Áudio** pelo Media Foundation do Windows: 48 minutos saem em 14 segundos, sem baixar nada.
2. **Quadros** pelo ffmpeg (baixado só na primeira vez): o vídeo é amostrado a 1 quadro por segundo
   e as miniaturas passam por um funil **em código, sem IA** que descarta o que não era a
   apresentação (você navegando no e-mail por cima), acha onde o slide está na tela — mesmo quando
   ele troca de lado — e recorta um por trecho. Medido: **2913 quadros → 59 slides**, 5 minutos
   descartados, análise em 10 segundos.
3. **Transcrição** pelo whisper.cpp local, idioma detectado sozinho.
4. **Tradução** pelo Claude quando a fala não está no seu idioma, preservando os carimbos de tempo —
   o original fica intacto em `transcricao.md`; a tradução vai para `traducao.md`.
5. **Resumo**, se estiver ligado em Configurações.

Então a sessão abre numa janela com os slides, a linha do tempo, os textos e o chat.

Uma apresentação de 48 minutos leva uns 15 minutos para importar inteira numa máquina de trabalho
— a maior parte é o whisper. O vídeo original **não é copiado** para a pasta; o que fica é o
caminho e o tamanho, porque o valor dele já foi extraído.

## O que uma sessão deixa

```
2026-09-09_apresentacao/
├── mixado.mp3        o áudio (numa gravação ao vivo: sistema.mp3 e microfone.mp3 também)
├── capturas/
│   ├── 001_slide_03m14s.jpg   um por slide, recortado no painel do slide
│   └── ...
├── transcricao.md    original, com carimbo por trecho
├── traducao.md       se a fala estava em outro idioma
├── resumo.md         se você pediu ao Claude
├── conversa.md       a conversa com o Claude, se houve
├── analise.json      o que o funil viu (retângulos, trechos, o que foi descartado)
└── sessao.json       a linha do tempo que amarra tudo
```

**Por que trilhas separadas** (nas gravações ao vivo): com a sua voz em um arquivo e a reunião em
outro, a IA sabe quem falou o quê sem adivinhar por timbre. Numa importação há uma trilha só, e o
`sessao.json` diz isso — para nenhuma IA fingir que sabe quem falou.

## Conversar com o Claude sobre a sessão

Abra uma gravação e pergunte no painel da direita: *"o que ela disse sobre tokenomics?"*, *"resuma
os slides de 20 a 30"*, *"em que minuto começou a parte de agentes?"*. A conversa continua entre
aberturas da janela — o Claude lembra o que já leu.

### Como ele lê: pelas ferramentas nossas, não pelas dele

O `claude` é chamado com as ferramentas nativas **desligadas**, com um system prompt de dez linhas
no lugar do dele, sem CLAUDE.md nem configurações suas — e com **um único servidor MCP: o próprio
`gravador-cli`**. Esse servidor recria as três ferramentas de leitura do Claude Code (`Read`,
`Glob`, `Grep`) trancadas na pasta da sessão, e oferece o que a tarefa precisa e o Claude Code não
tem:

| ferramenta | o que faz |
|---|---|
| `linha_do_tempo` | duração, idioma, arquivos, capítulos, slides, trechos descartados, trechos com o mudo |
| `transcricao(de, ate, versao)` | a fala entre dois instantes — o Claude pede o minuto que precisa, nunca tudo |
| `buscar(texto)` | onde um assunto foi falado, com carimbo |
| `quadros` / `ver_quadro(id)` | os slides, um a um, redimensionados a 1280 px |
| `definir_capitulo(em, titulo)` | ele nomeia os capítulos |
| `marcar_trecho(de, ate, tipo)` | ele corrige a análise automática ("isto era o e-mail, não a apresentação") |

O que isso custa, medido: uma pergunta sobre a sessão gasta **148 tokens novos de entrada** (mais
um prefixo de 6 mil em cache, que é o system prompt e os esquemas das ferramentas) e responde em 2
turnos. A mesma pergunta pelo Claude Code padrão, com tudo ligado, partiria de 15 a 20 mil.

Nada disso exige chave de API: é a sua assinatura, pelo `claude` já logado na máquina.

## O microfone mudo

Esta é a parte que os outros gravadores não fazem. Existem **dois mudos diferentes** no Windows:

1. **O do dispositivo.** É fato verificável: com ele ligado, nenhum aplicativo recebe áudio seu.
2. **O do aplicativo** (Teams, Meet, Zoom). **Não existe do lado do Windows**: o aplicativo continua
   lendo o microfone e só deixa de mandar o seu áudio para os outros.

Para o primeiro, o Gravador lê o endpoint e trata como certeza. Para o segundo, observa o atalho de
mudo do aplicativo em primeiro plano e trata como **palpite** — amarelo em vez de vermelho, marcado
como palpite na linha do tempo, corrigível pelo botão. O gancho de teclado devolve toda tecla para a
fila: se engolisse o `Ctrl+Shift+M`, o Teams deixaria de receber o próprio atalho.

Você escolhe o que fazer com o áudio dos trechos mudos: guardar na sua trilha e silenciar só na
mistura (padrão), gravar tudo e só anotar, ou não gravar nada.

## Transcrição

| Motor | Onde roda | Qualidade | Custo |
|---|---|---|---|
| **Nenhum** (padrão nas gravações) | — | você manda o áudio para a IA que preferir | zero |
| **whisper.cpp** (padrão nas importações) | nesta máquina, depois de gravar | excelente em inglês, boa em português | zero; baixa 9 MB + o modelo (148 MB a 1,5 GB) uma vez |
| **Windows** | nesta máquina, ao vivo ou depois | fraca; só nos idiomas com pacote de fala instalado | zero |
| **Remoto** | serviço compatível com a API de áudio da OpenAI | a melhor | por minuto; a chave fica numa variável de ambiente, nunca no arquivo |

O whisper não roda ao vivo de propósito: em máquina modesta ele disputaria a CPU com a reunião.

### Por que o Claude não faz a transcrição

Os modelos do Claude leem texto, imagem e PDF — **não leem áudio**. O que ele faz é o passo
seguinte, onde ganha de um transcritor: lê a transcrição junto com os slides e a linha do tempo e
devolve o que ficou decidido, o que ficou pendente, o que vale voltar a ouvir. E traduz.

## Linha de comando

```powershell
gravador-cli gravar --segundos 60          # grava e converte
gravador-cli importar <arquivo> [--idioma en] [--modelo small] [--sem-quadros] [--resumir]
gravador-cli transcrever <pasta>           # transcreve (ou refaz) uma sessão
gravador-cli traduzir <pasta> [--para pt-BR]
gravador-cli resumir <pasta>               # pede o resumo ao Claude
gravador-cli conversar <pasta> "pergunta"  # uma pergunta, com a resposta saindo em tempo real
gravador-cli quadros --video <mp4> --saida <pasta>   # só o funil de slides, para calibrar
gravador-cli ferramentas [--baixar]        # ffmpeg, whisper e modelo: onde estão, o que falta
gravador-cli mcp --sessao <pasta>          # o servidor MCP (é assim que o claude nos chama)
gravador-cli ipc                           # modo de integração, JSON por linha
```

O nome tem hífen por um motivo prático: o Windows não distingue maiúsculas, então `gravador.exe`
seria o mesmo arquivo que o `Gravador.exe` da janela. Com nomes distintos os dois moram na mesma
pasta e compartilham o runtime.

## O que isso custa (medido)

Nesta máquina (i7-14700HX, Windows 11):

| | Memória privada | CPU / tempo |
|---|---:|---:|
| Motor de gravação sozinho (`gravador-cli ipc`), duas trilhas | **12 MB** | **~1% de um núcleo** |
| Aplicação na bandeja, sem janela | 155 MB | ocioso |
| Aplicação com a janela aberta | 194 MB | ocioso |
| *(referência)* app WPF **vazio** | 164 MB | — |
| Importar 48 min de vídeo: áudio | | 14 s |
| Importar 48 min de vídeo: miniaturas (ffmpeg) | | 3m50s |
| Importar 48 min de vídeo: análise dos 2913 quadros | | 10 s |
| Transcrever 48 min (whisper base, 12 threads) | | 6m45s |
| Uma pergunta ao Claude sobre a sessão | | 148 tokens novos de entrada |

Tamanho em disco, por hora de reunião: **~28 MB por trilha** em MP3 a 64 kbps.

## Compilar

```powershell
dotnet build -c Release

# pacote self-contained (não depende do .NET instalado); os dois exes dividem o runtime
dotnet publish src/Gravador.App -c Release -r win-x64 --self-contained -o publish
dotnet publish src/Gravador.Cli -c Release -r win-x64 --self-contained -o publish

# conferir a interface sem olhar para a tela (desenha as abas em PNG)
.\src\Gravador.App\bin\Release\net10.0-windows\Gravador.exe --render C:\temp\telas
```

## Licença

MIT.
