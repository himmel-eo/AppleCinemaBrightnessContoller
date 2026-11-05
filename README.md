# Apple Cinema Display Brightness Contoller (検証用ソース)

## 機能

- Apple USB Vendor ID `0x05AC` Product IDで検出
- HID Feature Report `0x10` で輝度読み書き（8/16bit、HID論理範囲に合わせる）
- 対応機種は Feature Report `0x66` で自動輝度のオン/オフ
- スライダー、タスクトレイ、Ctrl+Alt+上下キーの輝度調整（最初のモニター）
- 自動更新、外部通信、常駐サービス、OS自動起動設定なし

## ビルド

Windows 10/11 x64 + .NET 10 SDK が必要です。

```powershell
cd AppleCinemaBrightness
dotnet build -c Release
dotnet run -c Release
```

単体EXEを発行する場合：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

発行先：`bin\Release\net10.0-windows\win-x64\publish\`

## 注意
- Apple Displayの **USBケーブル** もPCに接続してください。映像ケーブルだけでは検出できません。
- 他の輝度調整アプリとの同時使用は避けてください。
- 管理者権限やPowerShellのExecutionPolicy変更は通常不要です。


Win32 HID APIおよびWindows UI APIのみを使います。
本ソースが安全に動くことを保証するものではありません。必要なら公開前にビルド・実機テスト・コードレビューを行ってください。
