@echo off
chcp 65001 >nul
call "%~dp0run.cmd" --chat --verbose %*
