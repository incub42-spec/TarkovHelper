# Тексты релизов

Здесь лежит то, что написано в описании каждого выпуска на GitHub. Сам
выпуск — источник для игрока, а эта папка нужна, чтобы историю изменений
можно было читать и искать, не выходя из репозитория, и чтобы она не
зависела от того, доступен ли GitHub.

Файлы названы по версии и повторяют опубликованный текст дословно; шапка с
датой и ссылкой добавлена сверху.

## Как собирается выпуск

```
dotnet publish TarkovHelper -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
gh release create vX.Y.Z <путь к TarkovHelper.exe> --title "TarkovHelper X.Y.Z" --notes-file docs/releases/X.Y.Z.md
```

Готовый `TarkovHelper.exe` остаётся в `TarkovHelper/bin/Release/.../publish/`
и в репозиторий не попадает: собранные версии хранит GitHub, и держать их
копии по папкам не нужно.
