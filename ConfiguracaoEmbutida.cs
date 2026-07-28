using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Configuração opcional embutida DIRETO NOS BYTES do próprio .exe pelo
/// backend, quando baixado a partir de GET /empresas/minha/agente/baixar
/// ou GET /clientes/{id}/agente/baixar (ver
/// app/services/agente_download_service.py no repositório gdesk-backend).
///
/// Por quê nos bytes, e não no nome do arquivo (como na primeira versão
/// disso): o nome do arquivo baixado aparecia feio na tela "Salvar como"
/// do navegador (um base64 longo colado no nome) -- agora o download
/// sempre se chama só "GDeskAgent.exe", limpo, e quem carrega o
/// token/cliente é um trecho fixo dentro do próprio binário.
///
/// Como funciona: o literal PlaceholderConfig logo abaixo (marcador
/// "GDESKCFGv1:" + TamanhoPayload caracteres de preenchimento) fica
/// gravado, palavra por palavra, dentro do assembly publicado -- todo
/// literal de string do C# vira bytes UTF-16LE fixos na heap de strings
/// do assembly (heap "#US" do formato de metadados do .NET), então esse
/// texto aparece sempre com esse conteúdo exato no .exe final, contanto
/// que EnableCompressionInSingleFile continue DESLIGADO no .csproj (com
/// compressão ligada, os bytes do assembly principal viram um blob
/// comprimido, e essa busca por texto não encontra mais nada -- por isso
/// foi desligada de propósito lá, ver o comentário no .csproj).
///
/// O backend localiza esse marcador nos bytes do .exe (procurando o
/// texto "GDESKCFGv1:" codificado em UTF-16LE, do mesmo jeito que o
/// compilador grava) e sobrescreve só os TamanhoPayload caracteres
/// seguintes com o token/cliente reais em base64url, preenchidos com "."
/// até bater o tamanho fixo -- sem mudar o tamanho do arquivo em nenhum
/// byte, então nada mais no .exe (a estrutura do bundle single-file, o
/// manifest no fim do arquivo etc.) precisa se mexer.
///
/// Vantagem sobre a versão por nome de arquivo: sobrevive a renomear o
/// .exe antes de rodar. Se ninguém sobrescrever os bytes (build local
/// direto pelo `dotnet publish`, ou alguém pega o .exe "cru" da release
/// do GitHub sem passar pela tela do GDesk), o trecho continua só com
/// pontos -- DetectarNoProprioBinario() reconhece isso e cai no fluxo
/// manual de sempre (SetupForm pede o token colado à mão), nunca lança
/// exceção nem trava a instalação.
/// </summary>
public sealed class ConfiguracaoEmbutida
{
    private const string Marcador = "GDESKCFGv1:";

    // Precisa bater exatamente com _TAMANHO_PAYLOAD em
    // app/services/agente_download_service.py no gdesk-backend.
    private const int TamanhoPayload = 512;

    // ATENÇÃO: a sequência de pontos abaixo não é decorativa -- são
    // exatamente TamanhoPayload (512) caracteres de preenchimento,
    // gerados programaticamente. Se algum dia precisar mudar
    // TamanhoPayload, gere a string de novo (ex.: no Python,
    // "." * 512) em vez de editar os pontos manualmente -- uma contagem
    // errada aqui faz DetectarNoProprioBinario() nunca mais achar o
    // marcador direito.
    private const string PlaceholderConfig = Marcador + "................................................................................................................................................................................................................................................................................................................................................................................................................................................................................................................................";

    // Só existe pra FORÇAR o compilador a gravar o literal PlaceholderConfig
    // no assembly publicado. Um "const" nunca referenciado em lugar nenhum
    // do código pode não virar bytes físicos no assembly -- lendo
    // ".Length" aqui (não é uma expressão constante em C#, mesmo com uma
    // string const do lado esquerdo) obriga o compilador a emitir
    // "ldstr" pro literal inteiro, que é exatamente o que grava os bytes
    // UTF-16LE na heap #US do assembly -- os mesmos bytes que
    // DetectarNoProprioBinario() (e o backend, no download) procuram
    // depois. Nunca é lido de verdade em nenhum outro lugar.
    private static readonly int TamanhoPlaceholderConferido = PlaceholderConfig.Length;

    [JsonPropertyName("t")]
    public string? Token { get; set; }

    [JsonPropertyName("c")]
    public string? ClienteId { get; set; }

    [JsonPropertyName("p")]
    public bool PatrimonioObrigatorio { get; set; }

    [JsonPropertyName("l")]
    public bool NumeroLacreObrigatorio { get; set; }

