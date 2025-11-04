@echo off
cd apple-music-downloader
..\wsl1\LxRunOffline.exe r -n u22-amdl  -c "PATH=/usr/local/go/bin:$PATH /usr/bin/python3 -u controller.py"
cmd /k