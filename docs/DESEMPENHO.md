# Desempenho e memória

Estado em 14/10/2026, 22:40. O trabalho está pausado no meio da avaliação das melhorias secundárias.

- As três correções principais estão no código-fonte e passaram nos testes.
- O executável entregue (`out/AppleMusicPresence-win-x64/AppleMusicPresence.exe`) ainda é o v0.1.1 original, que voltou a rodar na bandeja.
- O código e este relatório estão versionados; o executável otimizado ainda não foi publicado.

Os dados brutos, binários de cada variante e scripts de investigação ficam em `out/perf/`, que é local e ignorado pelo Git. Este arquivo resume o que eles mostram. Não apague `out/perf/`: o executável de referência e os dados não existem em outro lugar.

## Resumo

O app mostrava ~166 MB na coluna **Memória** do Gerenciador de Tarefas. Essa coluna é o conjunto de trabalho privado. Ela não era o heap gerenciado: o heap do .NET ficava entre 4 e 8 MB. Três causas explicavam quase tudo:

1. **Compressão do pacote único.** Com `EnableCompressionInSingleFile=true`, cada assembly carregado existe duas vezes na RAM. Uma cópia descompactada fica numa seção da paginação (~111 MB, fora da coluna Memória, mas usada só por este processo). A outra é a imagem convertida em memória privada (~87 MB, dentro da coluna Memória).
2. **Leitura de metadados a cada segundo.** `TryGetMediaPropertiesAsync` e a miniatura da capa prendem memória nativa até o GC finalizar os objetos WinRT. Com ~16 KB/s de alocação e orçamento de geração 0 de 17 MB, o GC quase não roda. Resultado: +19 a +20 MB/min tocando música.
3. **Janela criada na partida.** Mesmo iniciado com `--background`, o app construía a janela WPF e inicializava o Direct3D e o driver de vídeo.

Resultado no código atual, bloco 1 (mediana de 5 execuções, v0 com 4, mesma máquina, música tocando):

| Métrica | v0.1.1 | Código atual | Efeito |
|---|---|---|---|
| Memória (conjunto de trabalho privado), só na bandeja | 152,8 MB | 19,9 MB | −87% |
| Bytes privados (commit), só na bandeja | 225,8 MB | 26,3 MB | −88% |
| Conjunto de trabalho total, só na bandeja | 342,4 MB | 122,1 MB | inclui DLLs compartilhadas |
| Memória após abrir e esconder a janela | 215,4 MB | 51,6 MB | −76% |
| Crescimento contínuo tocando (janela de 60 s) | +19,4 MB/min | ≈0 | vazamento eliminado |
| Pronto para uso após iniciar | 554 ms | 197 ms | −64% |
| CPU nos primeiros 10 s | 1.391 ms | 813 ms | −42% |
| Primeira abertura da janela (visível) | 97 ms | 410 ms | **mais lenta** |
| Reabertura da janela | 1,9 ms | 2,5 ms | igual |
| Tamanho do executável | 74,6 MB | 179,3 MB | **maior em disco** |

Faixas mín–máx e as demais métricas estão em `out/perf/runs/block1/summary.md`.

## Mudanças no código (validadas)

### 1. Leitura de metadados por evento

Arquivo: `src/AppleMusicPresence/Services/WindowsMediaReader.cs`.

- **Antes.** Título, artista, álbum e capa eram lidos a cada segundo.
- **Agora.** São relidos só quando o Windows dispara `MediaPropertiesChanged`, quando a sessão muda, quando a duração da faixa muda (rede de segurança se o evento se perder) ou depois de uma leitura que falhou.
- **Sem mudança.** Estado de reprodução e posição continuam sendo lidos a cada segundo.

Evidências (em `out/perf/investigation/probe/`):

