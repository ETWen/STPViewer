using System;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace STPViewer;

/// <summary>
/// 關於視窗：左 = 程式資訊 / 開發者 / 技術棧，右 = 版本紀錄（寫給一般使用者看，非技術 changelog）。
/// 樣式參考 ETTerms 的 AboutView。發新版時在 Changelog 陣列最上面加一筆。
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Version v = Assembly.GetExecutingAssembly().GetName().Version!;
        VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        ChangelogList.ItemsSource = Changelog.Select(e => new
        {
            Header = $"v{e.Version}  ·  {e.Title}    ({e.Date:yyyy-MM-dd})",
            e.Changes,
        }).ToList();
    }

    private record ChangelogEntry(string Version, DateOnly Date, string Title, string[] Changes);

    // ── 版本紀錄（新版加最上面；口吻：一般使用者看得懂，講「你會感覺到什麼」而非實作細節）──
    private static readonly ChangelogEntry[] Changelog =
    {
        new("0.7.0", new DateOnly(2026, 7, 4), "介面大整理 — 選單列＋可自訂快速列",
        new[]
        {
            "• 上方新增選單列，全部功能依「檔案／視圖／量測／變換／工具」分類，不用再在一長排按鈕裡找。",
            "• 原本的工具列變成「快速列」：只放常用按鈕。想放哪些自己決定 — 到「快速列 → 自訂按鈕」勾選，下次開啟會記住。",
            "• 剖面的軸向／位置控制列改成只在開啟剖面時出現，平常不佔空間。",
            "• 新增「關於」視窗（就是這個畫面），可以看版本紀錄。",
        }),
        new("0.6.0", new DateOnly(2026, 7, 4), "匯出 STL",
        new[]
        {
            "• 新增匯出 STL：把畫面上的零件（含對齊、旋轉後的位置）存成 STL 網格檔，可拿去 3D 列印或給其他軟體用。",
            "• 匯出前會先跳出設定視窗：全部合併成一個檔或每個檔案各存一個、Binary 或文字格式、mm 或 inch、網格精細度 — 確認後才匯出。",
            "• 設定視窗會即時顯示三角形數量與預估檔案大小。",
        }),
        new("0.5.0", new DateOnly(2026, 7, 3), "量測、視圖、樹狀面板大升級",
        new[]
        {
            "• 量距離時點到圓孔邊緣會自動吸到圓心 — 量兩孔中心距（pitch）超方便。",
            "• 量測快速鍵：P 點、D 距離、E 邊、F 面、C 圓、A 角度、M 面距；Esc 取消。",
            "• 樹狀面板可搜尋零件名稱；右鍵可「只顯示此節點」隔離觀察，也可以量體積與質心。",
            "• 新增標準視圖（等角／前／上／右）與正交投影，量尺寸不受透視變形。",
            "• 剖面新增「3點」模式：在模型上點 3 個點，就能沿任意斜面剖開。",
            "• 新增匯出 STEP：把對齊好的零件位置存成新 STEP 檔，交接給正式 CAD。",
            "• 會記住視窗大小、單位設定與最近開啟的 10 個檔案。",
        }),
        new("0.4.0", new DateOnly(2026, 7, 2), "更快、更穩",
        new[]
        {
            "• 大檔案拉剖面滑桿不再卡住畫面（改到背景計算）。",
            "• 拖曳／操作器放開零件後的定位計算大幅加速。",
            "• 程式遇到錯誤會提示訊息並繼續運作，不再整個閃退。",
        }),
        new("0.3.2", new DateOnly(2026, 6, 18), "大組件量測不再卡",
        new[]
        {
            "• 修正大型組件（數萬個面）進入量測模式後，轉動視角嚴重卡頓的問題 — 現在量測跟瀏覽一樣順。",
        }),
        new("0.3.1", new DateOnly(2026, 6, 13), "操作器永遠可見",
        new[]
        {
            "• 三軸操作器改成永遠浮在畫面最上層，不會被零件擋住，任何角度都抓得到。",
        }),
        new("0.3.0", new DateOnly(2026, 6, 13), "三軸操作器",
        new[]
        {
            "• 新增操作器：選取檔案後出現 XYZ 三色箭頭與旋轉環，箭頭沿軸精確移動、旋轉環繞軸轉任意角度。",
        }),
        new("0.2.0", new DateOnly(2026, 6, 13), "拖曳模式",
        new[]
        {
            "• 新增拖曳模式：左鍵按住零件直接拖到想要的位置，放開就定位。",
            "• 0.2.1 修正拖曳過的零件再點擊會當掉的問題。",
        }),
        new("0.1.0", new DateOnly(2026, 6, 13), "首次釋出",
        new[]
        {
            "• STEP／STL／DXF 匯入與裝配樹、點／距離／邊／面／圓／角度／面距量測、兩點與三點對齊、干涉檢查、剖面、mm ⇄ inch、CSV 與截圖匯出。",
        }),
    };
}
