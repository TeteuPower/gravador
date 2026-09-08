# Arquitetura

Este documento guarda as decisões que o código não consegue explicar sozinho — as que dependem de um
"por que não do outro jeito". As decisões locais estão comentadas no próprio código, junto ao trecho
que elas governam, e é lá que devem continuar.

## O caminho do som

```
   endpoint de REPRODUÇÃO                  endpoint de CAPTURA
   (o que está tocando)                    (seu microfone)
            │                                      │
   WasapiLoopbackCapture                   WasapiCapture
            │                                      │
            ▼                                      ▼
      SampleConverter  ← float, taxa e canais internos →  SampleConverter
            │                                      │
            ▼                                      ▼
         Trilha ─── ancora no RELÓGIO DE PAREDE ─── Trilha
            │         (falta vira silêncio,          │
            │          sobra vira descarte)          │
            ├──────────────┐              ┌──────────┤
            ▼              ▼              ▼          ▼
      sistema.wav      Mixer (soma por posição)   microfone.wav
                             │
                             ▼
                        mixado.wav
                             │
                    AudioEncoder (Media Foundation)
                             ▼
                          .mp3
```

Em paralelo, sem tocar no áudio: o `MuteWatcher` decide se você está mudo, o `ScreenCapture` guarda
as telas, e a `SessaoGravacao` amarra os três com carimbos de tempo no `sessao.json`.

## A âncora no relógio de parede

É a decisão que mais moldou o código, e a que não é óbvia.

O jeito ingênuo de gravar duas fontes é escrever o que chega, na ordem em que chega. Isso quebra de
três formas, e as três aparecem numa reunião de verdade:

**1. O loopback não entrega silêncio — ele não entrega NADA.** Quando nenhum aplicativo está
tocando som, o WASAPI em modo loopback simplesmente não dispara buffer. Não é um buffer de zeros: é
ausência. Escrevendo por chegada, todo trecho calado da reunião seria pulado, e a trilha do sistema
terminaria mais curta que a do microfone — com tudo depois do primeiro silêncio deslocado.

**2. Os dois dispositivos têm cristais diferentes.** Um fone USB e o áudio da placa-mãe não contam
48 000 amostras no mesmo segundo. A diferença é de partes por milhão, o que numa reunião de duas
horas dá segundos de afastamento entre as trilhas.

**3. As capturas de tela param de bater.** O carimbo da imagem vem do cronômetro; a posição no
áudio, da contagem de amostras. Se os dois divergirem, a captura dos 42 minutos deixa de
corresponder ao que se ouve aos 42 minutos — e o valor do pacote inteiro depende disso.

A correção é fazer a posição de escrita ser **função do tempo decorrido**, e não da contagem de
buffers. Em `Audio/Trilha.cs`, cada buffer que chega calcula em que quadro ele deveria começar:

- faltando áudio para chegar lá, o buraco vira **silêncio** — é isso que salva o loopback mudo;
- sobrando áudio, o excesso é **descartado** do começo.

A tolerância é de **100 ms**, e o número não é arbitrário: o WASAPI entrega em rajadas e o instante
em que o retorno de chamada roda tem jitter de dezenas de milissegundos. Corrigir a cada buffer
transformaria esse jitter em picote audível. Cem milissegundos ficam acima do jitter e muito abaixo
do desvio que incomoda; quando a correção acontece, ela é um trecho de silêncio inaudível.

### O mixer soma por posição, não por chegada

`Audio/Mixer.cs` mantém um acumulador de alguns segundos e recebe, de cada trilha, **o número do
quadro** em que aquele buffer começa — o mesmo número que a trilha usou para escrever o arquivo
dela. É isso que garante que o `mixado.mp3` bate com os separados.

O acumulador é drenado por trás, com um segundo de atraso, para dar tempo de a trilha mais lenta
entregar a parte dela antes de a região ir para o disco. Um buffer que chegue atrasado demais é
descartado em vez de sair fora do lugar.

**Um defeito que este desenho já teve:** o `Finalizar` despejava o acumulador inteiro no fim, e o
arquivo misturado saía com 3 segundos de silêncio a mais que as trilhas separadas — 12,17 s contra
9,17 s numa gravação de prova. O limite agora é o instante final, e o laço existe porque uma
drenagem só cobre, no máximo, o tamanho do acumulador.

### Somar duas fontes estoura o teto

Sistema e microfone somados passam de 1,0 quando os dois falam junto. Cortar no teto distorce e
atrapalha a transcrição. O mixer usa um **joelho macio**: acima de 0,8 o excesso passa por uma
tangente hiperbólica em vez de bater na parede. Custa uma `tanh` por amostra fora do trecho comum —
menos de 0,1% de um núcleo — e só roda quando o pico realmente passa de 0,8.

