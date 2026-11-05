using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Components;
using Dalamud.Memory;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.EzHookManager;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using PandorasBox.FeaturesSetup;
using PandorasBox.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static FFXIVClientStructs.FFXIV.Client.Game.ActionManager;
using System.Diagnostics;

namespace PandorasBox.Features
{
    public abstract class BaseFeature
    {
        protected Configuration? config;
        protected TaskManager TaskManager = null!;
        public FeatureProvider Provider { get; private set; } = null!;

        public virtual bool Enabled { get; protected set; }

        public virtual bool FeatureDisabled { get; protected set; } //This is to disable features that don't work

        public virtual string DisabledReason { get; set; } = "";

        public abstract string Name { get; }

        public virtual string Key => GetType().Name;

        public abstract string Description { get; }

        public static readonly SeString PandoraPayload = new SeString(new UIForegroundPayload(32)).Append($"{SeIconChar.BoxedLetterP.ToIconString()}{SeIconChar.BoxedLetterA.ToIconString()}{SeIconChar.BoxedLetterN.ToIconString()}{SeIconChar.BoxedLetterD.ToIconString()}{SeIconChar.BoxedLetterO.ToIconString()}{SeIconChar.BoxedLetterR.ToIconString()}{SeIconChar.BoxedLetterA.ToIconString()} ").Append(new UIForegroundPayload(0));
        public virtual void Draw() { }
        public virtual bool DrawConditions() { return false; }

        public virtual bool Ready { get; protected set; }

        public abstract FeatureType FeatureType { get; }

        protected Stopwatch AFKTimer { get; private set; } = new Stopwatch();
        protected bool UseAFKTimer { get; set; } = false;

        protected bool IsAFK(int minutes = 5)
        {
            if (!UseAFKTimer) return false;
            return AFKTimer.Elapsed.TotalMinutes >= minutes;
        }

        public void InterfaceSetup(PandorasBox plugin, IDalamudPluginInterface pluginInterface, Configuration config, FeatureProvider fp)
        {
            this.config = config;
            this.Provider = fp;
            this.TaskManager = new(new() { TimeoutSilently = true, ShowDebug = !UseAFKTimer });
        }

        public virtual void Setup()
        {
            Ready = true;
        }

        public virtual void Enable()
        {
            Svc.Log.Debug($"Enabling {Name}");
            Enabled = true;
            if (UseAFKTimer)
                Svc.Framework.Update += UpdateTimer;
        }

        public virtual void Disable()
        {
            TaskManager!.Abort();
            Enabled = false;
            Svc.Framework.Update -= UpdateTimer;
        }

        private void UpdateTimer(IFramework framework)
        {
            if ((Player.Available && Player.IsMoving) || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
            {
                if (!AFKTimer.IsRunning)
                    AFKTimer.Restart();
            }
            else
                AFKTimer.Reset();
        }

        public virtual void Dispose()
        {
            Ready = false;
        }

        protected T? LoadConfig<T>() where T : FeatureConfig? => LoadConfig<T>(Key);

        protected T? LoadConfig<T>(string key) where T : FeatureConfig?
        {
            try
            {
                var configDirectory = Svc.PluginInterface.GetPluginConfigDirectory();
                var configFile = Path.Combine(configDirectory, key + ".json");
                if (!File.Exists(configFile)) return default;
                var jsonString = File.ReadAllText(configFile);
                return JsonConvert.DeserializeObject<T>(jsonString);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"Failed to load config for feature {Name}");
                return default;
            }
        }

        protected void SaveConfig<T>(T config) where T : FeatureConfig? => SaveConfig(config, this.Key);

        protected void SaveConfig<T>(T config, string key) where T : FeatureConfig?
        {
            try
            {
                var configDirectory = Svc.PluginInterface.GetPluginConfigDirectory();
                var configFile = Path.Combine(configDirectory, key + ".json");
                var jsonString = JsonConvert.SerializeObject(config, Formatting.Indented);

                File.WriteAllText(configFile, jsonString);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"Feature failed to write config {this.Name}");
            }
        }

