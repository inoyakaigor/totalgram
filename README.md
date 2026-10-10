# TotalGram

<img src="logo.png" alt="" width="180">

Плагин файловой системы (WFX) для Total Commander: чаты Telegram видны как папки,
файлы из них скачиваются и загружаются обратно обычными F5/F6.

Документация для того, кто ставит плагин, — в [TotalGram/README.md](TotalGram/README.md),
история версий — в [TotalGram/CHANGELOG.md](TotalGram/CHANGELOG.md). Оба файла едут
внутрь zip при сборке.

## Чем собирается

| Нужно | Зачем |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | целевая платформа `net10.0-windows` |
| Visual Studio Build Tools, workload **Desktop development with C++** | Native AOT компилирует через линкер MSVC |
| Python 3 (необязательно) | только для пересборки иконки из картинки |

## От чего зависит код

| Зависимость | Где |
|---|---|
| [WTelegramClient](https://github.com/wiz0u/WTelegramClient) 4.4.8 | NuGet, единственный пакет |
| [WFX-SDK](https://github.com/ghisler/WFX-SDK) | сабмодуль, только документация — не компилируется |

Рантайм .NET пользователю не нужен: плагин компилируется в нативный код (Native AOT).

## Сборка

```powershell
git clone --recurse-submodules <repo>
cd TotalGram
.\build_package.ps1
```

На выходе `dist\TotalGram-<версия>.zip` — архив авто-установки для Total Commander,
и россыпью те же бинарники в `dist\release\`. Версия берётся из времени сборки:
`yy.MM.dd.HHmm`.

Подробности сборки, включая две ловушки (`vswhere.exe` вне PATH и ручной список
экспортов для x86), — в разделе [Building](TotalGram/README.md#building).

## Что где лежит

```
TotalGram/          исходники плагина
  Core/             Telegram: аккаунты, чаты, поиск по типам, кэш листингов, передача файлов
  Wfx/              экспорты WFX и всё общение с Total Commander
  Models/           общие типы и имена виртуальных папок
WFX-SDK/            сабмодуль: официальный SDK Total Commander
build_package.ps1   сборка x86 + x64 и упаковка в zip
dist/               результат сборки, в git не хранится
```

## Правила репозитория

Каждая сборка получает запись в `TotalGram/CHANGELOG.md`: пишется под заголовком
`## Unreleased`, а `build_package.ps1` заменяет его на версию, которую собирает.
Подробнее — в [AGENTS.md](AGENTS.md), раздел 5.
