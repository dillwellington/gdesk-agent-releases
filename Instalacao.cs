namespace GDeskAgent;

/// <summary>
/// Local fixo onde o agente vive depois de instalado:
/// %ProgramData%\GDeskAgent (ex.: C:\ProgramData\GDeskAgent). A presença
/// de appsettings.json aqui é o que diferencia "já instalado" (sincroniza
/// normalmente, ou abre o login de chamado) de "primeira execução" (mostra
/// a tela de instalação em SetupForm) -- ver Program.cs.
///
/// Por que ProgramData e não Program Files: o agente escreve seu próprio
/// arquivo de configuração aqui (o token da empresa, colado na instalação)
/// -- ProgramData é o local convencional do Windows para dados de
/// aplicação de máquina inteira que o próprio programa precisa regravar,
/// diferente de Program Files (pensado pra binários só-leitura).
/// </summary>
public static class Instalacao
{
    public static string Pasta { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GDeskAgent");

    public static string CaminhoExe => Path.Combine(Pasta, "GDeskAgent.exe");
    public static string CaminhoConfig => Path.Combine(Pasta, "appsettings.json");
    public static bool JaInstalado => File.Exists(CaminhoConfig);
}
