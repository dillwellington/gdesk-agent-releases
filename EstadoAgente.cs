using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Espelha app/schemas/agente.py::EstadoAgenteResposta (GET
/// /agente/estado) -- usado pelo SetupForm (montar os combos Setor/
/// Subsetor na instalação) e pelo PainelForm (mostrar o setor ATUAL do
/// recurso, que pode ter sido alterado pelo sistema depois da instalação).
/// </summary>
public sealed class EstadoAgenteResposta
{
    [JsonPropertyName("setor_id")]
    public string? SetorId { get; set; }

    [JsonPropertyName("setores")]
    public List<SetorAgenteItem> Setores { get; set; } = new();
}

/// <summary>
/// Espelha app/schemas/agente.py::SetorAgenteResposta. Um setor (se
/// SetorPaiId for null) ou subsetor (se não for -- hierarquia de só 2
/// níveis, ver app/models/setor.py) da empresa.
/// </summary>
public sealed class SetorAgenteItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("nome")]
    public string Nome { get; set; } = "";

    [JsonPropertyName("setor_pai_id")]
    public string? SetorPaiId { get; set; }
}
