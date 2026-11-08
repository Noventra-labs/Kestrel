@echo off
cd /d "%~dp0"
echo ========================================================
echo   Beam AI Cloud Backend Service (FastAPI)
echo ========================================================
echo Starting FastAPI server on http://localhost:8080...
echo Ready for local testing and Google Cloud Run deployment.
echo ========================================================
python main.py
pause
