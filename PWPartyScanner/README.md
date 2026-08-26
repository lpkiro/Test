# PWPartyScanner clean

Чиста WPF-збірка без bootstrapper-логіки.

Під час роботи застосунок НЕ запускає PowerShell/cmd, не використовує ExecutionPolicy Bypass, не просить elevation/UAC, не розпаковує та не компілює інший EXE.

- .NET 8 WPF x64
- локальний Tesseract OCR
- OpenCvSharp для підготовки зображення
- whitelist з players_list.txt
- поріг збігу: 90%

У publish-пакет додаються eng/ukr/rus traineddata.

Build target: Windows x64 self-contained.
