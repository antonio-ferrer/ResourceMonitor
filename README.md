# ResourceMonitor

App para Windows que monitora o uso de CPU, RAM e disco, e registra automaticamente o que aconteceu no PC quando os recursos ficam sob pressão — com relatórios que descontam o próprio consumo do monitor da conta, pra não distorcer a leitura.

## Funcionalidades

- **Monitoramento contínuo** de CPU, RAM, I/O de disco e espaço livre em disco (limite configurável por unidade), com "auto-exclusão": uma lista de padrões de processo (aceita coringa, ex: `discord*`) é descontada do total, então os alertas e a tendência refletem o uso real do resto do sistema — não o do próprio monitor.
- **Alertas configuráveis** por limite (ex: CPU > 90%, RAM > 85%), com histerese (N amostras seguidas violando o limite antes de disparar, M amostras seguidas normais antes de considerar recuperado) pra não gerar ruído com picos momentâneos. Um alerta interrompido (monitoramento parado, ou o processo encerrado no meio de um pico) fica marcado como tal, com a duração mínima conhecida — mesmo se o processo for finalizado à força, o app corrige isso sozinho na próxima execução.
- **Espaço em disco é tratado à parte**: cada unidade fixa tem seu próprio limite, e uma violação vira uma notificação na bandeja em vez de um episódio na base de picos. Se um disco ficar indisponível durante a execução, ele para de ser monitorado até o próximo início.
- **Captura de janela de pico**: quando um alerta dispara, o app guarda automaticamente as amostras de alguns segundos antes e depois do pico, mais o snapshot dos processos que mais consumiam CPU, RAM e I/O de disco naquele momento — sem precisar gravar o histórico completo o tempo todo.
- **Tendência diária**: a cada ~5 minutos, registra a média do dia (CPU, RAM, I/O, uso de disco) — pra responder "o uso normal está subindo com o tempo?", um sinal útil pra decidir sobre upgrade de hardware.
- **Interface gráfica** com ícone na bandeja do Windows:
  - **Home**: visão geral — resumo do computador, status do monitoramento, gráfico ao vivo e top processos por CPU/RAM/Disco I/O.
  - **Configurações**: limites de alerta, processos excluídos, limites por disco, iniciar com o Windows, iniciar/parar monitoramento, e limpeza seletiva de dados (cache, tendência, base de picos).
  - **Templates**: consultas SQL somente-leitura salvas — 3 prontas (Tendência diária, Ofensores, Base de picos) e outras que você criar, com filtro de período, exportação CSV e proteção contra escrita (mesmo um comando disfarçado é rejeitado).
  - **Gráficos**: dados correntes ao vivo, tendência diária, e eventos de picos (tendência com marcadores clicáveis nos dias com alerta) — via WebView2 (Edge embutido).
  - **Relatórios**: documento imprimível ou exportável em PDF por período, com resumo, quebra por métrica, ofensores, padrão de horário dos alertas, tendência diária, projeção de espaço em disco e lista completa de eventos — cada seção é opcional (checkbox própria) e o relatório se atualiza sozinho ao mudar o filtro.
  - **Ajuda**: manual embutido explicando o ciclo de amostragem, alertas, templates e relatórios.
- **App de console** equivalente, pra rodar sem interface gráfica (ex: como tarefa agendada) — compartilha a mesma configuração e banco de dados da GUI.

## Arquitetura

Solução com 3 projetos (.NET 8, Windows):

| Projeto | O que é |
|---|---|
| [`ResourceMonitor.Core`](src/ResourceMonitor.Core) | Biblioteca compartilhada: amostragem de recursos, avaliação de limites/alertas, persistência (SQLite) e o serviço de monitoramento (`MonitoringService`) usado tanto pelo console quanto pela GUI. |
| [`ResourceMonitor`](src/ResourceMonitor) | App de console — roda o monitoramento do início ao fim do processo, imprime no terminal. |
| [`ResourceMonitor.Gui`](src/ResourceMonitor.Gui) | App WPF com bandeja, controle de start/stop, edição de configuração, dados/gráficos, relatórios imprimíveis e ajuda. |

Config e banco de dados (`resourcemonitor.db`) ficam em `%LOCALAPPDATA%\ResourceMonitor\`, compartilhados pelos dois apps — não importa qual você abre, ambos leem/escrevem o mesmo estado.

## Download

Última versão pronta pra usar (não precisa compilar nada):

**[⬇ Baixar a última versão](https://github.com/antonio-ferrer/ResourceMonitor/releases/latest)**

Requisitos: Windows 10/11 de 64 bits com o [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) instalado.

Cada release inclui um arquivo `.sha256` junto do `.zip`. Pra conferir a integridade do download no Windows:

```powershell
certutil -hashfile ResourceMonitor.Gui-vX.Y.Z-win-x64.zip SHA256
```

Compare o valor mostrado com o conteúdo do `.sha256` correspondente.

## Rodando a partir do código-fonte

Pré-requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Windows.

```powershell
git clone https://github.com/antonio-ferrer/ResourceMonitor.git
cd ResourceMonitor
dotnet build

# Console
dotnet run --project src/ResourceMonitor

# GUI
dotnet run --project src/ResourceMonitor.Gui
```

## Créditos

Ícone do app: ["Performance" icon by Icons8](https://icons8.com/icon/CEZqMfdFYPeb/performance-2).

Este projeto foi construído com o [Claude](https://claude.com), da Anthropic, através do [Claude Code](https://claude.com/claude-code). O desenvolvimento foi conduzido predominantemente com o **Claude Sonnet 5**, utilizado como principal modelo de IA para implementação, revisão de código e apoio às decisões técnicas. O processo foi leve, produtivo e demonstra o potencial da IA como parceira no desenvolvimento de aplicações.

## Licença

[GPL-3.0](LICENSE)