- Leitor isolado tocando, capa de 707 KB. O antigo cresce +41 MB/min; o novo, −0,76 MB/min. CPU 281 × 188 ms por 100 s. Trocas de contexto iguais (68,0 × 66,9/s): a assinatura do evento não custa nada.
- 30 minutos lado a lado. Os dois leitores viram as mesmas 6 mudanças (3 trocas de faixa, pausa, retomada, troca pausada), com o mesmo título, estado e tamanho de capa. A maior diferença foi 0,6 s, dentro da defasagem do ciclo de 1 s.
- Num registro de eventos de 20 minutos, `MediaPropertiesChanged` precedeu toda troca de título. `TimelinePropertiesChanged` dispara de 4 a 14 vezes por segundo, por isso não é assinado.

### 2. Publicação sem compressão

Arquivo: `scripts/Publish.ps1`, `-p:EnableCompressionInSingleFile=false`.

Os assemblies passam a ser mapeados direto do executável: páginas de arquivo, limpas e descartáveis pelo Windows.

- **Ganho.** Na bandeja, a memória privada vai de 114,1 para 28,4 MB, comparando v1 e v2, que só diferem nisso. As duas categorias de cópia somem do mapa de memória.
- **Custo.** O executável passa de 74,6 para 179,3 MB em disco.

A publicação `-FrameworkDependent` não foi testada depois da mudança.

### 3. Janela criada na primeira abertura

Arquivo: `src/AppleMusicPresence/App.xaml.cs`, com `EnsureWindow()`. Bandeja, menu "Abrir", sinal de segunda instância e partida sem Application ID válido passam a criar a janela quando ela é necessária.

- **Ganho.** Na bandeja, 28,4 → 19,9 MB e 103,7 → 26,3 MB de commit. O driver NVIDIA e o `d3d9.dll` deixam de ser carregados antes da janela abrir. "Pronto" cai de 394 para 197 ms.
- **Custo.** A primeira abertura vai de 178 para 410 ms (faixa 259–516 ms), porque a janela passa a ser construída nesse momento. Depois da primeira abertura, a memória fica igual à do v2.

### Verificação do código atual

Feita em 14/10/2026, 22:35, com o app de bandeja fechado e música tocando:

- `dotnet build AppleMusicPresence.sln -c Release`: 0 avisos.
- Specs: 32 aprovados, 0 falhas.
- UiSmoke: 6/6. As capturas em `out/ui-smoke/` mostram faixa, artista, álbum, capa e progresso reais.
- LiveCheck: o Discord real confirmou o artista no cabeçalho e a imagem externa (`out/live-discord-check.json`). Ao terminar, o teste descarta o `DiscordBridge`, que limpa a atividade por construção. A saída não registra essa limpeza.

Essa é uma confirmação do protocolo RPC. Ninguém olhou o perfil no Discord.

Cobertura da janela sob demanda:

- **Executados.** O sinal de segunda instância, o fechamento por `WM_CLOSE` e a reabertura rodaram em mais de 30 aberturas nos benchmarks com o binário v3.
- **Só lidos no código.** Clique esquerdo no ícone, item **Abrir** do menu e partida sem Application ID válido.
- **UiSmoke.** Cria a janela diretamente, sem passar por `EnsureWindow()`.

## Composição da memória

Mapa por região no fim da janela em segundo plano (mediana do bloco 1). Formato: residente / privado, em MB.

| Categoria | v0.1.1 | Código atual | O que é |
|---|---|---|---|
| private-pe-copy | 87,1 / 87,1 | 0 / 0 | imagens convertidas dos assemblies compactados |
| section-assembly-copy | 111,6 / 0 | 0 / 0 | cópias planas descompactadas (seção da paginação) |
| private-data | 61,6 / 61,6 | 15,3 / 15,3 | heaps nativos, heap do GC, pilhas, metadados de tipos |
| mapped-file | 4,8 / 0 | 43,1 / 1,9 | o executável mapeado (assemblies sem compressão) |
| image | 45,7 / 3,4 | 36,4 / 1,9 | DLLs do sistema, quase tudo compartilhado |
| section-other | 30,4 / 0 | 26,7 / 0 | código JIT em mapeamento duplo (W^X) e outras seções |
| cfg-bitmap | 3,0 / 2,2 | 2,8 / 2,1 | bitmap do Control Flow Guard (reserva do SO) |

