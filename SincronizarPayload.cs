using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Espelha app/schemas/agente.py::AgenteSincronizarPayload no backend
/// (repositório gdesk-backend). Qualquer campo novo adicionado lá precisa
/// ser adicionado aqui também, com o mesmo nome via [JsonPropertyName].
/// </summary>
public sealed class SincronizarPayload
{
    [JsonPropertyName("identificador_agente")]
    public string IdentificadorAgente { get; set; } = "";

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    // Preenchidos a partir de um download personalizado por cliente (ver
    // ConfiguracaoEmbutida.cs/AgentConfig.cs) -- cliente_id é aplicado
    // pelo backend só na criação do Recurso (ver
    // app/routers/agente.py::sincronizar); patrimonio/numero_lacre podem
    // ser exigidos por Cliente.agente_patrimonio_obrigatorio/
    // agente_numero_lacre_obrigatorio (validado lá também).
    [JsonPropertyName("cliente_id")]
    public string? ClienteId { get; set; }

    // Escolhido uma única vez na tela de instalação (SetupForm) -- ver
    // AgentConfig.SetorId e InventoryCollector.ColetarComConfig. O backend
    // só grava isso na CRIAÇÃO do Recurso (ver app/routers/agente.py::
    // sincronizar); reenviar em sincronizações seguintes é inofensivo
    // (ignorado), mas depois de instalado só o sistema (tela de Recursos)
    // pode mudar esse valor.
    [JsonPropertyName("setor_id")]
    public string? SetorId { get; set; }

    [JsonPropertyName("patrimonio")]
    public string? Patrimonio { get; set; }

    [JsonPropertyName("numero_lacre")]
    public string? NumeroLacre { get; set; }

    [JsonPropertyName("numero_serie")]
    public string? NumeroSerie { get; set; }

    [JsonPropertyName("tipo_dispositivo")]
    public string? TipoDispositivo { get; set; }

    [JsonPropertyName("fabricante")]
    public string? Fabricante { get; set; }

    [JsonPropertyName("modelo")]
    public string? Modelo { get; set; }

    [JsonPropertyName("sistema_operacional")]
    public string? SistemaOperacional { get; set; }

    [JsonPropertyName("versao_sistema_operacional")]
    public string? VersaoSistemaOperacional { get; set; }

    [JsonPropertyName("sistema_operacional_ativado")]
    public bool? SistemaOperacionalAtivado { get; set; }

    [JsonPropertyName("chave_produto")]
    public string? ChaveProduto { get; set; }

    [JsonPropertyName("data_formatacao")]
    public string? DataFormatacao { get; set; } // formato "yyyy-MM-dd"

    [JsonPropertyName("arquitetura")]
    public string? Arquitetura { get; set; }

    [JsonPropertyName("processador")]
    public string? Processador { get; set; }

    [JsonPropertyName("nucleos_processador")]
    public int? NucleosProcessador { get; set; }

    [JsonPropertyName("memoria_ram_gb")]
    public decimal? MemoriaRamGb { get; set; }

    [JsonPropertyName("armazenamento_total_gb")]
    public decimal? ArmazenamentoTotalGb { get; set; }

    [JsonPropertyName("armazenamento_livre_gb")]
    public decimal? ArmazenamentoLivreGb { get; set; }

    [JsonPropertyName("tipo_armazenamento")]
    public string? TipoArmazenamento { get; set; }

    [JsonPropertyName("rotulo_armazenamento")]
    public string? RotuloArmazenamento { get; set; }

    [JsonPropertyName("endereco_ip")]
    public string? EnderecoIp { get; set; }

    [JsonPropertyName("endereco_mac")]
    public string? EnderecoMac { get; set; }

    [JsonPropertyName("placa_video")]
    public string? PlacaVideo { get; set; }

    [JsonPropertyName("placa_video_vram_gb")]
    public decimal? PlacaVideoVramGb { get; set; }

    [JsonPropertyName("dominio_rede")]
    public string? DominioRede { get; set; }

    [JsonPropertyName("usuario_logado")]
    public string? UsuarioLogado { get; set; }

    [JsonPropertyName("versao_agente")]
    public string? VersaoAgente { get; set; }

    [JsonPropertyName("programas_instalados")]
    public List<ProgramaInstaladoPayload> ProgramasInstalados { get; set; } = new();
}

/// <summary>
/// Espelha app/schemas/agente.py::ProgramaInstaladoPayload. Um item por
/// programa encontrado nas chaves de Uninstall do registro do Windows
/// (ver InventoryCollector.PreencherProgramasInstalados).
/// </summary>
public sealed class ProgramaInstaladoPayload
{
    [JsonPropertyName("nome")]
    public string Nome { get; set; } = "";

    [JsonPropertyName("versao")]
    public string? Versao { get; set; }

    [JsonPropertyName("fabricante")]
    public string? Fabricante { get; set; }

    [JsonPropertyName("data_instalacao")]
    public string? DataInstalacao { get; set; } // formato "yyyy-MM-dd"
}