        private void DrawAutoConfig()
        {
            var configChanged = false;
            try
            {
                // ReSharper disable once PossibleNullReferenceException
                var configObj = this.GetType().GetProperties().FirstOrDefault(p => p.PropertyType.IsSubclassOf(typeof(FeatureConfig)))?.GetValue(this);


                var fields = configObj?.GetType().GetFields()
                    .Where(f => f.GetCustomAttribute(typeof(FeatureConfigOptionAttribute)) != null)
                    .Select(f => (f, f.GetCustomAttribute(typeof(FeatureConfigOptionAttribute)) as FeatureConfigOptionAttribute))
                    .OrderBy(a => a.Item2?.Priority).ThenBy(a => a.Item2?.Name);

                if (fields is null) return;

                var configOptionIndex = 0;
                foreach (var (f, attr) in fields)
                {
                    if (attr is null) continue;
                    if (attr.Disabled)
                        ImGui.BeginDisabled();

                    if (attr.ConditionalDisplay)
                    {
                        var conditionalMethod = configObj?.GetType().GetMethod($"ShouldShow{f.Name}", BindingFlags.Public | BindingFlags.Instance);
                        if (conditionalMethod != null)
                        {
                            var shouldShow = (bool)(conditionalMethod.Invoke(configObj, Array.Empty<object>()) ?? true);
                            if (!shouldShow) continue;
                        }
                    }

                    if (attr.SameLine) ImGui.SameLine();

                    if (attr.Editor != null)
                    {
                        var v = f.GetValue(configObj);
                        var arr = new[] { $"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", v };
                        var o = (bool)attr.Editor.Invoke(null, arr)!;
                        if (o)
                        {
                            configChanged = true;
                            f.SetValue(configObj, arr[1]);
                        }
                    }
                    else if (f.FieldType == typeof(bool))
                    {
                        var v = (bool)f.GetValue(configObj)!;
                        if (ImGui.Checkbox($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v))
                        {
                            configChanged = true;
                            f.SetValue(configObj, v);
                        }
                    }
                    else if (f.FieldType == typeof(int))
                    {
                        var v = (int)f.GetValue(configObj)!;
                        ImGui.SetNextItemWidth(attr.EditorSize == -1 ? -1 : attr.EditorSize * ImGui.GetIO().FontGlobalScale);
                        var e = attr.IntType switch
                        {
                            FeatureConfigOptionAttribute.NumberEditType.Slider => ImGui.SliderInt($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, attr.IntMin, attr.IntMax),
                            FeatureConfigOptionAttribute.NumberEditType.Drag => ImGui.DragInt($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, 1f, attr.IntMin, attr.IntMax),
                            _ => false
                        };

                        if (v % attr.IntIncrements != 0)
                        {
                            v = v.RoundOff(attr.IntIncrements);
                            if (v < attr.IntMin) v = attr.IntMin;
                            if (v > attr.IntMax) v = attr.IntMax;
                        }

                        if (attr.EnforcedLimit && v < attr.IntMin)
                        {
                            v = attr.IntMin;
                            e = true;
                        }

                        if (attr.EnforcedLimit && v > attr.IntMax)
                        {
                            v = attr.IntMax;
                            e = true;
                        }

                        if (e)
                        {
                            f.SetValue(configObj, v);
                            configChanged = true;
                        }
                    }
                    else if (f.FieldType == typeof(float))
                    {
                        var v = (float)f.GetValue(configObj)!;
                        ImGui.SetNextItemWidth(attr.EditorSize == -1 ? -1 : attr.EditorSize * ImGui.GetIO().FontGlobalScale);
                        var e = attr.IntType switch
                        {
                            FeatureConfigOptionAttribute.NumberEditType.Slider => ImGui.SliderFloat($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, attr.FloatMin, attr.FloatMax, attr.Format),
                            FeatureConfigOptionAttribute.NumberEditType.Drag => ImGui.DragFloat($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, 1f, attr.FloatMin, attr.FloatMax, attr.Format),
                            _ => false
                        };

                        if (v % attr.FloatIncrements != 0)
                        {
                            v = v.RoundOff(attr.FloatIncrements);
                            if (v < attr.FloatMin) v = attr.FloatMin;
                            if (v > attr.FloatMax) v = attr.FloatMax;
                        }

                        if (attr.EnforcedLimit && v < attr.FloatMin)
                        {
                            v = attr.FloatMin;
                            e = true;
                        }

                        if (attr.EnforcedLimit && v > attr.FloatMax)
                        {
                            v = attr.FloatMax;
                            e = true;
                        }

                        if (e)
                        {
                            f.SetValue(configObj, v);
                            configChanged = true;
                        }
                    }
                    else
                    {
                        ImGui.Text($"Invalid Auto Field Type: {f.Name}");
                    }

                    if (attr.Disabled)
                    {
                        ImGui.EndDisabled();
                        ImGuiComponents.HelpMarker("Currently Disabled");
                    }

                }

                if (configChanged)
                {
                    SaveConfig((FeatureConfig)configObj!);
                }

            }
            catch (Exception ex)
            {
                ImGui.Text($"Error with AutoConfig: {ex.Message}");
                ImGui.TextWrapped($"{ex.StackTrace}");
            }
        }

