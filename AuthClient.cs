using System.Net.Http.Json;
using System.Text.Json;

namespace GDeskAgent;

/// <summary>
/// Cliente para o fluxo "abrir chamado logado" via agente: POST
/// /auth/login-agente (mesma validação de e-mail/senha do login normal do
/// site) devolve um ticket de uso único e curta duração, que o agente
/// passa pro navegador via URL (?ticket=...) -- o navegador troca esse
/// ticket por uma sessão de verdade em login-agente.html (gdesk-frontend).
/// Diferente do ApiClient (usado pela sincronização de inventário), este
/// cliente NÃO manda o X-Agent-Token: é autenticação do usuário/solicitante
/// (e-mail+senha), não da empresa.
/// </summary>
public sealed class AuthClient
{
    private readonly HttpClient _http;

    public AuthClient(string apiBaseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<(bool sucesso, string ticketOuErro)> LoginAsync(string email, string senha)
    {
        try
        {
            var resposta = await _http.PostAsJsonAsync("auth/login-agente", new { email, senha });
            var corpo = await resposta.Content.ReadAsStringAsync();

            if (!resposta.IsSuccessStatusCode)
            {
                return (false, ExtrairMensagemDeErro(corpo, resposta.StatusCode));
            }

            using var doc = JsonDocument.Parse(corpo);
            var ticket = doc.RootElement.GetProperty("ticket").GetString();
            return (true, ticket ?? "");
        }
        catch (Exception ex)
        {
            return (false, $"Falha de conexão: {ex.Message}");
        }
    }

    private static string ExtrairMensagemDeErro(string corpoJson, System.Net.HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpoJson);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                // "detail" às vezes é um objeto (ex.: e-mail em mais de uma
                // empresa, com lista de empresas para escolher) -- o
                // agente não tem como oferecer essa escolha numa telinha
                // simples, então só avisa que precisa entrar pelo site
                // nesse caso específico.
                if (detail.ValueKind == JsonValueKind.String) return detail.GetString() ?? "Erro ao entrar.";
                if (detail.ValueKind == JsonValueKind.Object) return "Este e-mail está em mais de uma empresa -- entre pelo site normalmente.";
            }
        }
        catch { /* corpo não é JSON válido */ }
        return $"Erro {(int)status} ao entrar.";
    }
}
