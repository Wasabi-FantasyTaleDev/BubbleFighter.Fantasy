@echo off
rem Launch the BF Item Viewer GUI with the project virtual environment.
rem Usage: run_gui.bat [client_root] [--cache DIR]
cd /d "%~dp0"
".venv\Scripts\python.exe" -m bf_item_viewer gui %*
