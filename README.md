# Postinstall

Przenośny program dla Windows z polskim interfejsem. Wersja **1.1.0** zawiera dokładnie cztery programy, w tej kolejności:

1. Synology Chat
2. Synology Drive
3. 7-Zip
4. Google Chrome

## Uruchomienie

Gotowy plik: [dist/Postinstall.exe](dist/Postinstall.exe).

Skopiuj EXE na komputer z Windows i uruchom go. Windows poprosi o uprawnienia administratora. Aplikacja nie wymaga własnego instalatora, dodatkowych DLL ani plików konfiguracyjnych. Wymaga systemowego **.NET Framework 4.8 lub nowszego**, Windows 10 od wersji 1809 albo Windows 11 oraz internetu. Zalecany system: Windows x64; programy mają również wymagania swoich producentów.

Zaznacz programy i wybierz instalację lub odinstalowanie. Możesz też użyć przycisków przy pojedynczych programach lub operacji na całym katalogu. Przed rozpoczęciem zobaczysz listę do potwierdzenia. Przyciski „wszystkie” obejmują również programy ukryte przez wyszukiwanie i filtry.

WinGet jest przygotowywany automatycznie, jeśli go brakuje. Program pobiera najnowsze stabilne wersje dostępne w katalogu WinGet. Instalacje przebiegają kolejno, a ich wyniki są weryfikowane w rejestrze Windows. „Zatrzymaj po bieżącym” pozwala uruchomionemu instalatorowi zakończyć pracę. Sam Postinstall jest przenośny; wybrane programy są normalnie instalowane w Windows.

Synology Drive instaluje desktopowy **Synology Drive Client**. Po instalacji aplikacji Synology skonfiguruj w nich połączenie ze swoim serwerem NAS.

## Projekt i sprawdzenie

- [Kod źródłowy, kompilacja i zakres testów](windows-wpf/shared/projects/Postinstall/README.md)
- [Projekt WPF](windows-wpf/shared/projects/Postinstall/Postinstall.csproj)
- [Raporty wersji 1.1.0](verification/1.1.0/)
- [Suma SHA-256 pliku EXE](dist/Postinstall.exe.sha256)

Katalog `windows-wpf` zawiera środowisko Windows do kompilacji i testowania. Wcześniejszy plik EXE zachowano w `dist/history/1.0.1`; raporty poprzednich wersji pozostały w `verification`.
