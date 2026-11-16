# Postinstall — WPF / .NET Framework 4.8

Polski program postinstalacyjny dla Windows 10/11. Wynikiem kompilacji jest pojedynczy `Postinstall.exe`. Projekt używa bibliotek systemowych .NET Framework i nie wymaga NuGet, dodatkowych DLL ani plików XAML obok EXE.

## Katalog wersji 1.1.0

| Program | Identyfikator WinGet |
| --- | --- |
| Synology Chat | `Synology.ChatClient` |
| Synology Drive | `Synology.DriveClient` |
| 7-Zip | `7zip.7zip` |
| Google Chrome | `Google.Chrome` |

Katalog zawiera wyłącznie te cztery pozycje, w podanej kolejności. Synology Drive to desktopowy Synology Drive Client. Aplikacje Synology wymagają konfiguracji połączenia z serwerem NAS po instalacji.

WinGet jest odświeżany przed kolejką instalacji. Postinstall pobiera najnowszą stabilną wersję dostępną w tym katalogu; wydanie u producenta i jego dodanie do WinGet mogą nastąpić w różnym czasie.

## Obsługa

Lista pozwala zaznaczać programy, wyszukiwać je oraz filtrować według kategorii i stanu. Kategorie i liczniki wynikają z bieżącego katalogu. Można instalować, aktualizować i odinstalowywać pojedynczo, wybrane pozycje albo cały katalog. Operacje na całym katalogu uwzględniają pozycje ukryte przez filtr. Deinstalacja dotyczy wyłącznie wykrytych instalacji. Potwierdzenie pokazuje dokładną listę programów.

Kolejka działa sekwencyjnie. Błąd jednej instalacji jest zapisywany przy programie i w dzienniku; następne pozycje są obsługiwane dalej. Zatrzymanie kończy kolejkę po bieżącym instalatorze. Zamknięcie okna podczas pracy czeka na zakończenie bieżącej operacji. Program nie wymusza restartu Windows.

Po operacji aplikacja ponownie odczytuje rejestr instalacji HKLM/HKCU w widoku 32- i 64-bitowym. Wykrywanie Synology rozróżnia klientów Chat i Drive od innych produktów. Deinstalatory mogą usuwać ustawienia zgodnie z zasadami producentów.

Dzienniki UTF-8 znajdują się w `%LOCALAPPDATA%\Postinstall\Logs`. Menu „…” udostępnia stronę programu oraz informacje o stanie i licencji.

## Wymagania i kompilacja

Uruchomienie: Windows 10 1809+ lub Windows 11, .NET Framework 4.8/4.8.1, internet i uprawnienia administratora. Zalecany jest Windows x64 oraz spełnienie wymagań producentów. Aplikacja jest kompilowana jako AnyCPU z wyłączonym `Prefer32Bit`.

Kompilacja na Windows wymaga Visual Studio albo Build Tools z MSBuild, narzędziami .NET Desktop, .NET Framework 4.8 SDK i Targeting Pack. Z katalogu projektu uruchom:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Wynik: `dist\Postinstall.exe` i suma `dist\Postinstall.exe.sha256`. Skrypt wykonuje pełne `Rebuild`.

Ze środowiska Linux w tym workspace:

```bash
cd windows-wpf
python scripts/windows.py exec '& "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe" "\\host.lan\Data\projects\Postinstall\Postinstall.csproj" /t:Rebuild /p:Configuration=Release /nr:false'
```

## Testy

Testy integracyjne znajdują się w `Tests/IntegrationTests.csproj`. Wymagają Windows; WinGet i testy interfejsu uruchamiaj w sesji zalogowanego użytkownika. Tryb instalacyjny jest przeznaczony dla osobnej maszyny testowej.

| Tryb | Zakres |
| --- | --- |
| `--read-only` | Dokładny katalog, wykrywanie klientów Synology i ich wersji, wykluczenie innych produktów, .NET 4.8, argumenty procesów, propagacja błędów, odczyt rejestru, WinGet |
| `--sources` | Powyższe kontrole oraz dostępność wszystkich czterech identyfikatorów w rzeczywistym katalogu WinGet |
| `--lifecycle` | Rzeczywista instalacja, weryfikacja w rejestrze i odinstalowanie czterech programów; istniejące instalacje są pomijane |
| `--ui` | Cztery pozycje i poprawny licznik, wyszukiwanie Synology, zaznaczanie, pusty wynik, dziennik i rozmiar okna WPF |
| `--ui-queue` | Potwierdzenie „Zainstaluj wszystkie” z aktywnym filtrem obejmuje dokładnie aktualny katalog; anulowanie pozostawia interfejs gotowy |

Drugim argumentem jest ścieżka raportu JSON. Aktualne raporty znajdują się w `verification/1.1.0` w głównym workspace. Testy przeprowadzono na Windows 11 x64; nie wykonano osobnego przebiegu na Windows 10 ani ARM64. Nie sprawdzano konfiguracji połączenia z NAS.

Starsze raporty w `verification` dokumentują poprzednie wersje z innym katalogiem programów.

## Źródła

- [Microsoft: instalacja przez WinGet](https://learn.microsoft.com/en-us/windows/package-manager/winget/install)
- [Oficjalne wydania WinGet](https://github.com/microsoft/winget-cli/releases)
- [Synology Chat](https://www.synology.com/pl-pl/dsm/feature/chat)
- [Synology Drive](https://www.synology.com/pl-pl/dsm/feature/drive)
- [Manifesty Synology w katalogu WinGet](https://github.com/microsoft/winget-pkgs/tree/master/manifests/s/Synology)
