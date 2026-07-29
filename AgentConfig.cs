using System.Text.Json;

namespace GDeskAgent;

/// <summary>
/// Configuração do agente, gravada por SelfInstaller na instalação (não é
/// mais um arquivo distribuído junto do .exe -- ver Instalacao.cs) em
/// %ProgramData%\GDeskAgent\appsettings.json.
/// </summary>
public sealed class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://gdesk-backend.onrender.com";
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

        if (string.IsNullOrWhiteSpace(config.AgentToken))
        {
            throw new InvalidDataException("Token do agente ausente na configuração salva. Reinstale o agente.");
        }

        return config;
    }
}
