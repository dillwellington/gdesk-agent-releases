# GDesk Agent

Agente Windows (.NET/C#), **um único `.exe` autoinstalável**: coleta o
inventário do computador (hardware, sistema operacional, ativação, rede) e
sincroniza automaticamente com um Recurso no GDesk, além de deixar um
atalho para o usuário fazer login e abrir um chamado.

Não tem zip, não tem `appsettings.json` separado, não tem script de
instalação para o cliente rodar: o próprio `.exe` se copia para o lugar
certo, cria a tarefa de sincronização e o atalho no Menu Iniciar — o
usuário só precisa colar o token da empresa uma vez, na primeira execução.

Este projeto foi escrito neste ambiente **sem** o SDK do .NET disponível
para compilar/testar — o código está completo e revisado, mas o primeiro
build e teste em uma máquina Windows real ficam por sua conta, seguindo o
passo a passo abaixo.

## Estrutura do projeto

- `GDeskAgent.csproj` — projeto .NET 8 (`win-x64`, self-contained, single-file, WinForms habilitado)
- `Assets/icone.ico` — ícone do app (monograma "G" com as cores do GDesk), referenciado via `ApplicationIcon` no `.csproj`; aparece no `.exe`, no atalho do Menu Iniciar e no Painel de Controle
- `Program.cs` — ponto de entrada e tratamento dos argumentos de linha de comando
- `Instalacao.cs` — define onde o agente vive depois de instalado (`%ProgramData%\GDeskAgent`)
- `ConfiguracaoEmbutida.cs` — decodifica o token/cliente/obrigatoriedade de patrimônio-lacre embutidos no NOME do `.exe` quando baixado via GDesk (ver seção 4b)
- `SetupForm.cs` — telinha da primeira execução (token pré-preenchido quando detectado via `ConfiguracaoEmbutida`, senão pede pra colar; também pede etiqueta de patrimônio/número do lacre quando o cliente exige)
- `SelfInstaller.cs` — faz a instalação de verdade: copia o exe, grava a config, cria a Tarefa Agendada, o atalho e a entrada no Painel de Controle; também faz a desinstalação (seção 7)
- `AgentConfig.cs` — leitura da configuração salva (`%ProgramData%\GDeskAgent\appsettings.json`)
- `BandejaContext.cs` — ícone fixo na bandeja do sistema, acesso rápido a "Abrir chamado" e "Sincronizar agora" (ver seção 5b)
- `SincronizarPayload.cs` — espelha `app/schemas/agente.py` do gdesk-backend
- `InventoryCollector.cs` — coleta via WMI/CIM e registro do Windows
- `ApiClient.cs` — chamada HTTP para `POST /agente/sincronizar`

`LoginForm.cs` e `AuthClient.cs` (se ainda estiverem na pasta) são
sobras da versão anterior de "Abrir chamado" (login com ticket, ver
seção 5) — sem uso, podem ser apagados manualmente sem afetar nada.

## 1. Pré-requisitos para compilar

Em uma máquina Windows (a sua, ou qualquer uma com acesso à internet):

1. Instale o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (não só o Runtime — o SDK)
2. Confirme com `dotnet --version` (deve mostrar `8.x`)

## 2. Compilar (publish)

Na pasta deste projeto:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

O executável final (o único arquivo que importa) fica em:

```
bin\Release\net8.0-windows\win-x64\publish\GDeskAgent.exe
```

## 3. Testar em uma máquina antes de distribuir

```powershell
# Só coleta e imprime o inventário, sem enviar nada nem instalar nada
.\GDeskAgent.exe --teste

# Primeira execução "de verdade": mostra a telinha pedindo o token da
# empresa e se autoinstala (copia pra %ProgramData%\GDeskAgent, cria a
# Tarefa Agendada e o atalho "Abrir Chamado GDesk" no Menu Iniciar)
.\GDeskAgent.exe

# Depois de instalado, roda de novo sem argumento para forçar uma nova
# sincronização manual (é isso que a Tarefa Agendada chama sozinha)
.\GDeskAgent.exe

# Pede login (e-mail/senha) e abre o portal já autenticado -- só funciona
# depois de instalado (usa o atalho "Abrir Chamado GDesk" por trás)
.\GDeskAgent.exe --abrir-chamado

# Mostra o ícone da bandeja manualmente (normalmente sobe sozinho no
# login, via a Tarefa Agendada "GDesk Agente - Bandeja" -- ver seção 5b)
.\GDeskAgent.exe --bandeja

# Desinstala (mesma coisa que clicar em Desinstalar no Painel de
# Controle -- ver seção 7)
.\GDeskAgent.exe --desinstalar
```

Onde pegar o token da empresa para colar na telinha: **GDesk > Minha
Empresa > card "Agente GDesk" > botão Copiar**.

Se `--teste` mostrar tudo certo e a instalação disser sucesso, confira no
GDesk (tela de Recursos) que o computador apareceu (ou foi atualizado, se
já existia).

Alguns campos são melhor esforço e podem ficar vazios dependendo do
hardware/edição do Windows (comentado no código, em `InventoryCollector.cs`):
tipo de armazenamento (SSD/HDD/NVMe — depende do namespace de Storage do
Windows estar disponível), VRAM da placa de vídeo acima de 4 GB, e chave
de produto original (só existe em máquinas OEM). Todos esses campos, e
mais os outros trazidos automaticamente por essa coleta, também podem ser
digitados manualmente no cadastro do Recurso no GDesk — útil pra ativos
que nunca terão o agente instalado (impressora, catraca etc.).

## 4. Distribuir

### Manual, computador por computador (o "leigo" clica sozinho)

1. Baixe `GDeskAgent.exe` (pelo botão "Baixar agente para Windows" em
   Minha Empresa ou na tela de um Cliente específico — ver seção 4b —, ou
   copiando o `.exe` publicado)
2. Dê duplo clique nele
3. Se o token já veio detectado automaticamente (baixado pelo GDesk — ver
   4b), o campo já aparece preenchido e só-leitura; senão, cole o token
   da empresa na telinha que abre. Se o cliente exigir, preencha também a
   etiqueta de patrimônio e/ou o número do lacre. Clique em **Instalar**
4. Aceite a permissão de administrador (UAC) que aparece — é necessária
   para criar a tarefa de sincronização e o atalho pra todos os usuários
   da máquina
5. Pronto: "Abrir Chamado GDesk" já aparece no Menu Iniciar, a
   sincronização de inventário já está agendada sozinha, e um ícone
   aparece na bandeja perto do relógio (ver seção 5b)

Não precisa saber o que é PowerShell, `Program Files` ou Tarefa Agendada
— é só isso.

### Em massa, via Active Directory (GPO)

Como o próprio `.exe` já faz tudo (copiar, configurar, agendar, criar
atalho), a distribuição em massa fica bem simples: um **Startup Script**
do GPO que roda o `.exe` com o token já embutido no comando, sem interação
nenhuma (o Startup Script de Configuração do Computador já roda como
SYSTEM, então nem precisa de UAC).

1. Publique o agente (`dotnet publish`, passo 2) e copie `GDeskAgent.exe`
   para um compartilhamento de rede legível por todos os computadores do
   domínio, ex.: `\\SEUSERVIDOR\GDeskAgent$\GDeskAgent.exe`
2. No **Editor de Gerenciamento de Política de Grupo**:
   - Crie (ou edite) um GPO e vincule-o à OU com os computadores alvo
   - Vá em **Configuração do Computador → Políticas → Configurações do
     Windows → Scripts (Inicialização/Desligamento) → Inicialização**
   - Adicione um script do tipo **Programas/Comando**, apontando para
     `\\SEUSERVIDOR\GDeskAgent$\GDeskAgent.exe`, com o parâmetro:
     ```
     --instalar-elevado "TOKEN_DA_EMPRESA_AQUI"
     ```
3. Nas máquinas alvo, rode `gpupdate /force` ou aguarde o próximo boot

`--instalar-elevado` é a mesma rotina de instalação usada no clique
manual — só que sem pedir UAC (o Startup Script já roda como SYSTEM) nem
mostrar nenhuma janela. Seguro de rodar de novo a cada boot: sempre
sobrescreve a instalação anterior com a versão/token mais recentes.

Também aceita os mesmos opcionais de cliente/patrimônio/lacre da seção
4b, se fizer sentido pro seu GPO (normalmente não faz — esses campos são
mais úteis no download manual por cliente):

```
--instalar-elevado "TOKEN_DA_EMPRESA_AQUI" --cliente-id "ID_DO_CLIENTE" --patrimonio "PAT-00123" --numero-lacre "LAC-45678"
```

## 4b. Download personalizado por empresa/cliente (sem colar token)

Tanto o botão "Baixar agente para Windows" em **Minha Empresa** quanto o
mesmo botão dentro do cadastro de um **Cliente** específico baixam
exatamente o mesmo `GDeskAgent.exe` de sempre — a diferença é só o NOME
do arquivo: o backend embute o token da empresa (e, no caso do Cliente,
também o id dele e se etiqueta de patrimônio/número do lacre são
obrigatórios ali) em base64url logo depois de `GDeskAgent__` no nome do
arquivo baixado (ver `app/services/agente_download_service.py` no
gdesk-backend). Nada disso altera o `.exe` em si nem exige recompilar ou
publicar uma versão por empresa/cliente.

Na primeira execução, `ConfiguracaoEmbutida.DetectarNoNomeDoArquivo()`
lê esse nome, decodifica e usa pra pular a telinha de colar o token (ver
`SetupForm.cs`). Se a pessoa renomear o arquivo antes de rodar (ou copiar
um `.exe` genérico de outro lugar), o agente simplesmente não encontra
esse prefixo e cai no fluxo antigo — pede o token na tela, sem erro
nenhum.

Baixado a partir de um Cliente que marcou "Exigir etiqueta de
patrimônio" e/ou "Exigir número do lacre" (ver cadastro do Cliente no
GDesk), a telinha de instalação mostra esses campos como obrigatórios e
só libera o botão Instalar depois de preenchidos — os valores digitados
acompanham a primeira sincronização e viram
`Recurso.patrimonio`/`Recurso.numero_lacre` (o backend também valida essa
obrigatoriedade do lado dele, em `app/routers/agente.py::sincronizar`,
como segunda camada de segurança).

