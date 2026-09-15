using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace BossMod.Global.DeepDungeon;

/// <summary>
/// 對 PalacePal 的唯讀 IPC 包裝：拿它累積的陷阱與埋藏寶藏座標，補充 BMR 內建的那份表。
/// </summary>
/// <remarks>
/// <para>
/// 形狀刻意與 <see cref="DeepDungeonNav"/> 一致：每次呼叫都即時探測、失敗一律回「不可用」、
/// <b>不快取「可用」狀態</b>（使用者中途裝上／停用外掛都要能反應），
/// <c>IpcError</c> 安靜處理，其他例外記 <c>Information</c> 之後吞掉。
/// </para>
/// <para>
/// 🔴 <b>紅線</b>：唯讀。這裡不回寫任何東西給 PalacePal、不碰記憶體、不碰封包。
/// </para>
/// <para>
/// 🔴 <b>合約是雙方逐字約定的，不可以自己改名</b>：
/// <c>PalacePal.ApiVersion</c>（() → int，目前必須是 1）、
/// <c>PalacePal.GetTrapLocations</c>（(ushort territoryType) → List&lt;Vector3&gt;）、
/// <c>PalacePal.GetHoardLocations</c>（同上）。
/// 版本不是 1 就整條停用——寧可退回內建表，也不要照著一份語意可能已經變掉的資料走路。
/// </para>
/// <para>
/// ⚠️ <b>座標是「這個區域曾經出現過」的聯集，不是「這一層現在有」。</b>
/// 深牢一個 territory 含 10 層，而各層是用同一組版面在同一組世界座標上拼出來的，
/// 所以 PalacePal 的清單與 BMR 內建的 <see cref="GeneratedTrapData"/> 一樣是跨層聯集。
/// 陷阱照這個語意用是對的（內建表本來就是這樣用）；
/// <b>寶藏就必須標成「資料庫記載」而不是「這裡有」</b>。
/// </para>
/// </remarks>
static class PalacePalIpc
{
    /// <summary>本端支援的合約版本。對方回別的值就整條停用。</summary>
    public const int SupportedApiVersion = 1;

    private static void LogUnexpected(string endpoint, Exception ex)
        => Service.Logger.Information($"[DD pal] PalacePal.{endpoint} 擲出非 IPC 例外（已忽略，不影響 BMR）: {ex}");