## As quatro perguntas sobre o mudo

`Muting/MuteWatcher.cs`. A pergunta "o microfone está mudo?" parece uma e são quatro, com
confiabilidade decrescente:

| Sinal | Como se lê | Confiança |
|---|---|---|
| Endpoint mudo | `IAudioEndpointVolume.Mute` | **fato**: nenhum aplicativo recebe áudio seu |
| Volume em zero | `MasterVolumeLevelScalar` | **fato** |
| Você marcou | botão, atalho ou menu da bandeja | vale mais que o palpite |
| Atalho de mudo do app | gancho de teclado observando a combinação | **palpite** |

O quarto existe porque o mudo do Teams, do Meet e do Zoom **não toca em nada do lado do Windows**.
O aplicativo continua lendo o microfone e apenas para de enviar o seu áudio aos outros
participantes. Não há API que conte isso — e é exatamente o que a pessoa quer saber depois.

O tratamento é o que a diferença de confiança pede: o palpite entra na linha do tempo **marcado como
palpite** (`"confirmado": false` no `sessao.json`), a interface o pinta de amarelo em vez de
vermelho, o texto que vai para o Claude diz "detecção provável" em vez de "confirmado pelo Windows",
e a correção manual sobrescreve.

### O gancho de teclado não pode engolir a tecla

`Muting/KeyboardObserver.cs` usa `WH_KEYBOARD_LL` e devolve **toda** tecla com `CallNextHookEx`,
inclusive quando reconhece a combinação. Se ele consumisse o `Ctrl+Shift+M`, o Teams deixaria de
receber o próprio atalho de mudo: a ferramenta anotaria "mudo" e a pessoa seguiria falando para a
reunião inteira. É o erro mais caro que este projeto poderia cometer.

Isso é o oposto do `HotkeyManager` da aplicação (`RegisterHotKey`), que **toma** a combinação de
quem estiver na frente — ali é o que se quer, porque o atalho é nosso. As duas coisas coexistem
porque servem a propósitos opostos.

O gancho roda numa thread própria com bomba de mensagens, porque `WH_KEYBOARD_LL` exige fila na
própria thread. O retorno de chamada é o caminho de toda tecla do sistema: ele só compara inteiros.

### Sessões de captura dizem quando a reunião acabou

O `MuteWatcher` varre, a cada 3 segundos, as sessões de áudio do endpoint de captura para saber quem
está com o microfone aberto. Serve para mostrar "o Teams está usando o microfone" e, principalmente,
para **zerar o palpite quando a chamada termina** — a reunião seguinte começa do zero em vez de
herdar um "mudo" pendurado da anterior.

## Por que MP3 do Windows, e não ffmpeg

Uma trilha mono de 48 kHz em WAV custa 345 MB por hora. Três trilhas de uma reunião de duas horas
dão 2 GB — inviável para arrastar para dentro de uma IA, que é o propósito da ferramenta.

O Media Foundation já vem no Windows e tem codificador de MP3. Medido nesta máquina, ele oferece MP3
**mono de 16 a 128 kbps** em 48 kHz; o AAC, também presente, só desce até 96 kbps em mono. Voz não
precisa de 96 kbps, e o tamanho é o que decide o uso. Daí MP3 a 64 kbps por padrão: **28 MB por hora
e por trilha**.

Empacotar ffmpeg custaria ~80 MB de binário para o mesmo resultado em voz, num projeto cujo
requisito é caber em máquina modesta.

O WAV é escrito **durante** a gravação e convertido no fim. Isso não é detalhe: o `WavWriter`
regrava o cabeçalho com o tamanho de até agora a cada 3 segundos, então uma reunião que termine em
queda de energia deixa um arquivo que qualquer tocador abre. Fechar o cabeçalho só no `Dispose` —
que é o que o `WaveFileWriter` do NAudio faz — deixaria um WAV com tamanho zero. É a diferença
entre perder a reunião e perder os últimos segundos.

## Por que WPF, e não Electron

O Limpador é Electron com um motor .NET por baixo, e a mesma forma foi considerada aqui. Foi
recusada por um requisito: **rodar em hardware modesto ao lado de uma chamada de vídeo**.

O que a medição mostrou nesta máquina:

| | Memória privada |
|---|---:|
| Motor sozinho (`gravador-cli ipc`, gravando) | 12 MB |
| Gravador na bandeja | 155 MB |
| Gravador com a janela | 194 MB |
| App WPF **vazio** (referência) | 164 MB |