## 5. "Abrir chamado" pelo agente

`GDeskAgent.exe --abrir-chamado` (o atalho "Abrir Chamado GDesk" no Menu
Iniciar, ou o ícone da bandeja — seção 5b) só abre o navegador padrão
direto em `PortalUrl/login.html`: a pessoa loga ali mesmo, com o e-mail e
senha de sempre. O agente não pede nem vê a senha em nenhum momento — não
tem formulário de login dentro dele.

(Uma versão anterior deste agente tinha uma tela própria de e-mail/senha
com login automático via ticket de uso único — foi simplificada de
propósito para este fluxo mais direto. O backend ainda tem as rotas
`POST /auth/login-agente` e `POST /auth/trocar-ticket-agente` desse
mecanismo, sem uso no momento, mas sem problema em deixar assim.)

## 5b. Ícone na bandeja (acesso rápido)

Depois de instalado, um ícone fica fixo na bandeja do sistema (perto do
relógio) o tempo todo que o usuário estiver logado — clique direito (ou
duplo clique) dá acesso rápido a:

- **Abrir chamado** — mesma ação de "Abrir Chamado GDesk" (seção 5): abre o navegador na tela de login
- **Sincronizar agora** — força uma sincronização de inventário na hora,
  com um aviso (balão) de sucesso ou falha