- **Abrir a janela.** Acrescenta ~28 MB de dados privados que ficam depois de escondê-la: estruturas do WPF, glifos e recursos do Direct3D/driver.
- **Heap gerenciado.** Cresce ~4,6 MB, quase tudo lixo à espera de uma coleta de geração 0.
- **Commit privado (v3).** Na bandeja, o commit privado é ~26 MB. Em v1 e v2, que constroem a janela na partida, era ~77 MB maior, quase sem páginas residentes correspondentes. Inferência, não medida: a maior parte é reserva do driver de vídeo, que só nesses casos já está carregado.

## Como medir

A ferramenta fica em `tests/AppleMusicPresence.Bench`. Ela observa o app de fora e não injeta nada. Para abrir e fechar a janela, usa só o sinal de segunda instância (`Local\AppleMusicPresence.Show`) e `WM_CLOSE`.

O que ela não faz:

- Não grava configurações.
- Não mexe na entrada de inicialização do Windows.
- Não controla a reprodução.

Atenção: cada execução usa as configurações reais e publica a música atual no Discord, como o app normal. Feche o app de bandeja antes de medir.

```powershell
dotnet tool install dotnet-counters --tool-path out/tools   # uma vez
.\scripts\Benchmark.ps1 -Variant 'antes=out\perf\baseline\AppleMusicPresence-v0.1.1.exe', 'depois=out\perf\variants\v3-media-nocompress-lazy\AppleMusicPresence.exe' -Rounds 5 -Warmup 60 -Out out\perf\runs\exemplo
```

`Benchmark.ps1` faz o seguinte:

1. Compila a ferramenta.
2. Aquece cada executável com `--diagnose`, que não usa o Discord.
3. Alterna a ordem das variantes a cada rodada.
4. Grava um JSON por execução e `summary.md` com mediana (mín–máx). Execuções em que alguém abriu a janela fora do roteiro saem do resumo.

Fases de cada execução (~4 min):

1. Início com `--background`.
2. Aquecimento (60 s).
3. Janela de 60 s só na bandeja.
4. Contadores do runtime e mapa de memória.
5. Primeira abertura: tempo até visível e até responder a `WM_NULL`.
6. Esconder, esperar 20 s, reabrir e esconder.
7. Mais 60 s escondido.
8. Contadores e mapa de novo.

Outros comandos de `AppleMusicPresence.Bench.exe` (em `tests/AppleMusicPresence.Bench/bin/Release/net8.0-windows10.0.19041.0/`):

- `memmap <pid> [saida.json]`: classifica cada página de um processo em execução.
- `watch <pid> <segundos>`: CPU, ciclos, trocas de contexto e memória de um processo vivo.
- `soak --exe … --label … --out … --minutes 35 --open-at 60 --counters out\tools\dotnet-counters.exe`: execução longa. Registra os contadores do runtime a cada 5 s em `*-counters.csv`. Resumo com `python -I out\perf\soak.py <json>`.
- `capture --exe … --out janela.png`: salva a janela real como PNG, para comparar a renderização entre builds.
- `summarize <pasta>`.

`soak --open-at` e `capture` foram compilados, mas ainda não executados.

Métricas:

- **Memória / PWS.** Conjunto de trabalho privado: o que a coluna Memória mostra.
- **Bytes privados.** Commit privado. Não é RAM.
- **WS.** Inclui páginas compartilhadas com outros processos.
- **CPU.** Use ciclos (`QueryProcessCycleTime`), porque o tempo de CPU avança em passos de 15,6 ms.
- **Trocas de contexto.** Somadas por thread entre leituras de 1 s. No bloco 1 a soma ignorava threads encerradas e não é confiável.
- **`StartedAt` nos JSONs.** Nos blocos 1 e 2, o campo e o horário no nome do arquivo marcam o *fim* da execução. Corrigido para execuções feitas depois de 22:38.

## Resultados parciais do bloco 2

O bloco 2 foi interrompido a pedido, com 12 de 30 execuções: 2 por variante, todas tocando. Duas amostras não decidem nada, mas indicam a direção.

As variantes são cumulativas:

- v4 = v3 + janela escondida sem `DataContext`.
- v5 = v4 + ReadyToRun.
- v6 = v4 + capa decodificada só quando exibida.
- v7 = v6 + renderização por software.
- x-notray = v4 sem ícone de bandeja. É um diagnóstico, não candidata.

| Métrica (mediana, faixa) | v3 | v4 | v5 R2R | v6 | v7 | x-notray |
|---|---|---|---|---|---|---|
| Memória na bandeja (MB) | 21,9 | 20,5 | 24,9 | 20,8 | 21,2 | 18,2 |
| Memória após abrir e esconder (MB) | 48,9 | 51,0 | 53,0 | 51,5 | **37,0** | 47,6 |
| Bytes privados após esconder (MB) | 146,3 | 147,0 | 157,0 | 147,5 | **112,8** | 142,5 |
| CPU escondida, M ciclos/60 s | 458 (344–572) | 129 (63–194) | 145 (70–221) | 61 (57–66) | 71 (66–76) | 63 (61–66) |
| Faltas de página/s escondida | 16,4 | 2,3 | 2,5 | 0,4 | 1,0 | 2,0 |
| Primeira abertura (ms) | 566 (489–643) | 761 (296–1.226) | 987 (549–1.425) | 1.398 (304–2.493) | 366 (256–476) | 1.796 (1.143–2.450) |
| Assemblies carregados | 79 | 79 | 79 | 79 | 79 | 72 |
| Métodos compilados pelo JIT | 6.098 | 5.885 | 3.020 | 5.835 | 5.807 | 5.842 |
| Executável (MB) | 179,3 | 179,3 | 237,9 | 179,3 | 179,3 | 179,3 |

Tabela completa em `out/perf/runs/block2/summary.md`. A rodada 2 de v4 e v5 teve sinais de carga externa: CPU em segundo plano de 925 e 472 M ciclos, contra ~230 nas demais.

## Candidatas em avaliação (fora do código)

Os arquivos com as três candidatas aplicadas estão em `out/perf/candidates/`. Os binários estão em `out/perf/variants/`.

**v4: soltar o `DataContext` com a janela escondida.** É a candidata mais forte.

- **Efeito.** O controlador notifica todas as propriedades a cada segundo, então a janela escondida reavaliava todos os bindings.
- **Evidência.** Nas 10 execuções derivadas de v4, a CPU escondida ficou entre 57 e 221 M ciclos/60 s. As 2 de v3 ficaram em 344 e 572. Só que, no bloco 1, v3 também teve uma execução em 138 (a métrica é bimodal).
- **Ganho absoluto.** Pequeno: ~0,1% de um núcleo.
- **Risco funcional.** Baixo. Os campos de configuração não usam binding, então texto não salvo não se perde.

No fim do construtor de `MainWindow`:

```csharp
// The controller notifies every second; a hidden window has nothing to show.
IsVisibleChanged += (_, _) => DataContext = IsVisible ? _controller : null;
```

**v7: renderização por software.** É o maior ganho restante após a primeira abertura: −12 a −15 MB de memória e −35 MB de commit.

Não é verdade que o v7 dispensa o Direct3D. Ele dispensa o dispositivo de renderização. Nos mapas do v7, `d3d9.dll` e o driver NVIDIA (`nvd3dumx`, `nvgpucomp64`, `nvldumdx`) continuam carregados. Só `nvppex` e `nvspcap64` somem. O WPF ainda consulta o adaptador.

Antes de manter:

- Confirmar pela memória de GPU por processo (`out/perf/experiments/gpu-memory.ps1`, ainda não executado).
- Comparar visualmente com `capture`.
- Medir a CPU com a janela aberta.

O comentário no código deve descrever o mecanismo real. Primeira linha de `App.OnStartup`, com `using System.Windows.Interop;` e `using System.Windows.Media;`:

```csharp
RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
```

**v6: decodificar a capa só quando exibida.** Provável descarte.

- Na bandeja, evita carregar `wpfgfx`, `d3dcompiler`, `mscms`, `icm32` e `windowscodecsext` (73 módulos em vez de 78).
- A memória não muda de forma mensurável (20,8 × 20,5 MB).
- Uma das duas primeiras aberturas levou 2,5 s, mas o x-notray, que não tem essa mudança, também teve esperas assim.