        public virtual bool UseAutoConfig => false;

        public string LocalizedName => GetType().Name switch
        {
            "AutoFairy" => "自動召喚小仙女／寶石獸",
            "AutoCollect" => "自動開啟收藏品採集",
            "AutoMeditation" => "自動使用鬥氣",
            "AutoChocobo" => "自動召喚陸行鳥",
            "AutoCordial" => "自動使用恢復藥",
            "AutoMotif" => "自動繪製畫題（非戰鬥中）",
            "AutoMountCombat" => "戰鬥結束後自動騎乘",
            "AutoMountGathering" => "採集後自動騎乘",
            "AutoMountTeleport" => "區域切換後自動騎乘",
            "AutoPeloton" => "自動使用速行",
            "AutoProspectTriangulate" => "自動使用山師之眼／樹木之聲",
            "AutoSprint" => "休息區自動疾跑",
            "SanctuarySprint" => "無人島自動使用島嶼疾跑",
            "AutoTankStance" => "自動開啟防禦姿態",
            "HotbarMapDecipher" => "快捷欄藏寶圖解讀",
            "PreserveMapLinks" => "保留剪貼簿中的地圖連結",
            "AutoTPCoords" => "自動傳送至地圖座標",
            "AutoOpenCoords" => "自動開啟聊天中的地圖座標",
            "CallbackCommand" => "回呼指令",
            "Interact" => "與目前目標互動",
            "ItemLink" => "物品連結",
            "LeaveDuty" => "快速離開副本",
            "MSQCountdown" => "主線任務計數",
            "ResetEnmity" => "重置仇恨",
            "UseItem" => "使用物品",
            "QuickPlateLink" => "快速連結投影台",
            "AfkDummyReset" => "木人閒置重置",
            "ATMSolver" => "自動完成極限技操作",
            "AutoRecommended" => "自動裝備最強裝備",
            "AutoSwitchGatherer" => "自動切換採集職業",
            "AutoSyncFate" => "危命任務自動等級同步",
            "PandoraGathering" => "Pandora 快速採集",
            "TurboController" => "控制器連發",
            "FATETargeting" => "危命任務鎖定模式",
            "AutoOpenChests" => "自動開啟寶箱",
            "AutoInteractGathering" => "自動與採集點互動",
            "AutoInteractDungeons" => "自動與副本物件互動",
            "ActionTargeting" => "動作戰鬥鎖定",
            "PartyFinderShowMore" => "招募隊員搜尋顯示更多項目",
            "MergeStacks" => "自動合併相同物品",
            "Desynth" => "全部分解",
            "ExtractAll" => "取出全部魔晶石",
            "AutoSelectGardening" => "自動選擇園藝土壤／種子",
            "ReduceAll" => "全部精選",
            "GCVendorDefault" => "預設軍票交換商店頁籤",
            "AutoVoteMvp" => "副本結束後自動投票推薦",
            "TradeAllCollectibles" => "一次交納全部收藏品",
            "EntrustChocoboDuplicates" => "陸行鳥鞍囊存放重複物品",
            "OpenAllCoffers" => "寶箱新增「全部開啟」",
            "AutoJoinPF" => "自動加入招募隊伍",
            "QuickLowerQuality" => "快速降低品質",
            "DefaultVendorValue" => "商店預設購買數量",
            "FCChestQuickDeposit" => "部隊儲物櫃快速存放",
            "EnableYesButtons" => "自動啟用「是」按鈕",
            "RepairAll" => "替換全部修理",
            "RefreshPF" => "自動重新整理招募隊員搜尋",
            "AutoSelectTurnin" => "自動選擇交納物品",
            "SimplifyRelicQuest" => "簡化古武任務領取",
            _ => Name,
        };

