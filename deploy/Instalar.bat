@echo off
setlocal

:: Instalador do Agente GDesk para quem nao usa terminal: duplo clique
:: (ou "Executar como administrador") aqui basta. Por baixo, ele so chama
:: o Instalar-Agente.ps1 que fica na mesma pasta -- que copia os arquivos
:: para Program Files, liga a sincronizacao automatica e cria o atalho
:: "GDesk Agente" no Menu Iniciar.

:: Verifica se ja esta rodando como administrador; se nao estiver, pede
:: elevacao via UAC e reinicia a si mesmo elevado.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Solicitando permissao de administrador...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ==========================================
echo   Instalando o Agente GDesk...
echo ==========================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Instalar-Agente.ps1"

echo.
echo ==========================================
echo   Instalacao concluida.
echo   Procure "GDesk Agente" no Menu Iniciar.
echo ==========================================
pause
