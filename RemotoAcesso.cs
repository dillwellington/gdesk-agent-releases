using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GDeskAgent;

/// <summary>
/// Espelha a resposta de GET /agente/remoto/config (gdesk-backend,
/// app/services/acesso_remoto_service.py::config_para_agente).
/// </summary>
public sealed class ConfigRemoto
{
    [JsonPropertyName("habilitado")] public bool Habilitado { get; set; }
    [JsonPropertyName("servidor")] public string? Servidor { get; set; }
    [JsonPropertyName("chave")] public string? Chave { get; set; }
    [JsonPropertyName("instalador_url")] public string? InstaladorUrl { get; set; }
    [JsonPropertyName("instalador_sha256")] public string? InstaladorSha256 { get; set; }
    [JsonPropertyName("instalador_versao")] public string? InstaladorVersao { get; set; }
    // Nome da pasta/exe em Program Files. "RustDesk" com o cliente oficial;
    // na fase 3 (build próprio) vira o nome do nosso app.
    [JsonPropertyName("app_nome")] public string? AppNome { get; set; }
}

/// <summary>
/// GDesk Remoto (base RustDesk): instala o cliente de acesso remoto nesta
/// máquina, aponta pro servidor próprio (hbbs/hbbr na VPS), define uma
/// senha fixa aleatória e devolve ID + senha pra irem no
/// POST /agente/sincronizar -- é o que faz o botão "Conectar" da tela
/// Recursos funcionar.
///
/// Só roda como administrador/SYSTEM (a Tarefa Agendada e a instalação
/// elevada); a sincronização da bandeja (usuário comum) não mexe em nada
/// e não manda os campos -- o backend mantém o que já tinha.
///
/// Tudo é best-effort: qualquer falha aqui vai pro log local e NUNCA
/// impede a sincronização do inventário.
/// </summary>
public static class RemotoAcesso
{
    // Pasta só de SYSTEM/Administradores (ver GarantirPastaProtegida) --
    // guarda a senha fixa em texto puro, então não pode herdar a permissão
    // de leitura que o grupo Users tem em %ProgramData%.
    private static string PastaEstado => Path.Combine(Instalacao.Pasta, "remoto");
    private static string CaminhoEstado => Path.Combine(PastaEstado, "estado.json");

    private sealed class Estado
    {
        public string? Senha { get; set; }
        // true = fomos NÓS que instalamos (então a desinstalação do agente
        // também remove). Máquina que já tinha RustDesk antes fica false.
        public bool InstaladoPeloAgente { get; set; }
        public string? AppNome { get; set; }
        // SHA-256 do instalador usado na última instalação. Build novo do
        // GDesk Remoto pode sair com a MESMA versão (1.5.1) -- é o hash
        // diferente no remoto.json que dispara a atualização.
        public string? InstaladorSha256 { get; set; }
    }

    /// <summary>Versão síncrona pra SelfInstaller.SincronizarAgora (já roda sync-over-async lá).</summary>
    public static void Preparar(AgentConfig config, SincronizarPayload payload) =>
        PrepararAsync(config, payload).GetAwaiter().GetResult();

