namespace BossMod.Stormblood.Extreme.Ex5Rathalos;

[ConfigDisplay(Order = 0x110, Parent = typeof(StormbloodConfig))]
public class Ex5RathalosConfig() : ConfigNode()
{
    // 刻意不做:等待量表累積期間不壓低輸出 —— 實測壓不住 RSR 的循環,只是一直送 IPC 洗 log。
    // 本選項只做兩件事:天空王者前血量過低時整個停止攻擊本體,以及倒地後改砍尾巴。
    [PropertyDisplay("極 火龍狩獵戰:刷鱗片模式(血量過低時避免打死本體、王倒地後優先砍尾巴)",
        tooltip: "天空王者結算前,如果血量已經低於下面設定的百分比,會直接停止攻擊本體,改集火加魯拉,避免本體提前被打死。\n" +
                 "天空王者結算後不會壓低輸出,正常全力打;墜地量表累積滿100(王倒地暈眩)時自動切換優先砍尾巴。\n" +
                 "血量門檻只在本體攻擊禁止那段生效,不影響一般輸出。")]
    public bool FarmScales = false;

    [PropertyDisplay("刷鱗片:天空王者前血量低於此百分比就停止攻擊本體(改集火加魯拉)",
        tooltip: "百分比,例如輸入 40 代表血量低於 40% 時停火。只在天空王者結算前生效,結算後不受這個數字影響。")]
    [PropertySlider(0f, 100f, Speed = 1f)]
    public float StopAttackHpPercent = 40f;
}
