namespace BossMod.Stormblood.Extreme.Ex5Rathalos;

[ConfigDisplay(Order = 0x110, Parent = typeof(StormbloodConfig))]
public class Ex5RathalosConfig() : ConfigNode()
{
    // 「刷鱗片」開啟後:天空王者結算前如果血量就已經低於40%,直接停止攻擊本體(整個禁止,
    // 不是降低輸出),改集火加魯拉,避免天空王者還沒放、本體就先被打死;天空王者結算後,墜地
    // 量表累積滿100(王倒地暈眩)時會優先砍尾巴。曾經嘗試過在等待量表累積期間強制只用最低
    // 威力技能來壓低輸出,但那個做法會一直對RSR送IPC指令、洗版Dalamud的log,而且實測下來
    // 也沒辦法可靠地真的壓住輸出,所以2026-09-06拿掉了——現在王者結算後到墜地之前是正常全力
    // 輸出,不會特別壓制,純粹只是量表進度的文字提示。
    [PropertyDisplay("極 火龍狩獵戰:刷鱗片模式(血量過低時避免打死本體、王倒地後優先砍尾巴)",
        tooltip: "天空王者結算前,如果血量已經低於下面設定的百分比,會直接停止攻擊本體,改集火加魯拉,避免本體提前被打死。\n" +
                 "天空王者結算後不會壓低輸出,正常全力打;墜地量表累積滿100(王倒地暈眩)時自動切換優先砍尾巴。\n" +
                 "血量門檻只在本體攻擊禁止那段生效,不影響一般輸出。\n" +
                 "測試環境:戰士/騎士,LV100、平均品級740Q,40%血量停火。")]
    public bool FarmScales = false;

    [PropertyDisplay("刷鱗片:天空王者前血量低於此百分比就停止攻擊本體(改集火加魯拉)",
        tooltip: "百分比,例如輸入 40 代表血量低於 40% 時停火。只在天空王者結算前生效,結算後不受這個數字影響。")]
    [PropertySlider(0f, 100f, Speed = 1f)]
    public float StopAttackHpPercent = 40f;
}