    public static async Task PrepararAsync(AgentConfig config, SincronizarPayload payload)
    {
        try
        {
            if (!EhAdministrador()) return;

            var cfg = await new ApiClient(config).ObterConfigRemotoAsync().ConfigureAwait(false);
            if (cfg == null || !cfg.Habilitado || string.IsNullOrWhiteSpace(cfg.Servidor) || string.IsNullOrWhiteSpace(cfg.Chave))
            {
                return;
            }

            var appNome = string.IsNullOrWhiteSpace(cfg.AppNome) ? "RustDesk" : cfg.AppNome!.Trim();
            var exe = CaminhoExe(appNome);
            GarantirPastaProtegida();
            var estado = LerEstado() ?? new Estado();
            // App que o agente tinha instalado antes (ex.: "RustDesk" oficial,
            // antes da troca pro "GDeskRemoto" -- ver AppNome no remoto.json).
            var appAnterior = estado.AppNome;
            var anteriorInstaladoPeloAgente = estado.InstaladoPeloAgente;

            var versaoInstalada = VersaoInstalada(exe);
            var shaNovo = (cfg.InstaladorSha256 ?? "").Trim().ToLowerInvariant();
            var precisaInstalar = versaoInstalada == null
                || (!string.IsNullOrWhiteSpace(cfg.InstaladorVersao) && !VersaoIgual(versaoInstalada, cfg.InstaladorVersao!))
                || (shaNovo.Length == 64 && !string.Equals(estado.InstaladorSha256, shaNovo, StringComparison.OrdinalIgnoreCase));
            if (precisaInstalar)
            {
                if (string.IsNullOrWhiteSpace(cfg.InstaladorUrl)) return;
                LogLocal.Registrar("INFO", versaoInstalada == null
                    ? "GDesk Remoto: instalando o cliente de acesso remoto..."
                    : $"GDesk Remoto: atualizando o cliente de acesso remoto ({versaoInstalada} -> {cfg.InstaladorVersao})...");
                await InstalarAsync(cfg).ConfigureAwait(false);
                if (!EsperarArquivo(exe, TimeSpan.FromSeconds(90)))
                {
                    LogLocal.Registrar("ERRO", $"GDesk Remoto: instalação terminou mas {exe} não apareceu.");
                    return;
                }
                if (versaoInstalada == null) estado.InstaladoPeloAgente = true;
                estado.AppNome = appNome;
                if (shaNovo.Length == 64) estado.InstaladorSha256 = shaNovo;
                GravarEstado(estado);

                // Trocou de app (RustDesk -> GDeskRemoto): remove o antigo, mas
                // só se foi o próprio agente que instalou ele.
                if (!string.IsNullOrWhiteSpace(appAnterior)
                    && !string.Equals(appAnterior, appNome, StringComparison.OrdinalIgnoreCase)
                    && anteriorInstaladoPeloAgente)
                {
                    var exeAnterior = CaminhoExe(appAnterior!);
                    if (File.Exists(exeAnterior))
                    {
                        LogLocal.Registrar("INFO", $"GDesk Remoto: removendo o cliente anterior ({appAnterior}).");
                        Executar(exeAnterior, "--uninstall", TimeSpan.FromMinutes(2));
                    }
                }
                // Dá tempo do serviço subir antes de mandar --option/--password (falam com ele via IPC).
                await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                versaoInstalada = VersaoInstalada(exe);
            }

            // Servidor próprio + chave. Idempotente: regrava sempre (barato,
            // e corrige se alguém mexer à mão nas configurações de rede).
            DefinirOpcao(exe, "custom-rendezvous-server", cfg.Servidor!);
            DefinirOpcao(exe, "relay-server", cfg.Servidor!);
            DefinirOpcao(exe, "key", cfg.Chave!);

            // Senha NOVA a cada sincronização completa (a cada 6h ou quando o
            // GDesk pede): uma senha que alguém tenha anotado perde a validade
            // logo. Sessões já abertas não caem com a troca. Se a troca falhar,
            // segue com a senha anterior (que continua valendo na máquina).
            var novaSenha = GerarSenha(16);
            var saidaSenha = Executar(exe, $"--password {novaSenha}", TimeSpan.FromSeconds(30));
            if (saidaSenha.Contains("Done", StringComparison.OrdinalIgnoreCase))
            {
                estado.Senha = novaSenha;
            }
            else
            {
                LogLocal.Registrar("ERRO", $"GDesk Remoto: não consegui trocar a senha ({saidaSenha.Trim()}).");
            }
            if (string.IsNullOrWhiteSpace(estado.Senha))
            {
                return;
            }

            var id = ObterId(exe, appNome);
            GravarEstado(estado);
            if (string.IsNullOrWhiteSpace(id))
            {
                LogLocal.Registrar("ERRO", "GDesk Remoto: cliente instalado, mas o ID ainda não está disponível (tenta de novo na próxima sincronização).");
                return;
            }

            payload.RemotoId = id;
            payload.RemotoSenha = estado.Senha;
            payload.RemotoVersao = versaoInstalada;
        }
        catch (Exception ex)
        {
            LogLocal.Registrar("ERRO", $"GDesk Remoto: falha ao preparar o acesso remoto: {ex.Message}");
        }
    }

