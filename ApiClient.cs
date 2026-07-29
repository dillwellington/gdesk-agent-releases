using System.Net.Http.Json;
using System.Text.Json;

namespace GDeskAgent;

/// <summary>
/// Cliente HTTP simples para falar com POST /agente/sincronizar no
/// gdesk-backend (ver app/routers/agente.py). Autenticação é via header
/// X-Agent-Token (token fixo por empresa, gerado em Minha Empresa >
/// Token do Agente), não via JWT -- o agente não representa um usuário
/// logado.
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(AgentConfig config)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ApiBaseUrl.TrimEnd('/') + "/"),
            // 60s (não 30s): o backend roda no plano gratuito do Render,
            // que hiberna a instância depois de um tempo sem uso -- a
            // primeira requisição depois disso ("cold start") pode demorar
            // bem mais que o normal só pra acordar o servidor.
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Add("X-Agent-Token", config.AgentToken);
    }

    public async Task<(bool sucesso, string mensagem)> SincronizarAsync(SincronizarPayload payload)
    {
        try
        {
            // ConfigureAwait(false) em toda chamada async daqui pra baixo:
            // evita que a continuação precise voltar pra uma thread
            // específica (ex.: a thread da tela do WinForms) pra
            // continuar. Sem isso, se algum código chamar este método de
            // forma síncrona (.GetAwaiter().GetResult()) direto na thread
            // de interface -- como aconteceu com ValidarTokenAsync em
            // SelfInstaller.ValidarToken -- o programa trava pra sempre
            // (deadlock clássico de sync-over-async no WinForms), em vez
            // de só demorar até o tempo limite da requisição.
            var resposta = await _http.PostAsJsonAsync("agente/sincronizar", payload).ConfigureAwait(false);
            var corpo = await resposta.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resposta.IsSuccessStatusCode)
            {
                return (true, corpo);
            }

            return (false, ExtrairDetalhe(corpo) ?? $"HTTP {(int)resposta.StatusCode}: {corpo}");
        }
        catch (Exception ex)
        {
            return (false, $"Falha de conexão: {ex.Message}");
        }
    }

    /// <summary>
    /// Busca a lista de setores/subsetores da empresa e, se
    /// identificadorAgente for informado e já existir um Recurso pra essa
    /// máquina, o setor_id atual dele -- usado pelo SetupForm (montar os
    /// combos na instalação, sem identificadorAgente ainda faz sentido:
    /// o recurso pode nem existir de verdade) e pelo PainelForm (mostrar
    /// o setor atual, buscado sempre do servidor -- pode ter mudado pelo
    /// sistema depois da instalação, então não dá pra confiar num valor
    /// salvo localmente).
    /// </summary>
    public async Task<(bool sucesso, EstadoAgenteResposta? estado, string mensagem)> ObterEstadoAsync(string? identificadorAgente = null)
    {
        try
        {
            // Ver comentário em SincronizarAsync sobre ConfigureAwait(false).
            var caminho = "agente/estado";
            if (!string.IsNullOrWhiteSpace(identificadorAgente))
            {
                caminho += $"?identificador_agente={Uri.EscapeDataString(identificadorAgente)}";
            }
            var resposta = await _http.GetAsync(caminho).ConfigureAwait(false);
            var corpo = await resposta.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resposta.IsSuccessStatusCode)
            {
                var estado = JsonSerializer.Deserialize<EstadoAgenteResposta>(corpo, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (true, estado, corpo);
            }

            return (false, null, ExtrairDetalhe(corpo) ?? $"HTTP {(int)resposta.StatusCode}: {corpo}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Falha de conexão: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirma junto do backend se o token é válido para alguma empresa,
    /// sem gravar nada -- chamado pelo instalador antes de configurar a
    /// máquina (ver SelfInstaller.InstalarComElevacao/ExecutarInstalacaoElevada).
    /// </summary>
    public async Task<(bool valido, string mensagem)> ValidarTokenAsync()
    {
        try
        {
            // Ver comentário em SincronizarAsync sobre ConfigureAwait(false).
            var resposta = await _http.GetAsync("agente/validar-token").ConfigureAwait(false);
            var corpo = await resposta.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resposta.IsSuccessStatusCode)
            {
                return (true, corpo);
            }

            return (false, ExtrairDetalhe(corpo) ?? $"HTTP {(int)resposta.StatusCode}: {corpo}");
        }
        catch (Exception ex)
        {
            return (false, $"Não foi possível validar o token (falha de conexão: {ex.Message}).");
        }
    }

    private static string? ExtrairDetalhe(string corpoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpoJson);
            if (doc.RootElement.TryGetProperty("detail", out var detalhe) && detalhe.ValueKind == JsonValueKind.String)
            {
                return detalhe.GetString();
            }
        }
        catch (JsonException)
        {
            // Corpo não era JSON (ex.: erro genérico do servidor/proxy) -- ignora e usa o fallback do chamador.
        }
        return null;
    }
}
