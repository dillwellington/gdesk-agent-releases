using System.Text.Json;

namespace GDeskAgent;

/// <summary>
/// Configuração do agente, gravada por SelfInstaller na instalação (não é
/// mais um arquivo distribuído junto do .exe -- ver Instalacao.cs) em
/// %ProgramData%\GDeskAgent\appsettings.json.
/// </summary>
public sealed class AgentConfig
{
    public const string ApiBaseUrlPadrao = "https://api.gdeskapp.com.br";
    public string ApiBaseUrl { get; set; } = ApiBaseUrlPadrao;
    public string AgentToken { get; set; } = "";
    public string PortalUrl { get; set; } = "https://gdeskapp.com.br";
    public int IntervalMinutes { get; set; } = 360;

    // Preenchidos só quando a instalação veio de um download personalizado
    // por cliente (ver ConfiguracaoEmbutida.cs e SelfInstaller.cs) --
    // gravados aqui pra irem em TODA sincronização daí em diante, não só
    // na primeira (ver InventoryCollector.ColetarComConfig). Vazio/null é
    // o caso normal (empresa sem cliente específico, ou patrimônio/lacre
    // não exigidos).
    public string? ClienteId { get; set; }
    public string? Patrimonio { get; set; }
    public string? NumeroLacre { get; set; }

    // Escolhido uma única vez na tela de instalação (SetupForm, a partir
    // da lista de GET /agente/estado) -- gravado aqui pra ir em toda
    // sincronização daí em diante (ver ColetarComConfig), mas o backend só
    // grava isso na criação do Recurso; depois disso é só leitura por
    // aqui. Pra saber o valor ATUAL (que pode ter mudado pelo sistema
    // depois da instalação), o PainelForm busca em /agente/estado, não lê
    // este campo.
    public string? SetorId { get; set; }

    // Valores alterados À MÃO no painel do agente (botão "Alterar cadastro",
    // que exige login de agente/admin_cliente no GDesk). Ficam num arquivo
    // próprio na pasta logs\ (que usuários comuns podem gravar) em vez de
    // reescrever o appsettings.json, que o usuário da bandeja não consegue
    // alterar. O que estiver aqui prevalece sobre o appsettings.json em toda
    // sincronização (tarefa agendada e bandeja).
    public sealed class CadastroManual
    {
        public string? ClienteId { get; set; }
        public string? SetorId { get; set; }
        public string? Patrimonio { get; set; }
        public string? NumeroLacre { get; set; }
    }

    private static string CaminhoCadastroManual => Path.Combine(LogLocal.PastaLogs, "cadastro-manual.json");
    private static string CaminhoCadastroManualReserva =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GDeskAgent", "logs", "cadastro-manual.json");

    public static CadastroManual? LerCadastroManual()
    {
        foreach (var caminho in new[] { CaminhoCadastroManual, CaminhoCadastroManualReserva })
        {
            try
            {
                if (!File.Exists(caminho)) continue;
                var valor = JsonSerializer.Deserialize<CadastroManual>(File.ReadAllText(caminho));
                if (valor != null) return valor;
            }
            catch { /* best-effort */ }
        }
        return null;
    }

    public static bool GravarCadastroManual(CadastroManual valor)
    {
        var json = JsonSerializer.Serialize(valor);
        foreach (var caminho in new[] { CaminhoCadastroManual, CaminhoCadastroManualReserva })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
                File.WriteAllText(caminho, json);
                return true;
            }
            catch { /* tenta o próximo local */ }
        }
        return false;
    }

    public static AgentConfig Carregar()
    {
        if (!File.Exists(Instalacao.CaminhoConfig))
        {
            throw new FileNotFoundException(
                "Agente ainda não configurado nesta máquina. Dê duplo clique no GDeskAgent.exe " +
                "(sem nenhuma opção) para instalar primeiro.");
        }

        var json = File.ReadAllText(Instalacao.CaminhoConfig);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("appsettings.json inválido (JSON malformado). Reinstale o agente.");

        // 1.9.0: a API saiu do Render e foi para a VPS. Máquinas instaladas
        // antes têm a URL antiga gravada no appsettings.json -- troca aqui, em
        // memória, pra valer em todos os processos (tarefa agendada, bandeja,
        // painel), e tenta regravar o arquivo (só funciona quando o processo
        // roda como SYSTEM/admin; se falhar, a troca em memória já resolve).
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl) ||
            config.ApiBaseUrl.Contains("onrender.com", StringComparison.OrdinalIgnoreCase))
        {
            config.ApiBaseUrl = ApiBaseUrlPadrao;
            try
            {
                var original = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
                var atualizado = original.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                var chaveUrl = atualizado.Keys.FirstOrDefault(k => string.Equals(k, "ApiBaseUrl", StringComparison.OrdinalIgnoreCase)) ?? "ApiBaseUrl";
                atualizado[chaveUrl] = ApiBaseUrlPadrao;
                File.WriteAllText(Instalacao.CaminhoConfig, JsonSerializer.Serialize(atualizado, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* sem permissão de escrita: segue com a URL nova só em memória */ }
        }

        if (string.IsNullOrWhiteSpace(config.AgentToken))
        {
            throw new InvalidDataException("Token do agente ausente na configuração salva. Reinstale o agente.");
        }

        return config;
    }
}