    /// <summary>Chamado na desinstalação do agente: só remove se fomos nós que instalamos.</summary>
    public static void DesinstalarSeInstaladoPeloAgente()
    {
        try
        {
            var estado = LerEstado();
            if (estado == null || !estado.InstaladoPeloAgente) return;
            var exe = CaminhoExe(estado.AppNome ?? "RustDesk");
            if (File.Exists(exe)) Executar(exe, "--uninstall", TimeSpan.FromMinutes(2));
        }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------

    private static string CaminhoExe(string appNome) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), appNome, appNome.ToLowerInvariant() + ".exe");

    private static bool EhAdministrador()
    {
        using var identidade = WindowsIdentity.GetCurrent();
        return identidade.IsSystem || new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? VersaoInstalada(string exe)
    {
        if (!File.Exists(exe)) return null;
        var info = FileVersionInfo.GetVersionInfo(exe);
        return (info.ProductVersion ?? info.FileVersion ?? "0").Split('+', ' ')[0];
    }

    private static bool VersaoIgual(string a, string b)
    {
        static string Normalizar(string v) => string.Join('.', v.Trim().TrimStart('v', 'V').Split('.').Select(p => p.TrimStart('0') == "" ? "0" : p.TrimStart('0')).Concat(new[] { "0", "0", "0" }).Take(3));
        return Normalizar(a) == Normalizar(b);
    }

    private static async Task InstalarAsync(ConfigRemoto cfg)
    {
        var url = cfg.InstaladorUrl!;
        var ehMsi = url.Split('?')[0].EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var destino = Path.Combine(PastaEstado, ehMsi ? "instalador.msi" : "instalador.exe");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.InstaladorSha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(hash, cfg.InstaladorSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"SHA-256 do instalador não confere (esperado {cfg.InstaladorSha256}, baixado {hash}) -- instalação cancelada.");
                }
            }
            await File.WriteAllBytesAsync(destino, bytes).ConfigureAwait(false);
        }

        try
        {
            string saida = ehMsi
                ? Executar("msiexec.exe", $"/i \"{destino}\" /qn /norestart CREATEDESKTOPSHORTCUTS=N INSTALLPRINTER=N", TimeSpan.FromMinutes(5))
                : Executar(destino, "--silent-install", TimeSpan.FromMinutes(5));
            if (!string.IsNullOrWhiteSpace(saida)) LogLocal.Registrar("INFO", $"GDesk Remoto: instalador: {saida.Trim()}");
        }
        finally
        {
            try { File.Delete(destino); } catch { /* fica pra próxima */ }
        }
    }

    private static bool EsperarArquivo(string caminho, TimeSpan limite)
    {
        var fim = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fim)
        {
            if (File.Exists(caminho)) return true;
            Thread.Sleep(2000);
        }
        return File.Exists(caminho);
    }

    private static void DefinirOpcao(string exe, string nome, string valor)
    {
        var atual = Executar(exe, $"--option {nome}", TimeSpan.FromSeconds(20)).Trim();
        if (atual == valor) return;
        Executar(exe, $"--option {nome} \"{valor}\"", TimeSpan.FromSeconds(20));
    }

    private static string? ObterId(string exe, string appNome)
    {
        // --get-id pergunta pro serviço via IPC; logo depois de instalar
        // pode vir vazio por alguns segundos.
        for (var tentativa = 0; tentativa < 10; tentativa++)
        {
            var id = Executar(exe, "--get-id", TimeSpan.FromSeconds(15)).Trim();
            if (id.Length >= 6 && id.All(char.IsLetterOrDigit)) return id;
            Thread.Sleep(3000);
        }

        // Plano B: lê direto do arquivo de configuração do serviço.
        var raiz = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var arquivo in new[]
        {
            Path.Combine(raiz, "ServiceProfiles", "LocalService", "AppData", "Roaming", appNome, "config", appNome + ".toml"),
            Path.Combine(raiz, "System32", "config", "systemprofile", "AppData", "Roaming", appNome, "config", appNome + ".toml"),
        })
        {
            try
            {
                if (!File.Exists(arquivo)) continue;
                foreach (var linha in File.ReadAllLines(arquivo))
                {
                    var l = linha.Trim();
                    if (!l.StartsWith("id ", StringComparison.Ordinal) && !l.StartsWith("id=", StringComparison.Ordinal)) continue;
                    var valor = l[(l.IndexOf('=') + 1)..].Trim().Trim('\'', '"');
                    if (valor.Length >= 6) return valor;
                }
            }
            catch { /* tenta o próximo */ }
        }
        return null;
    }

    private static string GerarSenha(int tamanho)
    {
        // Sem caracteres ambíguos (0/O, 1/l/I) e sem símbolos -- vai em URL e em linha de comando.
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alfabeto, tamanho);
    }

    private static string Executar(string arquivo, string argumentos, TimeSpan limite)
    {
        using var processo = Process.Start(new ProcessStartInfo(arquivo, argumentos)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (processo == null) return "";
        var saida = processo.StandardOutput.ReadToEndAsync();
        if (!processo.WaitForExit((int)limite.TotalMilliseconds))
        {
            try { processo.Kill(true); } catch { }
            return "";
        }
        return saida.Wait(2000) ? saida.Result : "";
    }

    private static void GarantirPastaProtegida()
    {
        if (Directory.Exists(PastaEstado)) return;
        Directory.CreateDirectory(PastaEstado);
        // Só SYSTEM (S-1-5-18) e Administradores (S-1-5-32-544), sem herança.
        using var p = Process.Start(new ProcessStartInfo("icacls.exe",
            $"\"{PastaEstado}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F")
        { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit();
    }

    private static Estado? LerEstado()
    {
        try
        {
            return File.Exists(CaminhoEstado) ? JsonSerializer.Deserialize<Estado>(File.ReadAllText(CaminhoEstado)) : null;
        }
        catch { return null; }
    }

    private static void GravarEstado(Estado estado)
    {
        try { File.WriteAllText(CaminhoEstado, JsonSerializer.Serialize(estado)); }
        catch (Exception ex) { LogLocal.Registrar("ERRO", $"GDesk Remoto: não consegui gravar o estado local: {ex.Message}"); }
    }
}
