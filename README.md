<img src="assets/icon.svg" width="64" height="64" alt="">

# Apple Music Presence

Um app de bandeja para mostrar o que está tocando no **Apple Music para Windows** como atividade **Ouvindo** no Discord.

- Cabeçalho **Ouvindo [artista]**, música, capa do álbum e tempo da faixa.
- Botão **Ouvir no Apple Music** na atividade.
- Remove a atividade ao pausar, parar, fechar o Apple Music ou desativar o compartilhamento.
- Reconecta quando o Discord é aberto novamente.
- Fechar/minimizar a janela mantém o app na bandeja, perto do relógio.
- Início com o Windows opcional, desativado por padrão.
- Instância única: abrir o executável novamente restaura a janela existente.

## Usar

1. Abra `out/AppleMusicPresence-win-x64/AppleMusicPresence.exe` (gere o arquivo com o comando abaixo).
2. Abra o **Apple Music da Microsoft Store** e o **Discord para desktop** na mesma sessão do Windows.
3. Na primeira abertura, configure seu Application ID:
   - Acesse o [Discord Developer Portal](https://discord.com/developers/applications).
   - Clique em **New Application** e escolha um nome (por exemplo, **Apple Music**). O cabeçalho da atividade mostrará o artista automaticamente.
   - Em **General Information**, copie o **Application ID**.
   - Cole no app e clique em **Salvar configuração**.
4. Dê o play no Apple Music. A janela mostra o estado da conexão e da publicação.

Não é necessário criar um bot, adicionar bots a servidores, fornecer token, senha ou fazer login no app. O Application ID é um identificador público. O projeto não inclui um ID de terceiros.

No Discord, habilite o compartilhamento de atividades em **Configurações do usuário → Privacidade da atividade**. Preferências de privacidade do Discord podem limitar quem vê a atividade. Os botões da atividade podem não aparecer para o próprio usuário: confira por outro perfil.

Para encerrar, clique com o botão direito no ícone da bandeja e escolha **Sair**. Para desativar apenas a atividade, use **Desativar compartilhamento**.

## Compilar e gerar o executável

Requisitos de desenvolvimento: Windows 10 2004 ou posterior, .NET SDK 8 e PowerShell. O app usa WPF, WinForms `NotifyIcon`, a API de mídia do Windows e a biblioteca `DiscordRichPresence`.

```powershell
dotnet build AppleMusicPresence.sln -c Release
dotnet run --project tests/AppleMusicPresence.Specs -c Release
.\scripts\Publish.ps1
```

O pacote padrão contém um executável **win-x64 autossuficiente**, sem exigir instalação do .NET no computador de destino, e um arquivo `.sha256`. Não exige administrador. É um executável local sem assinatura digital.

Para uma publicação menor que utiliza o [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) já instalado:

```powershell
.\scripts\Publish.ps1 -FrameworkDependent -OutputDirectory .\out\framework-dependent
```

Mantenha o executável em um local fixo antes de ativar **Iniciar com o Windows**. Se mover o arquivo, desative e ative novamente a opção para atualizar o caminho.

Desde a versão 0.1.2, o executável é publicado sem compressão: ocupa ~180 MiB em disco, e as bibliotecas são lidas direto do arquivo em vez de copiadas para a memória. Nos benchmarks, o app usa ~20 MiB de memória privada só na bandeja (coluna **Memória** do Gerenciador de Tarefas) e ~50 MiB depois de abrir a janela. O executável v0.1.1 publicado em 14/10, ainda compactado, usava ~150 MiB e crescia enquanto a música tocava. Método, resultados e próximos passos em [docs/DESEMPENHO.md](docs/DESEMPENHO.md).

## Validação

A suíte de cenários valida filtro do Apple Music, metadados reais do Windows, pausa/parada, troca e avanço de faixa, Unicode, persistência e erros. O protocolo RPC usa a biblioteca real com um transporte simulado para testar handshake, confirmação, remoção de atividade e reconexão sem publicar no perfil.

O teste visual local requer uma música tocando no Apple Music. Abre a janela WPF real, usa configurações isoladas, valida foco/erros/ações e salva capturas PNG; não altera o início automático do usuário:

```powershell
dotnet run --project tests/AppleMusicPresence.UiSmoke -c Release -- out/ui-smoke
```

Diagnóstico somente de leitura, sem enviar atividade ao Discord:

```powershell
$app = Resolve-Path .\out\AppleMusicPresence-win-x64\AppleMusicPresence.exe
Start-Process -FilePath $app -ArgumentList '--diagnose', 'out\diagnostics.json' -Wait
Get-Content .\out\diagnostics.json
```

O diagnóstico contém a faixa atual. Uma resposta RPC simulada confirma a integração do código; a exibição no perfil só pode ser conferida com um Application ID válido e o Discord conectado.

Para conferir a resposta do Discord real, feche o app de bandeja e deixe uma música tocando. Este teste usa o ID salvo, publica a faixa atual, verifica o artista no cabeçalho e a imagem externa confirmada, e limpa a atividade ao terminar:

```powershell
dotnet run --project tests/AppleMusicPresence.LiveCheck -c Release -- out/live-discord-check.json
```

## Dados e limitações

- Configurações e log de erros ficam em `%LocalAppData%\AppleMusicPresence`. Não mantém histórico de músicas.
- A capa da janela vem diretamente do Windows. **Buscar capas no catálogo da Apple** consulta a API pública da Apple com título e artista, após configurar o ID. A imagem pública é usada no Discord; nenhum arquivo local é enviado.
- A busca usa o catálogo brasileiro e, se a busca por faixa falhar, consulta os álbuns do artista. Reutiliza a capa entre faixas do mesmo álbum, limita as consultas a uma a cada quatro segundos e tenta novamente após 30 segundos quando necessário. Se nenhuma capa for encontrada, a atividade continua com texto e link. Desmarque a opção para evitar consultas de capas.
- A posição e a duração dependem do que o Apple Music informa ao Windows. Sem duração válida, a atividade fica sem contador.
- Esta versão atende ao aplicativo nativo da Apple, não ao player web, iTunes clássico ou outros serviços de música.
- O Discord no navegador não oferece a conexão local usada pelo app.

## Referências

- [API de sessões de mídia do Windows](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Discord RPC / SET_ACTIVITY](https://docs.discord.com/developers/topics/rpc#setactivity)
- [Discord RPC C#](https://github.com/Lachee/discord-rpc-csharp), licença MIT
- [API pública de busca da Apple](https://performance-partners.apple.com/search-api)

Projeto independente, sem afiliação com Apple ou Discord.