    // TEMPORÁRIO -- diagnóstico de por que DetectarNoProprioBinario() caiu
    // no fluxo manual, pra investigar um caso em que o marcador está
    // confirmadamente presente no arquivo (conferido por fora, via
    // PowerShell) mas a detecção em execução não achou nada. Mostrado na
    // SetupForm (ver lá) só quando a detecção falha, texto pequeno e
    // cinza -- não atrapalha o uso normal, é só pra tirar print e mandar.
    // Remover depois que a causa for encontrada.
    public static string UltimoDiagnostico { get; private set; } = "(não rodou ainda)";

    /// <summary>
    /// Lê os bytes do próprio .exe em execução, procura o marcador
    /// embutido e decodifica o que estiver gravado nos TamanhoPayload
    /// caracteres seguintes. Retorna null (nunca lança) se o marcador não
    /// for encontrado, o trecho ainda estiver só com pontos (placeholder
    /// intacto), o base64 for inválido, o JSON for inválido, ou não tiver
    /// um token utilizável -- em qualquer um desses casos, quem chamou
    /// deve seguir o fluxo antigo (pedir o token na tela).
    /// </summary>
    public static ConfiguracaoEmbutida? DetectarNoProprioBinario()
    {
        try
        {
            // Environment.ProcessPath (.NET 6+) em vez de
            // Process.GetCurrentProcess().MainModule?.FileName --
            // MainModule é conhecido por lançar BadImageFormatException
            // em apps publicados como single-file (0x8007000B): ele tenta
            // inspecionar o módulo como um PE "normal" pra montar
            // FileVersionInfo etc., e a estrutura de bundle do single-file
            // não bate com o que ele espera. ProcessPath só devolve o
            // caminho do executável que iniciou o processo, sem
            // inspecionar nada -- não tem esse problema.
            var caminho = Environment.ProcessPath;
            if (string.IsNullOrEmpty(caminho))
            {
                UltimoDiagnostico = "Environment.ProcessPath veio vazio/nulo";
                return null;
            }
            if (!File.Exists(caminho))
            {
                UltimoDiagnostico = $"File.Exists=false pra '{caminho}'";
                return null;
            }

            var bytes = File.ReadAllBytes(caminho);

            // UTF-16LE: é assim que o compilador grava literais de string
            // na heap #US do assembly -- precisa procurar os bytes nesse
            // formato, não em UTF-8/ASCII puro.
            var marcadorBytes = Encoding.Unicode.GetBytes(Marcador);
            var indice = IndexOfBytes(bytes, marcadorBytes);
            if (indice < 0)
            {
                UltimoDiagnostico = $"marcador NAO encontrado (arquivo {bytes.Length} bytes, caminho '{caminho}')";
                return null;
            }

            var inicioPayload = indice + marcadorBytes.Length;
            var tamanhoBytesPayload = TamanhoPayload * 2; // UTF-16LE: 2 bytes por caractere
            if (inicioPayload + tamanhoBytesPayload > bytes.Length)
            {
                UltimoDiagnostico = $"marcador achado no indice {indice}, mas nao cabe o payload (arquivo {bytes.Length} bytes)";
                return null;
            }

            var payloadTexto = Encoding.Unicode
                .GetString(bytes, inicioPayload, tamanhoBytesPayload)
                .TrimEnd('.');
            if (string.IsNullOrWhiteSpace(payloadTexto))
            {
                UltimoDiagnostico = $"marcador achado no indice {indice}, payload vazio (so pontos -- placeholder intacto)";
                return null;
            }

            string json;
            try
            {
                json = DecodificarBase64Url(payloadTexto);
            }
            catch (Exception exBase64)
            {
                UltimoDiagnostico = $"marcador achado no indice {indice}, payload='{payloadTexto}' (tamanho {payloadTexto.Length}), FALHOU decodificar base64: {exBase64.GetType().Name}: {exBase64.Message}";
                return null;
            }

            ConfiguracaoEmbutida? config;
            try
            {
                config = JsonSerializer.Deserialize<ConfiguracaoEmbutida>(json);
            }
            catch (Exception exJson)
            {
                UltimoDiagnostico = $"marcador achado no indice {indice}, base64 decodificado='{json}', FALHOU parsear JSON: {exJson.GetType().Name}: {exJson.Message}";
                return null;
            }

            if (string.IsNullOrWhiteSpace(config?.Token))
            {
                UltimoDiagnostico = $"marcador achado no indice {indice}, JSON parseado mas token vazio: '{json}'";
                return null;
            }

            UltimoDiagnostico = $"OK: marcador no indice {indice}, token detectado (tamanho {config.Token.Length})";
            return config;
        }
        catch (Exception ex)
        {
            // Arquivo bloqueado, etc. -- não é pra travar o agente por
            // causa disso, só cai no fluxo manual de sempre. Mas grava o
            // que aconteceu no diagnóstico, em vez de engolir em
            // silêncio, pra dar pra investigar.
            UltimoDiagnostico = $"EXCECAO: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var igual = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    igual = false;
                    break;
                }
            }
            if (igual) return i;
        }
        return -1;
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