## Opções descartadas

- **ReadyToRun (v5).** Reduz o JIT de ~5.900 para ~3.000 métodos. Mas aumenta a memória na bandeja em +4,4 MB: páginas realocadas do executável (4,5 × 1,9 MB privados) e bitmap CFG maior (3,4 × 2,1 MB). O executável cresce +59 MB. A primeira abertura não melhorou.
- **Ícone de bandeja sem WinForms.** O WinForms custa ~2,3 MB e 7 assemblies (x-notray). Não justifica reescrever o ícone e o menu com P/Invoke.
- **Ajustar o orçamento de geração 0 pelo runtimeconfig.** `System.GC.Gen0Size` e `GCgen0size` são ignorados (orçamento continua 17 MB). Só a variável de ambiente `DOTNET_GCgen0size` funciona, e o app é iniciado pelo Windows. Experimento: `out/perf/experiments/gen0-knob.ps1`.
- **Forçar GC ou cortar o conjunto de trabalho.** Não usado. Só maquia o número e custa CPU.

## Questões em aberto

1. **Esperas de 1 a 2,5 s na primeira abertura.** No bloco 2 apareceram em 5 execuções: v4, v5, v6 e duas de x-notray. Não apareceram em v3 nem v7 (2 execuções cada). Em 3 das 5, a CPU nos 3 s seguintes foi baixa (406–578 ms, contra 700–1.150 nas normais), sinal de thread de UI esperando. Nas outras 2, não. Hipóteses: inicialização do dispositivo da GPU, ou chamadas síncronas do GSMTC na thread de UI a cada segundo (já existiam no v0.1.1). Um monitor de latência da thread de UI (`WM_NULL` periódico) separaria as duas.
2. **Ciclo do GC.** Com 16 KB/s de alocação e orçamento de 17 MB, o heap acumula até ~17 MB de lixo entre coletas, num ciclo de ~18 min. As janelas de 60 s não enxergam isso.
   - O soak de 35 min com contadores mede o custo real.
   - Se for relevante, `System.GC.HeapHardLimit` reduz o orçamento: 128 MB dá 10 MB, 64 MB dá 5 MB e 32 MB dá 2 MB (medido em processo de sonda).
   - Critérios para adotar:
     - O pico do heap observado (aberturas, trocas de faixa com capas grandes, buscas no catálogo) fica pelo menos 4× abaixo do limite.
     - Specs, UiSmoke e LiveCheck passam no build limitado.
     - Um comentário no `.csproj` explica o porquê e o pico observado.
3. **Ressalvas das medições.**
   - Uma máquina só: Windows 11 26200, GPU NVIDIA.
   - Uma sessão do Apple Music. A mesma faixa tocou em repetição quase todo o tempo.
   - O experimento do GC e a sonda de 30 min rodaram junto com as rodadas 1–2 do bloco 1. A execução `v3-lazywindow-20261014-204056` coincidiu com eles. Ficou dentro da faixa das demais, com a maior primeira abertura do grupo (516 ms).
   - O vigia de inicializações (`out/perf/watch-launches.ps1`) consulta a lista de processos a cada 50 ms durante os blocos.

## Próximos passos

1. Fechar o app de bandeja: menu do ícone → **Sair**. Antes de encerrar por outro meio, confirme que o caminho é `out\AppleMusicPresence-win-x64\AppleMusicPresence.exe`.
2. Completar o bloco 2 com mais 3 rodadas (~75 min), chegando a 5 execuções por variante:

   ```powershell
   .\scripts\Benchmark.ps1 -Variant 'v3-lazywindow=out/perf/variants/v3-media-nocompress-lazy/AppleMusicPresence.exe', 'v4-hidden-detach=out/perf/variants/v4-hidden-detach/AppleMusicPresence.exe', 'v5-r2r=out/perf/variants/v5-hidden-detach-r2r/AppleMusicPresence.exe', 'v6-lazy-artwork=out/perf/variants/v6-lazy-artwork/AppleMusicPresence.exe', 'v7-software-render=out/perf/variants/v7-software-render/AppleMusicPresence.exe', 'x-notray=out/perf/variants/x-notray/AppleMusicPresence.exe' -Rounds 3 -Warmup 60 -Out out/perf/runs/block2
   ```

   As 12 execuções já salvas entram no mesmo resumo. Num agente, rode-o como processo independente: tarefas em segundo plano são interrompidas após 2 h.