    private static readonly Lazy<ICallGateSubscriber<int>?> ApiVersion =
        new(() => Service.PluginInterface?.GetIpcSubscriber<int>("PalacePal.ApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>?> TrapLocations =
        new(() => Service.PluginInterface?.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>?> HoardLocations =
        new(() => Service.PluginInterface?.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetHoardLocations"));

    /// <summary>
    /// 「現在真的看得到什麼」那組端點的版本線，與 <see cref="SupportedApiVersion"/> 各自獨立。
    /// </summary>
    /// <remarks>
    /// 🔑 這條判 <c>&gt;=</c>：對方新能力一律開新端點名，提高版本不代表既有語意變了。
    /// 🔴 舊那條的 <c>==</c> 是已經出貨的合約，不要跟著改。
    /// </remarks>
    public const int SupportedVisibleApiVersion = 1;

    private static readonly Lazy<ICallGateSubscriber<int>?> VisibleApiVersion =
        new(() => Service.PluginInterface?.GetIpcSubscriber<int>("PalacePal.VisibleLocationApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>?> VisibleHoardLocations =
        new(() => Service.PluginInterface?.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetVisibleHoardLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, int>?> VisibleAgeMillis =
        new(() => Service.PluginInterface?.GetIpcSubscriber<ushort, int>("PalacePal.GetVisibleLocationsAgeMillis"));

    /// <summary>
    /// 對方在不在、而且說得出我們認得的合約版本。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這個結果<b>不快取</b>。呼叫端要自己節流（見 <c>AutoClear</c> 的重整間隔），
    /// 別在每幀路徑上呼叫——沒安裝時 <c>InvokeFunc</c> 是靠擲例外回報的。
    /// </remarks>
    public static bool IsAvailable()
    {
        try
        {
            if (ApiVersion.Value is not { } g)
                return false;
            return g.InvokeFunc() == SupportedApiVersion;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("ApiVersion", ex);
            return false;
        }
    }

    /// <summary>這個區域已知的陷阱座標；null＝拿不到（沒裝／版本不合／對方出錯）。</summary>
    public static List<Vector3>? GetTraps(ushort territory) => Fetch(TrapLocations, "GetTrapLocations", territory);

    /// <summary>這個區域已知的埋藏寶藏座標；null＝拿不到。<b>目前沒有任何呼叫端</b>——這是刻意的。</summary>
    /// <remarks>
    /// 🔴🔴 <b>沒有呼叫端不是缺口，不要「順手接回去」。</b>
    /// 2026-08-10 使用者裁決把「PalacePal 資料庫記載的埋藏寶藏」從小地圖與世界疊加層雙雙移除，
    /// 原話：「埋藏寶藏 地圖不用放預測 你這不是每一格都畫了嗎」。
    /// 兩個獨立理由：①這份清單是<b>整座深牢跨樓層的聯集</b>（見本類別上方的座標語意說明），
    /// 攤到單層 25 格的小地圖上幾乎格格命中，等於零資訊的噪音；
    /// ②PalacePal 本身就會畫自己的世界標記，BMR 再畫一份是雙份。
    /// <para>
    /// 📌 包裝留著只是為了「合約仍然完整、要用時不必重接」，<b>不是</b>待辦。
    /// 陷阱那一半（<see cref="GetTraps"/>）照舊在用——那份餵的是迴避決策，有真實價值。
    /// </para>
    /// </remarks>
    public static List<Vector3>? GetHoards(ushort territory) => Fetch(HoardLocations, "GetHoardLocations", territory);

    /// <summary>對方有沒有「現在真的看得到什麼」那組端點。沒裝／舊版都回 false，不擲例外。</summary>
    public static bool IsVisibleApiAvailable()
    {
        try
        {
            if (VisibleApiVersion.Value is not { } g)
                return false;
            return g.InvokeFunc() >= SupportedVisibleApiVersion;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("VisibleLocationApiVersion", ex);
            return false;
        }
    }

    /// <summary>
    /// PalacePal 這一幀真的看得到的埋藏寶藏座標；<c>null</c>＝不知道（沒裝／舊版／快照太舊）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>null</c>＝不知道，空清單＝知道而且是 0 個，呼叫端不可以把兩者當成同一件事。
    /// 🔑 先問快照年齡再要座標：PalacePal 裝著但沒在深牢裡跑時端點照樣在，回的是殘影。
    /// </remarks>
    public static List<Vector3>? GetVisibleHoards(ushort territory, int maxAgeMillis)
    {
        if (!IsVisibleApiAvailable())
            return null;

        try
        {
            if (VisibleAgeMillis.Value is not { } age)
                return null;
            var ms = age.InvokeFunc(territory);
            if (ms < 0 || ms > maxAgeMillis)
                return null;
            return VisibleHoardLocations.Value?.InvokeFunc(territory);
        }
        catch (IpcError)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogUnexpected("GetVisibleHoardLocations", ex);
            return null;
        }
    }

    /// <remarks>
    /// 🔴 <b>先問版本再取資料</b>，而不是「取到東西就用」。端點名稱可能被別的外掛佔用，
    /// 也可能是舊版 PalacePal 用同名端點回傳不同語意的東西——那種情況下拿到的是
    /// 一份長得很正常但意義不同的座標清單，失敗形式是安靜地畫錯／閃錯地方。
    /// </remarks>
    private static List<Vector3>? Fetch(Lazy<ICallGateSubscriber<ushort, List<Vector3>>?> gate, string endpoint, ushort territory)
    {
        if (!IsAvailable())
            return null;

        try
        {
            return gate.Value?.InvokeFunc(territory);
        }
        catch (IpcError)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            return null;
        }
    }
}