- **Sair** — fecha o ícone (não desinstala nada; ele volta sozinho no
  próximo login)

Esse ícone (`GDeskAgent.exe --bandeja`) é independente da sincronização
periódica: continua rodando escondida a cada 6 horas via a Tarefa
Agendada "GDesk Agente - Sincronizacao", mesmo se o usuário fechar o
ícone da bandeja ou não estiver logado. A bandeja tem sua própria Tarefa
Agendada, "GDesk Agente - Bandeja" (gatilho **ao logar**, sem privilégio
de administrador — só precisa de sessão interativa pra aparecer na tela).

## 6. Desinstalar

Depois de instalado, o "GDesk Agente" aparece normalmente em **Painel de
Controle > Programas > Programas e Recursos** (ou **Configurações >
Aplicativos** no Windows 11), do mesmo jeito que qualquer outro programa
instalado na máquina. Selecionar e clicar em **Desinstalar** pede
confirmação, depois elevação (UAC), e remove tudo que a instalação criou:

- As duas Tarefas Agendadas ("GDesk Agente - Sincronizacao" e "GDesk
  Agente - Bandeja")
- O ícone da bandeja, se estiver rodando naquele momento
- O atalho "Abrir Chamado GDesk" do Menu Iniciar
- A própria entrada em Programas e Recursos
- A pasta `%ProgramData%\GDeskAgent` inteira (config e o `.exe` copiado)

Nada disso afeta o Recurso já cadastrado no GDesk (o histórico de
inventário sincronizado continua lá) — só remove o agente da máquina.
Pra reinstalar depois, é só rodar o `.exe` de novo, do mesmo jeito da
primeira vez.

## 7. Publicando o instalador para download dentro do próprio GDesk

A tela **Minha Empresa** (e a de cada **Cliente**, ver seção 4b) do GDesk
tem um botão "Baixar agente para Windows" que serve sempre o mesmo
binário `GDeskAgent.exe` pro admin — não precisa gerar/publicar nada por
empresa ou cliente. O que muda a cada download é só o NOME do arquivo
(token, e no caso do Cliente também o id dele e a obrigatoriedade de
patrimônio/lacre, embutidos ali — ver seção 4b), não o conteúdo do
binário. O asset publicado no GitHub (abaixo) continua precisando se
chamar exatamente `GDeskAgent.exe`, sem relação com o nome que o
navegador do admin efetivamente baixa.

O `.exe` fica hospedado como asset de uma **release do GitHub**, num
repositório dedicado só a isso —
[`dillwellington/gdesk-agent-releases`](https://github.com/dillwellington/gdesk-agent-releases)
— em vez do Supabase Storage (o binário self-contained do agente passa
dos 50 MB do plano gratuito do Supabase, mesmo com
`EnableCompressionInSingleFile` habilitado no `.csproj` — WinForms puxa
bastante coisa do runtime) e em vez do próprio repositório `gdesk-backend`
(esse é **privado**, e o link direto de download de release do GitHub só
funciona em repositórios **públicos** — por isso o repositório separado:
ele só guarda o `.exe` compilado, nada de código-fonte nem segredo, então
não tem problema ser público).

Para publicar (ou atualizar) o instalador:

1. Publique o agente (`dotnet publish`, passo 2)
2. Acesse `https://github.com/dillwellington/gdesk-agent-releases/releases`
3. Se a release `agente-latest` ainda não existir: clique em **Draft a
   new release** (ou "Create a new release"), em "Choose a tag" digite
   `agente-latest` e crie essa tag, dê um título (ex.: "Agente GDesk"), e
   arraste o `GDeskAgent.exe` publicado na área de assets. Publique a
   release.
4. Para atualizar uma versão nova depois: abra a release `agente-latest`
   existente, clique em **Edit release**, remova o asset antigo, arraste
   o novo `GDeskAgent.exe` no lugar, e salve — **o nome do arquivo
   precisa continuar sendo exatamente `GDeskAgent.exe`**, senão o link
   fixo usado pelo backend
   (`releases/download/agente-latest/GDeskAgent.exe`) para de funcionar.
5. Pronto — o botão "Baixar agente para Windows" em Minha Empresa passa a
   servir essa versão para qualquer empresa que clicar (o backend busca o
   arquivo do GitHub e repassa, sem redirecionar o navegador direto pra
   lá — ver `app/routers/empresas.py::baixar_agente`)
