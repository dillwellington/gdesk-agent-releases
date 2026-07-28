using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Configuração opcional embutida no NOME do .exe pelo backend, quando
/// baixado a partir de GET /empresas/minha/agente/baixar ou
/// GET /clientes/{id}/agente/baixar (ver
/// app/services/agente_download_service.py no repositório gdesk-backend).
/// O arquivo vem nomeado "GDeskAgent__&lt;base64url&gt;.exe" -- o trecho
/// depois de "GDeskAgent__" é um JSON compacto (chaves curtas de
/// propósito: "t"=token da empresa, "c"=id do cliente, "p"/"l"=
/// obrigatoriedade de etiqueta de patrimônio/número do lacre nesse
/// cliente) codificado em base64url sem padding.
///
/// Isso deixa o .exe continuar sendo um binário único e genérico (o
/// mesmo de sempre, publicado na release do GitHub) -- só o NOME do
/// arquivo muda a cada download, então não precisa recompilar nem
/// modificar o executável por empresa/cliente.
///
/// Se o arquivo for renomeado antes de rodar (ou copiado de algum lugar
/// só com o nome genérico "GDeskAgent.exe"), DetectarNoNomeDoArquivo
/// simplesmente retorna null -- degradação graciosa pro fluxo antigo
/// (SetupForm pede o token colado à mão), nunca lança exceção.
/// </summary>
public sealed class ConfiguracaoEmbutida
{
    private const string Prefixo = "GDeskAgent__";

    [JsonPropertyName("t")]
    public string? Token { get; set; }

    [JsonPropertyName("c")]
    public string? ClienteId { get; set; }

    [JsonPropertyName("p")]
    public bool PatrimonioObrigatorio { get; set; }

    [JsonPropertyName("l")]
    public bool NumeroLacreObrigatorio { get; set; }

    /// <summary>
    /// Olha o nome do próprio .exe em execução (só o nome do arquivo, não
    /// a pasta) em busca do prefixo "GDeskAgent__" e decodifica o que vier
    /// depois. Retorna null (nunca lança) se o nome não tiver o prefixo,
    /// o base64 for inválido, o JSON for inválido, ou não tiver um token
    /// utilizável -- em qualquer um desses casos, quem chamou deve seguir
    /// o fluxo antigo (pedir o token na tela).
    /// </summary>
    public static ConfiguracaoEmbutida? DetectarNoNomeDoArquivo()
    {
        try
        {
            var caminho = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(caminho)) return null;

            var nome = Path.GetFileNameWithoutExtension(caminho);
            if (!nome.StartsWith(Prefixo, StringComparison.Ordinal)) return null;

            var codificado = nome.Substring(Prefixo.Length);
            var json = DecodificarBase64Url(codificado);
            var config = JsonSerializer.Deserialize<ConfiguracaoEmbutida>(json);

            return string.IsNullOrWhiteSpace(config?.Token) ? null : config;
        }
        catch
        {
            // Base64 inválido, JSON malformado etc. -- não é pra travar o
            // agente por causa disso, só cai no fluxo manual de sempre.
            return null;
        }
    }

    /// <summary>
    /// Base64url (RFC 4648, sem padding) -- espelha
    /// app/services/agente_download_service.py::codificar_config no
    /// backend. .NET só tem Convert.FromBase64String pro alfabeto padrão
    /// (com "+"/"/" e "="), então troca os caracteres e recompõe o
    /// padding antes de decodificar.
    /// </summary>
    private static string DecodificarBase64Url(string texto)
    {
        var normalizado = texto.Replace('-', '+').Replace('_', '/');
        switch (normalizado.Length % 4)
        {
            case 2: normalizado += "=="; break;
            case 3: normalizado += "="; break;
        }
        var bytes = Convert.FromBase64String(normalizado);
        return Encoding.UTF8.GetString(bytes);
    }
}
