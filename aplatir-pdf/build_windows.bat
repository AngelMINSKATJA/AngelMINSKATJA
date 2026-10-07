@echo off
rem Fabrique AplatirPDF.exe sur votre PC Windows (Python 3.10+ requis : https://www.python.org/downloads/)
rem Double-cliquez sur ce fichier. Le résultat sera dans le dossier "dist".
cd /d "%~dp0"
if not exist .venv (
    py -3 -m venv .venv || python -m venv .venv
)
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
python -m pip install -r requirements-dev.txt || goto :erreur
python build.py || goto :erreur
echo.
echo Termine : dist\AplatirPDF.exe
pause
exit /b 0
:erreur
echo.
echo ECHEC de la fabrication, voir les messages ci-dessus.
pause
exit /b 1
