@echo off
rem Starts the latest released BatteryCharger: downloads it from GitHub first if a newer release exists.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-and-run.ps1"