        public string LocalizedDescription => GetType().Name switch
        {
            "AutoFairy" => "切換為學者或召喚師時，自動召喚小仙女或寶石獸。",
            "AutoCollect" => "切換為捕魚人時，自動保持收藏品採集狀態。",
            "AutoMeditation" => "非戰鬥中自動使用鬥氣。",
            "AutoChocobo" => "在野外未召喚陸行鳥時，自動使用基薩爾野菜。",
            "AutoCordial" => "採集力低於設定值時，自動使用恢復藥。",
            "AutoMotif" => "非戰鬥且不在休息區時，自動繪製畫題。",
            "AutoMountCombat" => "戰鬥結束後自動騎乘坐騎。",
            "AutoMountGathering" => "完成採集後自動騎乘；移動中會在延遲後持續嘗試最多 3 秒。",
            "AutoMountTeleport" => "區域切換後，若尚未騎乘則自動騎乘。",
            "AutoPeloton" => "非戰鬥中自動使用速行（僅限遠程物理職業）。",
            "AutoProspectTriangulate" => "切換為採礦工或園藝工時，自動開啟另一採集職業的探索技能。",
            "AutoSprint" => "位於可累積休息經驗值的區域時自動疾跑。",
            "SanctuarySprint" => "在無人島自動使用島嶼疾跑。",
            "AutoTankStance" => "切換職業或進入副本時，自動開啟坦克防禦姿態。",
            "HotbarMapDecipher" => "允許直接從快捷欄解讀藏寶圖。",
            "PreserveMapLinks" => "複製聊天訊息時保留地圖連結格式，使貼上後仍可互動。",
            "AutoTPCoords" => "自動傳送至聊天地圖連結附近的乙太之光；需要 Teleporter 插件。",
            "AutoOpenCoords" => "聊天中出現座標時，自動開啟對應地圖。",
            "CallbackCommand" => "向指定介面元件送出自訂回呼；請謹慎使用。",
            "Interact" => "與目前選取的目標互動。",
            "ItemLink" => "搜尋物品並產生可點擊的物品連結。",
            "LeaveDuty" => "快速離開目前副本。",
            "MSQCountdown" => "在聊天視窗顯示目前資料片尚未完成的主線任務數量。",
            "ResetEnmity" => "重置與木人的戰鬥狀態。",
            "UseItem" => "使用物品欄中的指定物品。",
            "QuickPlateLink" => "快速將多個套裝與投影台連結。",
            "AfkDummyReset" => "在設定時間內未執行動作時，自動重置木人仇恨。",
            "ATMSolver" => "畫面出現特殊操作時，自動按鍵或連打。",
            "AutoRecommended" => "切換職業後自動裝備最強裝備。",
            "AutoSwitchGatherer" => "接近採集點時切換為適合的採集職業；必須已有對應套裝。",
            "AutoSyncFate" => "以過高等級進入危命任務時自動同步。",
            "PandoraGathering" => "以可隨時中止並保留設定的強化快速採集取代原功能。",
            "TurboController" => "按住控制器按鈕時啟用連發。",
            "FATETargeting" => "已同步參與危命任務時，自動鎖定相關敵人。",
            "AutoOpenChests" => "靠近寶箱時自動開啟。",
            "AutoInteractGathering" => "使用正確職業靠近採集點時自動互動。",
            "AutoInteractDungeons" => "在副本中自動拾取鑰匙、操作機關及開門（高度實驗性）。",
            "ActionTargeting" => "自動鎖定並切換至視線範圍內最近的目標。",
            "PartyFinderShowMore" => "將招募隊員搜尋顯示上限由 50 提升至遊戲允許的 100。",
            "MergeStacks" => "開啟物品欄時，自動合併相同物品的未滿堆疊。",
            "Desynth" => "在分解視窗新增按鈕，以分解目前分類中的全部物品；請謹慎使用。",
            "ExtractAll" => "在取出魔晶石視窗新增全部取出按鈕。",
            "AutoSelectGardening" => "自動在園藝介面填入種子與土壤。",
            "ReduceAll" => "在精選介面新增處理全部物品的按鈕。",
            "GCVendorDefault" => "開啟軍票交換商店時設定預設頁籤。",
            "AutoVoteMvp" => "副本結束時，自動隨機推薦一名小隊成員。",
            "TradeAllCollectibles" => "將收藏品介面的交納按鈕替換為交納所選收藏品的全部數量。",
            "EntrustChocoboDuplicates" => "在陸行鳥鞍囊底部新增存放重複物品按鈕。",
            "OpenAllCoffers" => "在可堆疊且可開啟的物品選單新增「全部開啟」。",
            "AutoJoinPF" => "點擊招募項目時略過說明視窗並自動點擊加入。",
            "QuickLowerQuality" => "自動確認降低品質的提示視窗。",
            "DefaultVendorValue" => "設定向商店購買物品時的預設數量。",
            "FCChestQuickDeposit" => "開啟部隊儲物櫃時，在物品選單新增快速存放功能。",
            "EnableYesButtons" => "需要勾選確認方塊的提示中，自動啟用「是」按鈕。",
            "RepairAll" => "讓全部修理按鈕忽略目前分類，修理所有裝備。",
            "RefreshPF" => "依設定間隔自動重新整理招募隊員搜尋。",
            "AutoSelectTurnin" => "需要選擇交納物品時，自動填入介面。",
            "SimplifyRelicQuest" => "與 NPC 對話時顯示古武任務所屬階段。",
            _ => Description,
        };