O código do projeto soma ~30 MB sobre o piso do WPF, e a captura custa ~1% de um núcleo. Um
Electron parte de 200–400 MB entre os processos dele, antes de qualquer código. Para uma ferramenta
que fica ligada a reunião inteira disputando CPU com o Teams, a diferença é o requisito.

Duas consequências desta escolha:

- **A janela só é construída quando alguém vai vê-la.** Quem abre na bandeja não paga pela árvore
  visual das quatro páginas — são os 39 MB entre 155 e 194.
- **Nada de interface no núcleo.** `Gravador.Core` não conhece WPF; é o que permite a linha de
  comando fazer exatamente a mesma coisa que a janela, sem uma segunda implementação para divergir.

Ajustar o GC (`DOTNET_GCgen0size`, `gcConserveMemory`, `GCRetainVM`) não mudou o número: a memória é
reserva do runtime, não do código. Fica registrado para ninguém tentar de novo.

## Por que o Claude entra depois da transcrição

Os modelos do Claude leem texto, imagem e PDF — não leem áudio. A transcrição por ele não é uma
funcionalidade que faltou implementar: não é possível.

O papel dele é o passo seguinte, e é onde ele ganha de um transcritor: ler a transcrição junto com
as capturas de tela e a linha do tempo (inclusive quais trechos você estava mudo) e devolver
decisões, pendências e os momentos que valem voltar a ouvir.

### A chamada é pelo `claude`, não pela API

Token de assinatura **não é credencial de API**. Montar a requisição à mão com o token do OAuth dá
401 depois de o login ter dado certo — que é o pior tipo de falha, porque parece defeito do login.
A chamada é feita pelo `claude` em modo não interativo (`-p --output-format json`), que é a forma
suportada de usar a assinatura. Mesma escolha do Limpador, que fala com o Claude pelo SDK do agente
em vez de montar a requisição.

O prompt vai pelo **stdin**, e não como argumento: o resumo de uma reunião com transcrição passa
dos 32 mil caracteres que a linha de comando do Windows aceita, e o erro que isso dá não diz o que
aconteceu.

As ferramentas liberadas são só de leitura (`Read`, `Glob`) e o diretório de trabalho é a pasta da
sessão: o Claude consegue abrir as capturas e a transcrição, e não consegue mexer em mais nada.

Existe um segundo caminho, por `ANTHROPIC_API_KEY`, para quem não tem o Claude Code instalado. Ele
manda as imagens em base64 e é cobrado por uso — por isso vem depois da assinatura na ordem de
escolha.

## A captura de tela, e o erro que ela dá

`Screens/ScreenCapture.cs` usa `CopyFromScreen` (BitBlt), e não a API moderna
(`Windows.Graphics.Capture`). A moderna pega janelas aceleradas por GPU que o BitBlt entrega em
preto, mas exige WinRT, cria uma sessão de captura e, em algumas máquinas, faz o Windows piscar a
borda amarela de "esta janela está sendo capturada" — no meio de uma apresentação isso é
inaceitável. Reunião e apresentação rodam em janela, que é o caso em que o BitBlt funciona.

Duas armadilhas encontradas na construção:

**`GetWindowRect` mente.** No Windows 10 e 11 ele devolve alguns pixels a mais de cada lado — a
borda invisível que o gerenciador de janelas usa para o redimensionamento com o mouse. Capturar por
ela põe uma faixa do que está atrás na imagem. O atributo estendido do DWM
(`DWMWA_EXTENDED_FRAME_BOUNDS`) dá o retângulo que a pessoa enxerga.

**O BitBlt falha com "identificador inválido" e todos os identificadores válidos.** Quando a
estação está bloqueada ou o protetor de tela está ligado, a área de trabalho que recebe a entrada
não é a sua, e o erro 6 aparece com o `GetDC` tendo devolvido um handle bom. Sem explicação, isso é
indistinguível de defeito. `AreaDeTrabalhoIndisponivel()` pergunta antes qual área de trabalho está
na frente (`OpenInputDesktop`) e devolve a mensagem de verdade: "o protetor de tela está ligado,
mexa no mouse".

Este caso não foi imaginado — apareceu no teste, com a máquina de desenvolvimento em protetor de
tela.

## O modo `--render`

`Gravador.exe --render <pasta>` desenha cada aba num PNG e sai. `--gravando` grava de verdade por
alguns segundos e desenha a tela principal no meio disso.

Existe porque a interface precisa ser conferida sem alguém olhando para o monitor: em máquina de
esteira, em sessão remota, ou com o protetor de tela ligado — que é justamente quando uma captura
de tela comum não devolve nada. O WPF desenha por software num `RenderTargetBitmap`, sem depender da
área de trabalho, então isto prova o que uma captura não conseguiria: que o XAML monta, mede,
posiciona e pinta sem estourar.