3. Medir a GPU por processo: `.\out\perf\experiments\gpu-memory.ps1 -Exe out\perf\variants\v6-lazy-artwork\AppleMusicPresence.exe`, e o mesmo para `v7-software-render`.
4. Capturar a janela de v6 e v7 com `capture` e comparar.
5. Decidir com as regras definidas antes de ver os dados (registradas em `out/perf/decisions.tsv`):
   - **v4.** Fica se a CPU escondida cair ≥25% em relação a v3, com ≥4 de 5 execuções abaixo da mediana de v3.
   - **v5.** Fica só se a primeira abertura melhorar ≥30% sem aumentar a memória na bandeja.
   - **v6.** Fica se a memória na bandeja cair ≥1 MB com faixas disjuntas.
   - **v7.** Fica se a memória após esconder cair ≥5 MB com faixas disjuntas e a primeira abertura não piorar mais de 20%.
   - A regra original do v7 também exigia "`d3d9.dll` ausente", que já falhou. A verificação certa passa a ser a memória de GPU.
   - O que não passar fica fora do código.
6. Aplicar as aprovadas e publicar uma variante final em `out/perf/variants/final`.
7. Soaks de 35 min com `--open-at 60` e contadores: final, v0.1.1 e, se o soak indicar, final com `HeapHardLimit`.
8. Rodar Specs, UiSmoke e LiveCheck de novo. LiveCheck exige o app de bandeja fechado e música tocando.
9. Subir `<Version>` em `src/AppleMusicPresence/AppleMusicPresence.csproj` (ainda é 0.1.1, a mesma do executável antigo). Publicar com `.\scripts\Publish.ps1 -OutputDirectory out\staging` e substituir:
   1. Faça backup do executável atual.
   2. Feche só o app (confira o caminho).
   3. Copie para `out\AppleMusicPresence-win-x64\`.
   4. Confira o SHA-256.
   5. Confirme que o hash de `settings.json` continua `8196fd6f…` e que a entrada `Run` não mudou.
   6. Inicie com `--background`.
10. Atualizar o README com os números finais de memória e tamanho.

## Restrições que continuam valendo

- Nunca resetar nem gravar testes em `%LocalAppData%\AppleMusicPresence\settings.json`. O hash de referência está em `out/perf/baseline/settings-sha256-before.txt`.
- Benchmarks não podem alterar a entrada `HKCU\…\Run`. A linha de referência está em `out/perf/baseline/startup-run-entry-before.txt`.
- Não enviar faixas sintéticas ao Discord real.
- Não fechar o Apple Music nem o Discord, e não controlar a reprodução.
- Só uma instância do app por vez, por causa do mutex. LiveCheck e benchmarks exigem o app de bandeja fechado.
- `--diagnose` é somente leitura.

## Artefatos locais (`out/perf/`)

- `baseline/`: executável v0.1.1 e SHA-256, hash das configurações, entrada `Run` e código-fonte antes da otimização (`source-before-optimization.tar.gz`).
- `variants/`: binários v0 a v7 e x-notray.
- `candidates/`: arquivos com as candidatas v4, v6 e v7 aplicadas.
- `runs/block1/`, `runs/block2/`: JSON por execução e `summary.md`.
- `investigation/`:
  - `live-counters.csv` e `live-memmap*.json`: processo original vivo.
  - `probe/*.log`: leitores antigo e novo, eventos GSMTC, latência de 30 min.
- `experiments/`: orçamento do GC (`gen0-knob.ps1`, `gen0/`), memória de GPU (`gpu-memory.ps1`) e o código do diagnóstico sem bandeja.
- `decisions.tsv`: diário de decisões com hora, hipótese, evidência e resultado.
- `launches.log`: quem iniciou o app durante as medições.