        public bool DrawConfig(ref bool hasChanged)
        {
            var configTreeOpen = false;
            if ((UseAutoConfig || DrawConfigTree != null) && Enabled)
            {
                var x = ImGui.GetCursorPosX();
                if (ImGui.TreeNode($"{LocalizedName}##treeConfig_{GetType().Name}"))
                {
                    configTreeOpen = true;
                    ImGui.SetCursorPosX(x);
                    ImGui.BeginGroup();
                    if (UseAutoConfig)
                        DrawAutoConfig();
                    else
                        DrawConfigTree!(ref hasChanged);
                    ImGui.EndGroup();
                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0x0);
                ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0x0);
                ImGui.TreeNodeEx(LocalizedName, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
                ImGui.PopStyleColor();
                ImGui.PopStyleColor();
            }

            if (hasChanged && Enabled) ConfigChanged();
            return configTreeOpen;
        }

        protected delegate void DrawConfigDelegate(ref bool hasChanged);
        protected virtual DrawConfigDelegate? DrawConfigTree => null;

        protected virtual void ConfigChanged()
        {
            if (this is null) return;

            var config = this.GetType().GetProperties().FirstOrDefault(p => p.PropertyType.IsSubclassOf(typeof(FeatureConfig)));

            if (config != null)
            {
                var configObj = config.GetValue(this);
                if (configObj != null)
                    SaveConfig((FeatureConfig)configObj);
            }
        }

        protected void Log(string msg) => Svc.Log.Debug($"[{Name}] {msg}");

        public unsafe bool IsRpWalking()
        {
            return Control.Instance()->IsWalking;
        }

