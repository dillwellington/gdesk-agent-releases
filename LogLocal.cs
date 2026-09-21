using System.Text.Json;
using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Uma falha de sincronização que o servidor NÃO tem como ter registrado
/// sozinho (sem internet, timeout, Render dormindo, 401/403, 502/503/504).
/// Fica numa fila local e vai junto no próximo POST /agente/sincronizar
/// bem-sucedido (campo erros_pendentes), pra aparecer na aba Log do
/// recurso no GDesk com a data/hora ORIGINAL da falha.
/// </summary>
public sealed class ErroPendente
{
    [JsonPropertyName("ocorrido_em")]
    public string OcorridoEm { get; set; } = "";   // ISO 8601 UTC

    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = "conexao";  // conexao | http

    [JsonPropertyName("mensagem")]
    public string Mensagem { get; set; } = "";
}

/// <summary>
/// Log local do agente (arquivo de texto) + fila de falhas pendentes.
/// Toda tentativa de sincronização -- deu certo ou não -- é registrada
/// aqui, e o PainelForm/LogAgenteForm mostra isso pra quem está na
/// máquina, mesmo quando a sincronização nunca chegou no servidor.
///
/// Tudo é best-effort: falha ao gravar log nunca derruba o agente.
/// A pasta logs\ recebe permissão de escrita pra Users na instalação
/// (ver SelfInstaller); em máquinas instaladas antes disso, o processo
/// da bandeja (usuário comum) cai pro %LocalAppData%\GDeskAgent\logs.
/// </summary>
public static class LogLocal
{
    private static readonly object Trava = new();
    private const long TamanhoMaximoBytes = 1_000_000;
    private const int MaximoPendentes = 50;

    public static string PastaLogs => Path.Combine(Instalacao.Pasta, "logs");
    public static string CaminhoLog => Path.Combine(PastaLogs, "agente.log");
    private static string CaminhoPendentes => Path.Combine(PastaLogs, "erros-pendentes.json");
    private static string CaminhoLogReserva =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GDeskAgent", "logs", "agente.log");

    /// <summary>nivel: INFO | OK | ERRO</summary>
    public static void Registrar(string nivel, string mensagem)
    {
        var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{nivel}] {mensagem.Replace("\r", " ").Replace("\n", " ")}{Environment.NewLine}";
        lock (Trava)
        {
            if (!TentarAnexar(CaminhoLog, linha))
            {
                TentarAnexar(CaminhoLogReserva, linha);
            }
        }
    }

    private static bool TentarAnexar(string caminho, string linha)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            if (File.Exists(caminho) && new FileInfo(caminho).Length > TamanhoMaximoBytes)
            {
                var antigo = caminho + ".old";
                if (File.Exists(antigo)) File.Delete(antigo);
                File.Move(caminho, antigo);
            }
            File.AppendAllText(caminho, linha);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Últimas linhas do log (mais recente primeiro), juntando o arquivo principal e o de reserva.</summary>
    public static List<string> LerUltimasLinhas(int maximo = 400)
    {
        var linhas = new List<string>();
        lock (Trava)
        {
            foreach (var caminho in new[] { CaminhoLog, CaminhoLogReserva })
            {
                try
                {
                    if (File.Exists(caminho)) linhas.AddRange(File.ReadAllLines(caminho).Where(l => !string.IsNullOrWhiteSpace(l)));
                }
                catch { /* leitura best-effort */ }
            }
        }
        // O formato "yyyy-MM-dd HH:mm:ss" ordena corretamente como texto.
        return linhas.OrderByDescending(l => l, StringComparer.Ordinal).Take(maximo).ToList();
    }

    /// <summary>Resultado da última tentativa de sincronização registrada (OK ou ERRO), ou null se nunca houve.</summary>
    public static (bool sucesso, string linha)? UltimoResultado()
    {
        foreach (var linha in LerUltimasLinhas(200))
        {
            if (linha.Contains("[OK]")) return (true, linha);
            if (linha.Contains("[ERRO]")) return (false, linha);
        }
        return null;
    }

    public static List<ErroPendente> ObterPendentes()
    {
        lock (Trava)
        {
            try
            {
                if (!File.Exists(CaminhoPendentes)) return new();
                return JsonSerializer.Deserialize<List<ErroPendente>>(File.ReadAllText(CaminhoPendentes)) ?? new();
            }
            catch
            {
                return new();
            }
        }
    }

    public static void AdicionarPendente(string tipo, string mensagem)
    {
        lock (Trava)
        {
            try
            {
                var lista = ObterPendentesSemTrava();
                lista.Add(new ErroPendente { OcorridoEm = DateTime.UtcNow.ToString("o"), Tipo = tipo, Mensagem = mensagem });
                if (lista.Count > MaximoPendentes) lista = lista.Skip(lista.Count - MaximoPendentes).ToList();
                Directory.CreateDirectory(PastaLogs);
                File.WriteAllText(CaminhoPendentes, JsonSerializer.Serialize(lista));
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Remove as primeiras N pendências (as que acabaram de ser enviadas com sucesso).</summary>
    public static void RemoverPendentes(int quantidade)
    {
        if (quantidade <= 0) return;
        lock (Trava)
        {
            try
            {
                var lista = ObterPendentesSemTrava();
                lista = lista.Skip(quantidade).ToList();
                if (lista.Count == 0)
                {
                    if (File.Exists(CaminhoPendentes)) File.Delete(CaminhoPendentes);
                }
                else
                {
                    File.WriteAllText(CaminhoPendentes, JsonSerializer.Serialize(lista));
                }
            }
            catch { /* best-effort */ }
        }
    }

    private static List<ErroPendente> ObterPendentesSemTrava()
    {
        try
        {
            if (!File.Exists(CaminhoPendentes)) return new();
            return JsonSerializer.Deserialize<List<ErroPendente>>(File.ReadAllText(CaminhoPendentes)) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
