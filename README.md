# ResourceMonitor

App para Windows que monitora o uso de CPU, RAM e disco, e registra automaticamente o que aconteceu no PC quando os recursos ficam sob pressão — com relatórios que descontam o próprio consumo do monitor da conta, pra não distorcer a leitura.

## Funcionalidades

- **Monitoramento contínuo** de CPU, RAM, espaço livre em disco e I/O de disco, com "auto-exclusão": o consumo do próprio `ResourceMonitor` é medido e descontado do total, então os alertas refletem o uso real do resto do sistema.
- **Alertas configuráveis** por limite (ex: CPU > 90%, RAM > 85%), com histerese (N amostras seguidas violando o limite antes de disparar, M amostras seguidas normais antes de considerar recuperado) pra não gerar ruído com picos momentâneos.
- **Captura de janela de pico**: quando um alerta dispara, o app guarda automaticamente as amostras de alguns segundos antes e depois do pico (mais o snapshot dos processos que mais consumiam CPU/RAM naquele momento) numa base SQLite permanente — sem precisar gravar o histórico completo o tempo todo.
- **Interface gráfica** com ícone na bandeja do Windows:
  - Aba **Monitoramento**: editar os parâmetros, iniciar/parar, restaurar valores padrão, e opção de iniciar automaticamente com o Windows (minimizado na bandeja, já monitorando).
  - Aba **Dados**: grid com os dados correntes (cache ao vivo) e outro com a base de picos (histórico persistido), com exportação pra CSV e opção de apagar a base.
  - Aba **Gráficos**: gráfico ao vivo do uso atual e gráfico da janela capturada em torno de um alerta selecionado, renderizados via WebView2 (Edge embutido).
- **App de console** equivalente, pra rodar sem interface gráfica (ex: como tarefa agendada).

## Arquitetura

Solução com 3 projetos (.NET 8, Windows):

| Projeto | O que é |
|---|---|
| [`ResourceMonitor.Core`](src/ResourceMonitor.Core) | Biblioteca compartilhada: amostragem de recursos, avaliação de limites/alertas, persistência (SQLite) e o serviço de monitoramento (`MonitoringService`) usado tanto pelo console quanto pela GUI. |
| [`ResourceMonitor`](src/ResourceMonitor) | App de console — roda o monitoramento do início ao fim do processo, imprime no terminal. |
| [`ResourceMonitor.Gui`](src/ResourceMonitor.Gui) | App WPF com bandeja, controle de start/stop, edição de configuração e as abas de dados/gráficos. |

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

## Licença

[GPL-3.0](LICENSE)
