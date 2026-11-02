@echo off
python "%~dp0orthanc.py" %*
exit /b %errorlevel%
