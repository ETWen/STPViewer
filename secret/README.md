# secret/ — 本機敏感資料集中地

本資料夾除 `README.md` 與 `*.example` 外全部 gitignored（規則見根目錄 `.gitignore`）。

## 內含物件

| 檔案 | git | 用途 |
|---|---|---|
| `README.md` | ✅ committed | 本說明 |
| `run-STPViewer.ps1.example` | ✅ committed | Windows 啟動腳本範本 |
| `run-STPViewer.sh.example` | ✅ committed | Git Bash 啟動腳本範本 |
| `run-STPViewer.ps1` / `.sh` | 🚫 ignored | 從範本複製後的本機實值版 |

> STPViewer 為離線桌面工具，目前**沒有任何 DB 密碼 / ApiKey**；
> 腳本僅做 build + run。未來若加入需要 secret 的功能（雲端授權、回報伺服器等），
> 依範本內註解加上 env var 注入。

## 第一次使用

```powershell
cd secret
Copy-Item run-STPViewer.ps1.example run-STPViewer.ps1
.\run-STPViewer.ps1
```
