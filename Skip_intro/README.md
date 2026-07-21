# DIVE or DIE – Skip Intro

## English

Standalone BepInEx 5 preloader patcher for **DIVE or DIE – Children of Rain**.

### What it skips

- the startup cutscene and video path;
- splash video and audio playback;
- the demo welcome popup;
- the autosave/disclaimer transition.

The mod contains no localization code and does not require the Bulgarian Language project.

### Installation

1. Install BepInEx 5.4.23.5 for the Windows x64 Mono version of the game.
2. Extract the release archive into the game directory.
3. Confirm that the file is located at `BepInEx/patchers/DiveOrDieSkipIntroPatcher.dll`.

### Build from source

The build requires the .NET SDK with .NET Framework 4.7.2 targeting support. It does not require an installed copy of the game.

```powershell
.\scripts\build-patcher.ps1
.\scripts\package-release.ps1 -Version 0.1.0
```

### Settings after removing Bulgarian Language

Skip Intro does not modify language settings. If Bulgarian Language was previously active, reset its custom saved language index before disabling it. A leftover `languageIndex: 10` is invalid for the stock game's ten locales and can prevent Settings from initializing. Use the reset utility included with Bulgarian Language or change the saved index to `0`–`9` before uninstalling that project.

---

## Български

Самостоятелен BepInEx 5 мод за **DIVE or DIE – Children of Rain**, който прескача началните екрани.

### Какво прескача

- началната сцена и видеото;
- видеото и звука на началния екран;
- приветстващия прозорец в демоверсията;
- прехода с предупреждението за автоматично запазване.

Модът не съдържа код за локализация и не изисква проекта Bulgarian Language.

### Инсталиране

1. Инсталирай BepInEx 5.4.23.5 за Windows x64 Mono версията на играта.
2. Разархивирай изданието в папката на играта.
3. Провери дали файлът е на адрес `BepInEx/patchers/DiveOrDieSkipIntroPatcher.dll`.

### Компилиране от изходния код

Необходими са .NET SDK и поддръжка за .NET Framework 4.7.2. Не е нужно играта да е инсталирана.

```powershell
.\scripts\build-patcher.ps1
.\scripts\package-release.ps1 -Version 0.1.0
```

### Настройки след премахване на Bulgarian Language

Skip Intro не променя езиковите настройки. Ако Bulgarian Language е бил активен, върни запазения езиков индекс към стойност от `0` до `9`, преди да го изключиш. Останала стойност `languageIndex: 10` е невалидна за десетте езика на оригиналната игра и може да попречи на зареждането на менюто с настройки. Използвай помощния скрипт от Bulgarian Language или избери стандартен език, преди да премахнеш проекта.
