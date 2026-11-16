Windows 11 Pro do kompilowania WPF na CachyOS. Maszyna działa w obrazie
[Dockur Windows](https://github.com/dockur/windows), z KVM, 8 wątkami CPU,
12 GB RAM oraz dyskiem 128 GB, który zajmuje na hoście tylko zapisane miejsce.
Obraz kontenera jest przypięty do SHA256 w `.env`.

Pulpit: <http://127.0.0.1:8006>. RDP: `127.0.0.1:3389`.
Użytkownik i wygenerowane hasło są w lokalnym pliku `.env` (uprawnienia `0600`).
Oba porty są przypisane do localhost.

Instalacja WPF automatycznie uruchamia się przy pierwszym logowaniu do Windows.
Instaluje [Visual Studio Build Tools 2026](https://visualstudio.microsoft.com/downloads/)
z [narzędziami .NET Desktop](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-build-tools),
MSBuild, NuGet, Windows SDK, pakietami .NET Framework 4.8 i 4.8.1 oraz
SDK .NET 8 i .NET 10. Instaluje również QEMU Guest Agent z projektu Fedora
VirtIO, umożliwiający uruchamianie poleceń z hosta.

Stan instalacji i log są w `shared/status/wpf-setup.json` i
`shared/status/setup.log`, a wewnątrz Windows w `C:\WpfSetup`.
Stan `ready` oznacza pomyślne kompilacje WPF dla czterech frameworków.
Pliki wykonywalne testów trafiają do `shared/artifacts/`.
Wyniki sprawdzenia aktualnej instalacji, wersje narzędzi i wynik kompilacji
przykładu z hosta są w [verification.json](verification.json).

Katalog `shared/` jest dostępny w Windows jako `Z:` i `\\host.lan\Data`.
Umieść swoje projekty w `shared/projects/`. Na pulpicie jest skrót
`WPF Developer PowerShell`, który udostępnia `dotnet` i `msbuild`.

Przykładowa kompilacja w PowerShell w Windows:

```powershell
dotnet build Z:\projects\WpfHello\WpfHello.csproj -c Release
dotnet run --project Z:\projects\WpfHello\WpfHello.csproj -f net10.0-windows
# Klasyczny projekt .NET Framework:
msbuild Z:\projects\MyApp\MyApp.sln /restore /p:RestorePackagesConfig=true /p:Configuration=Release
```

Kompilacja bezpośrednio z terminala Linux, w tym katalogu:

```bash
python scripts/windows.py info
python scripts/windows.py status
python scripts/windows.py build projects/WpfHello/WpfHello.csproj
python scripts/windows.py build projects/MyApp/MyApp.sln --msbuild
```

Polecenia przez Guest Agent działają jako `SYSTEM`. Korzystają z udziału UNC,
ponieważ `Z:` należy do sesji użytkownika. Pełny log ostatniej kompilacji
z hosta jest w `shared/artifacts/build.log`.

Zarządzanie maszyną, w tym katalogu:

```bash
./scripts/start.sh
./scripts/docker.sh compose stop
./scripts/docker.sh compose start
./scripts/docker.sh compose logs --tail 50
```

`restart: unless-stopped` uruchamia kontener po restarcie hosta, jeżeli
wcześniej nie został ręcznie zatrzymany. Dysk Windows jest w `storage/`.
Zatrzymaj maszynę przed kopiowaniem tego katalogu jako kopii zapasowej.
Zmiana obrazu kontenera zachowuje instalację Windows. Skrypt OEM uruchamia
się podczas pierwszej instalacji; po zmianie skryptu można uruchomić aktualną
kopię przez skopiowanie jej z `oem/` do `shared/`, a następnie wykonanie
jej w Windows jako administrator. Log i status pozwalają diagnozować błędy
bez ponownego instalowania systemu.

Do instalacji Dockera na tym hoście użyto:

```bash
pkexec /usr/bin/bash scripts/setup-host.sh potter
```

Użytkownik `potter` należy do grupy `docker`. Skrypt `docker.sh` odświeża
członkostwo przez `newgrp`, więc działa również przed ponownym zalogowaniem.