        internal static unsafe int GetInventoryFreeSlotCount()
        {
            var types = new InventoryType[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
            var c = InventoryManager.Instance();
            var slots = 0;
            foreach (var x in types)
            {
                var inv = c->GetInventoryContainer(x);
                for (var i = 0; i < inv->Size; i++)
                {
                    if (inv->Items[i].ItemId == 0)
                    {
                        slots++;
                    }
                }
            }
            return slots;
        }

        internal static unsafe bool IsTargetLocked => *(byte*)(((nint)TargetSystem.Instance()) + 309) == 1;
        internal static bool IsInventoryFree()
        {
            return GetInventoryFreeSlotCount() >= 1;
        }

        public unsafe bool IsMoving() => Player.IsMoving;

        public void PrintModuleMessage(string msg)
        {
            var message = new XivChatEntry
            {
                Message = new SeStringBuilder()
                .AddUiForeground($"[{P.Name}] ", 45)
                .AddUiForeground($"[{Name}] ", 62)
                .AddText(msg)
                .Build()
            };

            Svc.Chat.Print(message);
        }

        public void PrintModuleMessage(SeString msg)
        {
            var message = new XivChatEntry
            {
                Message = new SeStringBuilder()
                .AddUiForeground($"[{P.Name}] ", 45)
                .AddUiForeground($"[{Name}] ", 62)
                .Append(msg)
                .Build()
            };

            Svc.Chat.Print(message);
        }

        private const int UnitListCount = 18;
        public unsafe AtkUnitBase* GetAddonByID(uint id)
        {
            var unitManagers = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager.DepthLayerOneList;
            for (var i = 0; i < UnitListCount; i++)
            {
                var unitManager = &unitManagers[i];
                foreach (var j in Enumerable.Range(0, Math.Min(unitManager->Count, unitManager->Entries.Length)))
                {
                    var unitBase = unitManager->Entries[j].Value;
                    if (unitBase != null && unitBase->Id == id)
                    {
                        return unitBase;
                    }
                }
            }

            return null;
        }

        public unsafe bool IsActionUnlocked(uint id)
        {
            var unlockLink = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(id).UnlockLink.RowId;
            if (unlockLink == 0) return true;
            return UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(unlockLink);
        }

        internal static unsafe AtkUnitBase* GetSpecificYesno(Predicate<string> compare)
        {
            for (var i = 1; i < 100; i++)
            {
                try
                {
                    var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                    if (addon == null) return null;
                    if (GenericHelpers.IsAddonReady(addon))
                    {
                        var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                        var text = MemoryHelper.ReadSeString(&textNode->NodeText).GetText();
                        if (compare(text))
                        {
                            Svc.Log.Verbose($"SelectYesno {text} addon {i} by predicate");
                            return addon;
                        }
                    }
                }
                catch (Exception e)
                {
                    Svc.Log.Error("", e);
                    return null;
                }
            }
            return null;
        }

        internal static unsafe AtkUnitBase* GetSpecificYesno(params string[] s)
        {
            for (var i = 1; i < 100; i++)
            {
                try
                {
                    var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                    if (addon == null) return null;
                    if (GenericHelpers.IsAddonReady(addon))
                    {
                        var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                        var text = MemoryHelper.ReadSeString(&textNode->NodeText).GetText().Replace(" ", "");
                        if (text.EqualsAny(s.Select(x => x.Replace(" ", ""))))
                        {
                            Svc.Log.Verbose($"SelectYesno {s.Print()} addon {i}");
                            return addon;
                        }
                    }
                }
                catch (Exception e)
                {
                    Svc.Log.Error("", e);
                    return null;
                }
            }
            return null;
        }

        internal static bool TrySelectSpecificEntry(string text, Func<bool>? Throttler = null)
        {
            return TrySelectSpecificEntry(new string[] { text }, Throttler);
        }

        internal static unsafe bool TrySelectSpecificEntry(IEnumerable<string> text, Func<bool>? Throttler = null)
        {
            if (GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
            {
                var entry = GetEntries(addon).FirstOrDefault(x => x.ContainsAny(text));
                if (entry != null)
                {
                    var index = GetEntries(addon).IndexOf(entry);
                    if (index >= 0 && IsSelectItemEnabled(addon, index) && (Throttler?.Invoke() ?? GenericThrottle))
                    {
                        new AddonMaster.SelectString((nint)addon).Entries[index].Select();
                        Svc.Log.Debug($"TrySelectSpecificEntry: selecting {entry}/{index} as requested by {text.Print()}");
                        return true;
                    }
                }
            }
            else
            {
                RethrottleGeneric();
            }
            return false;
        }

        internal static unsafe bool IsSelectItemEnabled(AddonSelectString* addon, int index)
        {
            var step1 = (AtkTextNode*)addon->AtkUnitBase
                        .UldManager.NodeList[2]
                        ->GetComponent()->UldManager.NodeList[index + 1]
                        ->GetComponent()->UldManager.NodeList[3];
            return GenericHelpers.IsSelectItemEnabled(step1);
        }

        internal static unsafe List<string> GetEntries(AddonSelectString* addon)
        {
            var list = new List<string>();
            for (var i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
            {
                list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText());
            }
            return list;
        }

        internal static bool GenericThrottle => EzThrottler.Throttle("PandorasBoxGenericThrottle", 200);
        internal static void RethrottleGeneric(int num) => EzThrottler.Throttle("PandorasBoxGenericThrottle", num, true);
        internal static void RethrottleGeneric() => EzThrottler.Throttle("PandorasBoxGenericThrottle", 200, true);

        internal static unsafe bool IsLoading() => (GenericHelpers.TryGetAddonByName<AtkUnitBase>("FadeBack", out var fb) && fb->IsVisible) || (GenericHelpers.TryGetAddonByName<AtkUnitBase>("FadeMiddle", out var fm) && fm->IsVisible);

        public unsafe bool IsInDuty() => GameMain.Instance()->CurrentContentFinderConditionId > 0;

        public unsafe bool ZoneHasFlight()
        {
            if (Svc.ClientState.LocalPlayer is null) return false;
            var territory = Svc.Data.Excel.GetSheet<TerritoryType>()?.GetRow(Svc.ClientState.TerritoryType);
            return territory?.TerritoryIntendedUse.RowId is 1 or 47 or 49;
        }

        public unsafe bool UseAction(uint id)
        {
            var am = ActionManager.Instance();
            if (am->GetActionStatus(ActionType.Action, id) != 0) return false;
            return am->UseAction(ActionType.Action, id);
        }

        public delegate void SendActionDelegate(ulong targetObjectId, byte actionType, uint actionId, ushort sequence, long a5, long a6, long a7, long a8, long a9);
        [EzHook("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B E9 41 0F B7 D9", detourName: nameof(SendActionDetour), false)]
        public EzHook<SendActionDelegate>? SendActionHook;

        public virtual void SendActionDetour(ulong targetObjectId, byte actionType, uint actionId, ushort sequence, long a5, long a6, long a7, long a8, long a9)
        {
            SendActionHook?.Original(targetObjectId, actionType, actionId, sequence, a5, a6, a7, a8, a9);
        }

        public unsafe delegate bool UseActionDelegate(ActionManager* actionManager, ActionType actionType, uint actionId, ulong targetId, uint extraParam, UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);
        [EzHook("E8 ?? ?? ?? ?? B0 01 EB B6", detourName: "UseActionDetour")]
        public EzHook<UseActionDelegate>? UseActionHook;

        public virtual unsafe bool UseActionDetour(ActionManager* actionManager, ActionType actionType, uint actionId, ulong targetId, uint extraParam, UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
        {
            return UseActionHook!.Original(actionManager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        }


        // API14 controller hooks use the SDK virtual table contract.
        public unsafe Dalamud.Hooking.Hook<T> HookFromVTable<T>(void* vtblAddress, int vfIndex, T detour) where T : Delegate
            => HookFromVTable((nint)vtblAddress, vfIndex, detour);
        public unsafe Dalamud.Hooking.Hook<T> HookFromVTable<T>(nint vtblAddress, int vfIndex, T detour) where T : Delegate
            => Svc.Hook.HookFromAddress(*(nint*)(vtblAddress + vfIndex * 0x08), detour);

    }
}
