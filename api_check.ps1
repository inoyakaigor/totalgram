Add-Type -Path "$env:USERPROFILE\.nuget\packages\wtelegramclient\4.4.8\lib\net8.0\WTelegramClient.dll"
Write-Host "=== DownloadFileAsync ==="
[WTelegram.Client].GetMethods() | Where-Object { $_.Name -eq 'DownloadFileAsync' } | ForEach-Object { Write-Host "  $_ -> $($_.ReturnType.Name)" ; $_.GetParameters() | ForEach-Object { Write-Host "    $($_.ParameterType.Name) $_.Name" } }
Write-Host "=== DocumentAttributeAudio ==="
[TL.DocumentAttributeAudio].GetFields() | ForEach-Object { "$($_.Name) -> $($_.FieldType.Name)" }
Write-Host "=== InputPeer types ==="
[System.AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { try { $_.GetTypes() | Where-Object { $_.FullName -match '^TL\.InputPeer' } | ForEach-Object { Write-Host $_.FullName } } catch {} }
Write-Host "=== InputPhotoFileLocation ==="
[TL.InputPhotoFileLocation].GetConstructors() | ForEach-Object { $_.GetParameters() | ForEach-Object { Write-Host "$($_.ParameterType.Name) $_.Name" } }
Write-Host "=== InputDocumentFileLocation ==="
[TL.InputDocumentFileLocation].GetConstructors() | ForEach-Object { $_.GetParameters() | ForEach-Object { Write-Host "$($_.ParameterType.Name) $_.Name" } }