Ele pagou o custo dele já na primeira execução, pegando dois defeitos:

1. **As `ComboBox` saíam brancas com texto branco.** O gabarito de fábrica desenha a moldura com os
   pincéis do **sistema**, que ignoram `Background` e `Foreground`; num tema escuro isso deixa a
   página de configurações ilegível. Hoje `ComboBox`, `CheckBox` e `Slider` têm gabarito próprio.
2. **O atalho padrão `Ctrl+Alt+M` já vinha tomado** por outro programa da máquina, e o
   `RegisterHotKey` recusava. Medindo as alternativas, virou `Ctrl+Alt+N`. Um atalho padrão que não
   funciona na primeira execução é pior do que um sem mnemônico perfeito.

Não substitui olhar: prova que desenha, não que ficou bom.

## O que foi verificado, e como

Números medidos nesta máquina (i7-14700HX, 28 núcleos lógicos, Windows 11), não estimados.

### O alinhamento das trilhas

Gravação de prova de 9 s com som tocando no dispositivo padrão:

| arquivo | duração | RMS |
|---|---:|---:|
| `sistema.mp3` | 9,17 s | −33,0 dB |
| `microfone.mp3` | 9,17 s | −73,0 dB (sala em silêncio) |
| `mixado.mp3` | 9,17 s | −33,0 dB |

As três batem no centésimo de segundo. Antes da correção do `Mixer.Finalizar`, a mixada saía com
12,17 s — três segundos de zeros a mais, que eram o acumulador sendo despejado inteiro no fim.

### O mudo silenciando de verdade

Doze segundos, microfone com sinal real, mudo ligado aos 3,8 s e desligado aos 7,9 s. RMS por
segundo, com `SILÊNCIO` significando zero digital e não apenas "baixo":

Com **"guardar na trilha, silenciar na mistura"** (o padrão):

```
microfone.wav   -50,9  -53,1  -51,4  -52,3  -52,6  -53,2  -51,7  -53,8  -53,6  -52,8  -53,5  -53,2
mixado.wav      -50,9  -53,1  -51,4  -53,2   SIL.   SIL.   SIL.  -62,2  -53,6  -52,8  -53,5  -53,2
```

A sua trilha guarda tudo; a mistura vira o que a reunião ouviu. É exatamente a promessa da opção.

Com **"não gravar nada do que eu disser mudo"**, as duas ficam em silêncio no mesmo intervalo — e
voltam no segundo seguinte à liberação, sem arrastar.

### O custo

| | Memória privada | CPU |
|---|---:|---:|
| `gravador-cli ipc` gravando duas trilhas | 12 MB | 0,04% de 28 núcleos (~1% de um) |
| Aplicação na bandeja | 155 MB | ocioso |
| Aplicação com a janela | 194 MB | ocioso |
| App WPF **vazio**, para referência | 164 MB | — |

### O que NÃO foi verificado

Duas coisas ficaram sem prova nesta máquina, e vale dizer quais:

- **A captura de tela em imagem final.** O caminho inteiro (BitBlt, redimensionamento, JPEG) foi
  exercitado e produziu um arquivo correto, mas a máquina de desenvolvimento estava com o protetor
  de tela ligado durante a construção — a captura pelo atalho, com a área de trabalho normal na
  frente, é o que falta ver funcionando de verdade.
- **A detecção do mudo pelo atalho do aplicativo.** O gancho de teclado, o catálogo e a virada de
  estado foram exercitados pelo caminho manual, que passa exatamente pelo mesmo ponto. O que falta é
  o teste com o Teams aberto de verdade, apertando `Ctrl+Shift+M`.

## O que ficou de fora, e por quê

**Gravar vídeo.** O propósito é virar texto para uma IA; vídeo multiplica o tamanho por dez sem
acrescentar nada que a captura de tela sob demanda não dê. As capturas cobrem o slide e a planilha,
que é o que se quer rever.

**Transcrição local por whisper.cpp.** Seria a melhor qualidade offline, e foi recusada pelo mesmo
requisito que recusou o Electron: em máquina modesta, transcrever em tempo real disputa a CPU com a
reunião que está sendo gravada. Fica declarado como o próximo motor a entrar, atrás da interface
`ITranscritorDeArquivo`, que já existe justamente para isso.

**Separar quem falou (diarização).** As trilhas separadas já resolvem o caso que importa — você
contra os outros. Separar os outros entre si exige modelo próprio e erra bastante com áudio de
chamada comprimido.

**Detectar o mudo pelo pixel do botão do Teams.** Considerado e recusado: quebra a cada atualização
de interface do aplicativo, e um sinal que quebra em silêncio é pior que um sinal declaradamente
aproximado.
